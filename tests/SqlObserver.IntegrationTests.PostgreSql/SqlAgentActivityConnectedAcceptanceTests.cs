extern alias ServerAssembly;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using Xunit.Abstractions;
using static SqlObserver.IntegrationTests.PostgreSql.SqlAgentActivityPersistenceTests;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Connected synthetic source acceptance, not native SQL Server qualification.
/// Production collector validation, restricted repository admission, root HTTP and built UI are real.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
[Trait("Category", "RequiresAgentConnectedAcceptance")]
public sealed class SqlAgentActivityConnectedAcceptanceTests(PostgreSql18Fixture fixture, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private const string Prefix = "SQLOBSERVER_AGENT_CONNECTED_";
    private const string AcceptanceScheme = "Agent.Connected.Acceptance";

    [Fact]
    public async Task SyntheticSourceThroughProductionCollectorCanonicalRepositoryRootHttpAndBuiltOperations()
    {
        Assert.Equal("1", Required("ACCEPTANCE"));
        string webRoot = Path.GetFullPath(Required("WEB_ROOT")), script = Path.GetFullPath(Required("BROWSER_SCRIPT"));
        Assert.True(File.Exists(Path.Combine(webRoot, "index.html"))); Assert.True(File.Exists(script));
        await using var database = await SqlAgentActivityHistoryFixture.CreateAsync(fixture, seal: true);
        await using (var version = database.DataSource.CreateCommand("SHOW server_version_num"))
            Assert.Equal("180004", await version.ExecuteScalarAsync());
        var target = await SeedAsync(database);
        await using var server = database.CreateServerDataSource();
        await using var collectorData = database.CreateCollectorDataSource();
        await using (var role = server.CreateCommand("SELECT current_user")) Assert.Equal("sqlobserver_server", await role.ExecuteScalarAsync());
        await using (var role = collectorData.CreateCommand("SELECT current_user")) Assert.Equal("sqlobserver_collector", await role.ExecuteScalarAsync());
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        await using var host = new ConnectedFactory(connection, target.Value, webRoot);
        host.UseKestrel(0);
        using var admin = host.Client("admin"); using var viewer = host.Client("viewer"); using var foreign = host.Client("foreign");
        string statePath = $"/api/v1/observation-targets/{target.Value:D}/sql-agent/activity-observations/collection";
        var initial = await ReadStateAsync(viewer, statePath); Assert.False(initial.Enabled);
        var enableBody = new { expectedTargetRevision = 1, expectedScheduleRevision = initial.Revision, expectedEnabled = false, enabled = true };
        using (var denied = await viewer.PutAsJsonAsync(statePath, enableBody)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var unavailable = await admin.PutAsJsonAsync(statePath, enableBody)) Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        var enrollment = await EnrollAsync(database, server, target);
        initial = await ReadStateAsync(admin, statePath);
        using (var enabled = await admin.PutAsJsonAsync(statePath, new { expectedTargetRevision = 1, expectedScheduleRevision = initial.Revision, expectedEnabled = false, enabled = true }))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((await ReadStateAsync(viewer, statePath)).Enabled);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorData);
        var resolver = new PostgreSqlSqlAgentModuleEnrollmentPort(collectorData);
        int delegateReads = 0;
        SqlAgentModuleCaptureResult? originalSource = null;
        var actualCollector = new SqlAgentActivityObservationsCollector(async (request, token) =>
        {
            delegateReads++;
            var current = Assert.IsType<SqlAgentModuleEnrollmentSnapshot>(await resolver.ResolveAsync(request.TargetId, request.TargetRevision, token));
            var currentEnrollment = Assert.IsType<SqlAgentModuleEnrollment>(current.Enrollment);
            Assert.True(enrollment.SameAuthorization(currentEnrollment));
            if (delegateReads == 2) throw new UnauthorizedAccessException("PRIVATE_SYNTHETIC_SOURCE_REFUSAL");
            var now = (await NowAsync(database)).AddSeconds(-2).AddTicks(7);
            var local = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
            var header = new SqlAgentActivityHeader(now, new(1, 1, 0, null), new(9, local.AddHours(-1)));
            var jobs = Enumerable.Range(1, 12).Select(index => new SqlAgentActivityJob(
                Guid.Parse(FormattableString.Invariant($"22222222-2222-4222-8222-{index:x12}")), 9,
                local, null, local, local.AddHours(-1), 2, local)).ToArray();
            originalSource = new(new(request.TargetId, request.TargetRevision, request.RunId, header, jobs,
                new(now.AddSeconds(1), header.Service, header.Session), jobs.Length, false), currentEnrollment);
            return originalSource;
        });
        var successful = await CollectCommitAsync(database, collectorData, runtime, actualCollector, target);
        var source = Assert.IsType<SqlAgentModuleCaptureResult>(originalSource);
        Assert.Equal(CollectorRunOutcome.Succeeded, successful.Result.Outcome);
        Assert.Equal(12, successful.Result.Accounting.SourceRowsRead);
        Assert.Equal(4096 + 12 * 768, successful.Result.Accounting.ResponseBytes);
        Assert.Equal(successful.Result.Accounting.ResponseBytes, successful.Result.Accounting.OutputBytes);
        string original = JsonSerializer.Serialize(source.Capture, Web);
        var failed = await CollectCommitAsync(database, collectorData, runtime, actualCollector, target);
        Assert.Equal(2, delegateReads); Assert.Equal(CollectorRunOutcome.PermissionDenied, failed.Result.Outcome);
        Assert.Equal(CollectorRunReason.RequiredPermissionMissing, failed.Result.Reason); Assert.Equal(0, failed.Result.Payload.ItemCount);
        Assert.True(failed.Completed >= successful.Completed);
        var beforeRevocation = await ReadStateAsync(viewer, statePath); Assert.True(beforeRevocation.Enabled);

        // Revocation uses the production enrollment administration port. Its trigger disables collection;
        // retained reads must remain available and no new source evidence is invented.
        await SqlAgentActivityHistoryFixture.RevokeAsync(server, target);
        var afterRevocation = await ReadStateAsync(viewer, statePath);
        Assert.False(afterRevocation.Enabled); Assert.Equal(beforeRevocation.Revision + 1, afterRevocation.Revision);
        var revoked = await resolver.ResolveAsync(target, new(1), CancellationToken.None);
        Assert.Null(revoked?.Enrollment);
        Assert.DoesNotContain((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
            item => item.TargetId == target && item.CollectorId.Value == "sql-agent.activity-observations");
        await using (var bypass = collectorData.CreateCommand("DELETE FROM telemetry.sql_agent_activity_receipt WHERE run_id=@run"))
        {
            bypass.Parameters.AddWithValue("run", successful.Run.Id.Value);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => bypass.ExecuteNonQueryAsync())).SqlState);
        }
        var from = successful.Completed.AddMinutes(-1);
        var to = failed.Completed.AddSeconds(1);
        string basePath = $"/api/v1/observation-targets/{target.Value:D}/sql-agent/activity/observations";
        string query = $"targetRevision=1&fromUtc={Utc(from)}&toUtc={Utc(to)}";
        string listText = await ReadJsonAsync(viewer, basePath + "?" + query + "&limit=100");
        string exactText = await ReadJsonAsync(viewer, basePath + $"/{successful.Run.Id.Value:D}?" + query + "&sampleTargetRevision=1");
        string failureText = await ReadJsonAsync(viewer, basePath + $"/{failed.Run.Id.Value:D}?" + query + "&sampleTargetRevision=1");
        string missingText = await ReadJsonAsync(viewer, basePath + $"/{Guid.NewGuid():D}?" + query + "&sampleTargetRevision=1");
        using var listed = JsonDocument.Parse(listText); using var exact = JsonDocument.Parse(exactText);
        using var failure = JsonDocument.Parse(failureText); using var missing = JsonDocument.Parse(missingText);
        Assert.Equal(2, listed.RootElement.GetProperty("coverage").GetProperty("runCount").GetInt32());
        Assert.Equal(1, listed.RootElement.GetProperty("coverage").GetProperty("failedRunCount").GetInt32());
        Assert.Equal(failed.Run.Id.Value, listed.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.DoesNotContain("jobId", listText, StringComparison.Ordinal); Assert.DoesNotContain("enrollment", listText, StringComparison.OrdinalIgnoreCase);
        var capture = exact.RootElement.GetProperty("capture"); AssertFlags(exact.RootElement); AssertFlags(capture);
        Assert.Equal("ServiceNotRunning", capture.GetProperty("gap").GetString());
        Assert.Equal(source.Capture.Before.SourceObservedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), capture.GetProperty("before").GetProperty("sourceObservedAtUtc").GetString());
        Assert.Equal("2026-11-01T01:30:00.1234567", capture.GetProperty("jobs")[0].GetProperty("startExecutionLocal").GetString());
        Assert.Equal("2026-11-01T00:30:00.1234567", capture.GetProperty("jobs")[0].GetProperty("stopExecutionLocal").GetString());
        Assert.All(capture.GetProperty("jobs").EnumerateArray(), job => Assert.Equal("Unknown", job.GetProperty("candidateState").GetString()));
        Assert.Equal("collection_failed", failure.RootElement.GetProperty("reason").GetString());
        Assert.Equal(failed.Run.Id.Value, failure.RootElement.GetProperty("receipt").GetProperty("runId").GetGuid());
        Assert.Equal(JsonValueKind.Null, failure.RootElement.GetProperty("capture").ValueKind);
        Assert.Equal("observation_not_retained", missing.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("receipt").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("capture").ValueKind);
        foreach (string text in new[] { listText, exactText, failureText, missingText })
            foreach (string forbidden in new[] { "enrollmentId", "evidenceSha256", "expectedWindowsSid", "definitionSha256", "publicCertificateSha256", "elapsedSeconds", "currentStep", "PRIVATE_SYNTHETIC_SOURCE_REFUSAL" })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        using (var denied = await foreign.GetAsync(basePath + "?" + query)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await foreign.GetAsync(basePath + $"/{successful.Run.Id.Value:D}?" + query + "&sampleTargetRevision=1")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(original, JsonSerializer.Serialize(source.Capture, Web));
        Assert.Equal(0L, await CountAsync(database, "SELECT count(*) FROM telemetry.collection_run WHERE instance_id=@target", ("target", target.Value)));
        await RunBrowserAsync(viewer.BaseAddress!, target.Value, successful.Run.Id.Value, failed.Run.Id.Value, from, to, capture);
        output.WriteLine("AGENT_CONNECTED_SYNTHETIC_ACCEPTANCE_V1:" + JsonSerializer.Serialize(new { targetId = target.Value,
            targetRevision = 1, successRunId = successful.Run.Id.Value, failedRunId = failed.Run.Id.Value,
            sourceObservedAtUtc = source.Capture.Before.SourceObservedAtUtc, completedAtUtc = successful.Completed,
            originalSourcePreserved = true, sealedCanonicalRepository = true, productionCollector = true,
            scopedProductionHttp = true, noReadPortMocks = true, enrollmentRevocationDisabledCollection = true,
            nativeQualification = "Absent", productionAdmitted = false, serviceSessionAssociationQualified = false,
            scope = "Synthetic source delegate and prerequisites -> production collector -> restricted canonical repository -> root HTTP -> compiled Operations" }, Web));
    }

    private sealed record Committed(Run Run, CollectorExecutionResult Result, DateTimeOffset Completed);
    private static async Task<Committed> CollectCommitAsync(RepositoryTestDatabase database, NpgsqlDataSource data,
        PostgreSqlCollectorRuntimeRepositoryPort runtime, SqlAgentActivityObservationsCollector collector, MonitoredInstanceId target)
    {
        // Only timing is advanced by the fixture. Collection enablement was admitted through real scoped HTTP.
        await SqlAgentActivityHistoryFixture.ExecuteAsync(database,
            "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='sql-agent.activity-observations'", ("target", target.Value));
        var work = Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
            item => item.TargetId == target && item.CollectorId.Value == "sql-agent.activity-observations");
        var leases = new PostgreSqlWorkerLeasePort(data);
        var acquired = await leases.AcquireAsync(new(new($"collector/run/sql-agent.activity-observations/{target.Value:N}"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None);
        var lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var run = new Run(work, new(Guid.NewGuid()), lease);
        try
        {
            Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run.Id, lease, Timeout), CancellationToken.None)).Status);
            var profile = new CapabilityProfile(target, work.TargetRevision, new("capability.connection"), 1, 1,
                new(new(17, 0, 1000, 1), new("Synthetic connected acceptance"), SqlServerEngineEdition.Standard, SqlServerPlatform.Windows),
                CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false,
                collector.Manifest.RequiredCapabilities.Select(id => new CapabilityEvidence(id, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
                [], TimeSpan.FromMilliseconds(1), 64, work.RepositoryTimeUtc.AddSeconds(-1), work.RepositoryTimeUtc.AddMinutes(5));
            var request = new CollectorExecutionRequest(run.Id, target, work.TargetRevision, work.ConnectionPolicy, profile, new(1), new(TimeSpan.FromSeconds(5)));
            var clock = Stopwatch.StartNew(); var result = await collector.CollectAsync(request, CancellationToken.None); clock.Stop();
            var summary = new CollectorRunSummary(run.Id, target, work.TargetRevision, work.CollectorId, result.CollectorManifestVersion,
                result.OutputSchemaVersion, result.Outcome, result.Reason, clock.Elapsed, 1, result.Accounting, result.Loss);
            var commit = new CommitCollectorRunRequest(work, summary, result.Payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
            var completed = await runtime.CommitRunAsync(commit, CancellationToken.None);
            Assert.Equal(CollectorRunCommitStatus.Committed, completed.Status);
            Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            return new(run, result, Assert.IsType<DateTimeOffset>(completed.CommittedAtUtc));
        }
        finally { Assert.Equal(LeaseReleaseStatus.Released, await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None)); }
    }
    private static async Task<(bool Enabled, long Revision)> ReadStateAsync(HttpClient client, string path)
    {
        using var json = JsonDocument.Parse(await ReadJsonAsync(client, path + "?targetRevision=1"));
        return (json.RootElement.GetProperty("collectorEnabled").GetBoolean(), json.RootElement.GetProperty("scheduleRevision").GetInt64());
    }
    private static async Task<string> ReadJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore); return await response.Content.ReadAsStringAsync();
    }
    private static void AssertFlags(JsonElement value)
    { Assert.False(value.GetProperty("productionAdmitted").GetBoolean()); Assert.False(value.GetProperty("serviceSessionAssociationQualified").GetBoolean()); Assert.Equal("Absent", value.GetProperty("nativeQualification").GetString()); }
    private static string Required(string key) => Environment.GetEnvironmentVariable(Prefix + key) ?? throw new InvalidOperationException("Explicit connected synthetic acceptance input is required: " + key);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private async Task RunBrowserAsync(Uri address, Guid target, Guid success, Guid failed, DateTimeOffset from, DateTimeOffset to, JsonElement expected)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable(Prefix + "NODE") ?? "node")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(Required("BROWSER_SCRIPT")));
        foreach (var pair in new Dictionary<string, string> { ["API_URL"] = address.ToString(), ["TARGET_ID"] = target.ToString("D"),
            ["TARGET_REVISION"] = "1", ["SUCCESS_RUN_ID"] = success.ToString("D"), ["FAILED_RUN_ID"] = failed.ToString("D"),
            ["FROM_UTC"] = from.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            ["TO_UTC"] = to.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), ["EXPECTED_CAPTURE_JSON"] = expected.GetRawText() })
            start.Environment[Prefix + pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start connected acceptance browser.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        output.WriteLine(await stdout); output.WriteLine(await stderr); Assert.Equal(0, process.ExitCode);
    }
    private sealed class ConnectedFactory(string connection, Guid target, string webRoot) : WebApplicationFactory<ServerAssembly::Program>
    {
        internal HttpClient Client(string identity) { var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Agent-Connected-Identity", identity); return client; }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development"); builder.UseSetting("SqlObserver:DevelopmentAuthentication:Enabled", "false");
            builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
            builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("SqlObserver:WebRootPath", webRoot);
            builder.ConfigureLogging(static logging => logging.ClearProviders());
            foreach (var (index, role) in new[] { (0, "Viewer"), (1, "TargetAdministrator"), (2, "Viewer") })
            {
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:GroupSid", $"S-1-5-21-{2100 + index}");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:AllTargets", "false");
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:TargetIds:0", (index == 2 ? Guid.NewGuid() : target).ToString("D"));
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:Roles:0", role);
            }
            // Authentication is the only replaced host behavior. Program supplies every production port,
            // middleware and hash-validated compiled asset route.
            builder.ConfigureTestServices(services => services.AddAuthentication(options =>
            { options.DefaultAuthenticateScheme = AcceptanceScheme; options.DefaultChallengeScheme = AcceptanceScheme; })
                .AddScheme<AuthenticationSchemeOptions, AcceptanceAuthentication>(AcceptanceScheme, static _ => { }));
        }
    }
    private sealed class AcceptanceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            int group = Request.Headers["X-Agent-Connected-Identity"].ToString() switch { "viewer" => 2100, "admin" => 2101, "foreign" => 2102, _ => 0 };
            if (group == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.PrimarySid, "S-1-5-21-1700"), new(ClaimTypes.GroupSid, $"S-1-5-21-{group}")], AcceptanceScheme));
            return Task.FromResult(AuthenticateResult.Success(new(principal, AcceptanceScheme)));
        }
    }
}
