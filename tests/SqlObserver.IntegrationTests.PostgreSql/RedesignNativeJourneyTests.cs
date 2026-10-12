extern alias ServerAssembly;

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.IntegrationTests.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

// Opt-in native qualification; explicit environment traits keep ordinary sweeps
// from selecting it, and an explicit selection fails if its contract is missing.
[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class RedesignNativeJourneyTests(PostgreSql18Fixture fixture)
{
    [Fact]
    [Trait("Category", "NativeRedesign")]
    [Trait("Category", "RequiresPostgreSql")]
    [Trait("Category", "RequiresSqlServer")]
    public async Task ActualCoreCollectorPersistsThroughProductionApiAndRenderedDashboard()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SQLOBSERVER_REDESIGN_NATIVE"));
        string root = Environment.GetEnvironmentVariable("SQLOBSERVER_REDESIGN_ROOT") ?? throw new InvalidOperationException("Workspace root is required.");
        string output = Path.Combine(root, ".artifacts", "ui-redesign-20261009", "native");
        Directory.CreateDirectory(output);
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migration.HasFailures);
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,instance_name,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'LOCAL NATIVE SQL LAB','localhost','SQLEXPRESS',interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            """))
        {
            seed.Parameters.AddWithValue("target", target.Value); seed.Parameters.AddWithValue("key", $"redesign.lab.{target.Value:N}");
            Assert.Equal(1, await seed.ExecuteNonQueryAsync());
        }
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None))
            .Items.Single(item => item.TargetId == target && item.CollectorId.Value == "engine.core");
        var run = new CollectorRunId(Guid.NewGuid());
        Guid owner = Guid.NewGuid(); string leaseKey = $"collector/run/engine.core/{target.Value:N}";
        WorkerLeaseIdentity lease;
        await using (var acquire = database.DataSource.CreateCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');"))
        {
            acquire.Parameters.AddWithValue("key", leaseKey); acquire.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader reader = await acquire.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0));
            lease = new(new WorkerLeaseKey(leaseKey), new WorkerExecutionId(owner), new FencingToken(reader.GetInt64(1)));
        }
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None)).Status);
        var source = new RedesignLabConnectionFactory();
        int major; bool encrypted; bool sysadmin; SqlServerAuthenticationScheme authentication;
        await using (SqlConnection connection = new(SqlServerLabContract.ConnectionString))
        {
            await connection.OpenAsync(); major = int.Parse(connection.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
            await using var probe = new SqlCommand("SELECT auth_scheme,encrypt_option,IS_SRVROLEMEMBER('sysadmin') FROM sys.dm_exec_connections WHERE session_id=@@SPID;", connection);
            await using SqlDataReader reader = await probe.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.True(Enum.TryParse(reader.GetString(0), true, out authentication));
            encrypted = reader.GetString(1).Equals("TRUE", StringComparison.OrdinalIgnoreCase); sysadmin = reader.GetInt32(2) == 1;
        }
        var instant = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(instant.Ticks - instant.Ticks % 10, TimeSpan.Zero);
        var profile = new CapabilityProfile(target, work.TargetRevision, new CollectorId("capability.connection"), 1, 1,
            new SqlServerIdentity(new SqlServerVersion(major, 0, 0, 0), new SqlServerEditionName("Local lab"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
            sysadmin || !encrypted ? CapabilityDiscoveryOutcome.SecurityPolicyRejected : authentication == SqlServerAuthenticationScheme.Kerberos ? CapabilityDiscoveryOutcome.Supported : CapabilityDiscoveryOutcome.Degraded,
            sysadmin ? CapabilityDiscoveryReason.ExcessivePrivilege : !encrypted ? CapabilityDiscoveryReason.TransportNotEncrypted : authentication == SqlServerAuthenticationScheme.Kerberos ? CapabilityDiscoveryReason.Verified : CapabilityDiscoveryReason.AuthenticationSchemeFallback,
            authentication, encrypted, sysadmin, [], [], TimeSpan.FromMilliseconds(1), 1, now, now.AddMinutes(5));
        var collector = new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), source);
        var timer = Stopwatch.StartNew();
        CollectorExecutionResult collected = await collector.CollectAsync(new CollectorExecutionRequest(run, target, work.TargetRevision,
            new SqlServerConnectionPolicy(SqlServerLabContract.Endpoint, new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))), profile,
            new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(15))), CancellationToken.None);
        timer.Stop(); Assert.Equal(CollectorRunOutcome.Succeeded, collected.Outcome); Assert.False(collected.Loss.HasLoss);
        var summary = new CollectorRunSummary(run, target, work.TargetRevision, collected.CollectorId, collected.CollectorManifestVersion,
            collected.OutputSchemaVersion, collected.Outcome, collected.Reason, timer.Elapsed, 1, collected.Accounting, collected.Loss);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, collected.Payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None)).Status);
        double connections = collected.Payload.Metrics.Single(sample => sample.MetricId.Value == "engine.user_connections").Value;
        var to = DateTimeOffset.UtcNow.AddMilliseconds(1); var from = to.AddHours(-1);
        string fromUtc = from.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture); string toUtc = to.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string receiptPath = Path.Combine(output, $"browser-{run.Value:N}.json");
        await using var factory = new RedesignNativeFactory(database.ConnectionString, target.Value, Path.Combine(root, "web", "dist"));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 4359));
        using HttpClient client = factory.CreateClient(); client.DefaultRequestHeaders.Add(RedesignNativeAuthentication.Header, "viewer");
        using HttpResponseMessage response = await client.GetAsync($"/api/v1/overview?targetId={target.Value:D}&fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string api = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(api);
        JsonElement evidence = Assert.Single(document.RootElement.GetProperty("evidence").EnumerateArray());
        JsonElement series = evidence.GetProperty("series").EnumerateArray().Single(item => item.GetProperty("metric").GetString() == "engine.user_connections");
        Assert.Equal(connections, Assert.Single(series.GetProperty("points").EnumerateArray()).GetProperty("value").GetDouble());
        await File.WriteAllTextAsync(Path.Combine(output, "api-overview.json"), api);
        await File.WriteAllTextAsync(Path.Combine(output, "binding.json"), JsonSerializer.Serialize(new { targetId = target.Value, runId = run.Value, fromUtc, toUtc, connections, receiptPath, sourceRows = collected.Accounting.SourceRowsRead, outputItems = collected.Payload.ItemCount,
            authentication = authentication.ToString(), encrypted, sysadmin, admissionQualified = false,
            dashboardManifestSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "web", "dist", ".vite", "manifest.json")))),
            qualification = "Direct passive core collector test seam only; local certificate contract and test viewer authentication. Production collector admission, least-privilege operation, and other diagnostic sources are unqualified." }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!File.Exists(receiptPath)) await Task.Delay(200, deadline.Token);
        using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, deadline.Token));
        Assert.Equal(run.Value, receipt.RootElement.GetProperty("runId").GetGuid());
        Assert.Equal(target.Value, receipt.RootElement.GetProperty("targetId").GetGuid());
        Assert.Equal(connections, receipt.RootElement.GetProperty("connections").GetDouble());
        Assert.True(receipt.RootElement.GetProperty("rendered").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "web", "dist", ".vite", "manifest.json")))), receipt.RootElement.GetProperty("dashboardManifestSha256").GetString());
    }
}

internal sealed class RedesignLabConnectionFactory : ISqlServerConnectionFactory
{
    public async ValueTask<SqlConnection> OpenConnectionAsync(SqlServerConnectionPolicy policy, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class RedesignNativeFactory(string connection, Guid target, string webRoot) : WebApplicationFactory<ServerAssembly::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        builder.UseSetting("SqlObserver:WebRootPath", webRoot);
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:GroupSid", RedesignNativeAuthentication.Group);
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:AllTargets", "false");
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:TargetIds:0", target.ToString("D"));
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:Roles:0", "Viewer");
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = RedesignNativeAuthentication.SchemeName; options.DefaultChallengeScheme = RedesignNativeAuthentication.SchemeName;
            options.DefaultForbidScheme = RedesignNativeAuthentication.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, RedesignNativeAuthentication>(RedesignNativeAuthentication.SchemeName, _ => { }));
    }
}

internal sealed class RedesignNativeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Redesign.LocalLab";
    internal const string Header = "X-Redesign-Lab";
    internal const string Group = "S-1-5-21-9349";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!IPAddress.IsLoopback(Context.Connection.RemoteIpAddress ?? IPAddress.None) || Request.Headers[Header] != "viewer")
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Group), new Claim(ClaimTypes.GroupSid, Group)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
