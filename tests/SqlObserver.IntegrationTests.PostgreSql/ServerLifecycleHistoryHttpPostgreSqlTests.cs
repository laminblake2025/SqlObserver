extern alias ServerAssembly;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class ServerLifecycleHistoryPostgreSqlTests
{
    [Theory]
    [InlineData("server_lifecycle_receipts")]
    [InlineData("server_lifecycle_events")]
    public async Task ProductionHttpLifecycleRetentionUsesGlobalAuthorizationBoundsAndNativeRevisionHistory(string dataClass)
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database), other = await SeedAsync(database);
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        string url = "/api/v1/retention/policies/" + dataClass;
        using HttpClient security = host.Client("security"), viewer = host.Client("viewer");
        using HttpResponseMessage denied = await viewer.PutAsJsonAsync(url, new
        { dataClass, enabled = true, retainFor = TimeSpan.FromDays(90), minimumPartitionsToKeep = 3, expectedRevision = 1, changeReason = "Review lifecycle retention" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        foreach ((int days, int floor) in new[] { (89, 3), (90, 2), (3651, 3), (90, 10001) })
        {
            using HttpResponseMessage invalid = await security.PutAsJsonAsync(url, new
            { dataClass, enabled = true, retainFor = TimeSpan.FromDays(days), minimumPartitionsToKeep = floor, expectedRevision = 1, changeReason = "Review lifecycle retention" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using HttpResponseMessage enabled = await security.PutAsJsonAsync(url, new
        { dataClass, enabled = true, retainFor = TimeSpan.FromDays(90), minimumPartitionsToKeep = 3, expectedRevision = 1, changeReason = "Review lifecycle retention" });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        using HttpResponseMessage read = await security.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using JsonDocument policy = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.True(policy.RootElement.GetProperty("policy").GetProperty("enabled").GetBoolean());
        Assert.Equal(2, policy.RootElement.GetProperty("revision").GetInt64());
        using HttpResponseMessage stale = await security.PutAsJsonAsync(url, new
        { dataClass, enabled = false, retainFor = (TimeSpan?)null, minimumPartitionsToKeep = 3, expectedRevision = 1, changeReason = "Review lifecycle retention" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(1L, await CountAsync(database, $"SELECT count(*) FROM system.retention_policy_history WHERE data_class='{dataClass}' AND change_reason='Review lifecycle retention';"));
    }

    [Fact]
    public async Task ProductionHttpPagesRealSealedReceiptsAndReadsAnIndependentExactProofWithoutPrecisionLoss()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database), other = await SeedAsync(database);
        Window window = await WindowAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run? prior = null;
        for (int index = 0; index < 6; index++)
        {
            Run run = await BeginAsync(database, collector, runtime, target, prior?.Lease);
            var engine = new ServerLifecycleEngineEvidence(Node, Logical, 10 + index,
                LocalStart.AddMinutes(index), 100 + index, long.MaxValue);
            await CommitAsync(runtime, Commit(run, Snapshot(run, engine)));
            prior = run;
        }
        Guid[] native = await NativeEventIdsAsync(database, target);
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        using HttpClient client = host.Client("viewer");
        string url = NativeListUrl(target, window) + "&limit=2";
        var received = new List<Guid>();
        string? cursor = null, epoch = null, cutoff = null;
        do
        {
            using HttpResponseMessage response = await client.GetAsync(url +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.Private);
            Assert.True(response.Headers.CacheControl.NoStore);
            Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement body = json.RootElement;
            epoch ??= body.GetProperty("readEpoch").GetString();
            cutoff ??= body.GetProperty("cutoffUtc").GetString();
            Assert.Equal(epoch, body.GetProperty("readEpoch").GetString());
            Assert.Equal(cutoff, body.GetProperty("cutoffUtc").GetString());
            Assert.Equal(5, body.GetProperty("coverage").GetProperty("eventCount").GetInt32());
            Assert.False(body.TryGetProperty("nextAnchor", out _));
            received.AddRange(body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("eventId").GetGuid()));
            cursor = body.GetProperty("nextCursor").GetString();
        } while (cursor is not null);
        Assert.Equal(native, received);
        Assert.Equal(5, received.Distinct().Count());

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var reader = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        ServerLifecycleHistoryPage page = Assert.IsType<ServerLifecycleHistoryPage>(await reader.ListAsync(List(target, window), CancellationToken.None));
        ServerLifecycleHistoryEvent selected = page.Items[^1];
        using HttpResponseMessage detailResponse = await client.GetAsync(NativeExactUrl(target, window, selected));
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using JsonDocument detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        JsonElement exact = detail.RootElement;
        Assert.Equal(selected.EventId, exact.GetProperty("event").GetProperty("eventId").GetGuid());
        Assert.Null(exact.GetProperty("event").GetProperty("occurredAtUtc").GetString());
        Assert.Equal("9223372036854775807", exact.GetProperty("beforeEngine").GetProperty("currentMsTicks").GetString());
        Assert.Equal(LocalStart.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            exact.GetProperty("afterEngine").GetProperty("sourceStartLocal").GetString());
        Assert.EndsWith("Z", exact.GetProperty("event").GetProperty("detectedAtUtc").GetString(), StringComparison.Ordinal);
        Assert.False(exact.TryGetProperty("coverage", out _));
        Assert.False(exact.TryGetProperty("sourceConsistencyVerified", out _));
        Assert.False(exact.TryGetProperty("availabilityQualification", out _));
        Assert.Equal("complete", exact.GetProperty("afterEngine").GetProperty("coverage").GetString());
    }

    [Fact]
    public async Task ProductionHttpAuthorizationScopesRealRepositoryReadsAndNeverCombinesMixedTargetGrants()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database), other = await SeedAsync(database);
        Window window = await WindowAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run first = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(first, Snapshot(first)));
        Run next = await BeginAsync(database, collector, runtime, target, first.Lease);
        await CommitAsync(runtime, Commit(next, Snapshot(next, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)))));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var reader = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var item = Assert.Single(Assert.IsType<ServerLifecycleHistoryPage>(await reader.ListAsync(List(target, window), CancellationToken.None)).Items);
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        using HttpClient anonymous = host.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(NativeListUrl(target, window))).StatusCode);
        foreach (string identity in new[] { "viewer", "operator", "admin" })
        {
            using HttpClient allowed = host.Client(identity);
            Assert.Equal(HttpStatusCode.OK, (await allowed.GetAsync(NativeListUrl(target, window))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await allowed.GetAsync(NativeExactUrl(target, window, item))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await allowed.GetAsync(NativeListUrl(other, window))).StatusCode);
        }
        foreach (string identity in new[] { "mixed", "auditor", "security" })
        {
            using HttpClient denied = host.Client(identity);
            Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(NativeListUrl(target, window))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(NativeExactUrl(target, window, item))).StatusCode);
        }
    }

    [Fact]
    public async Task ProductionHttpFailedSourceReceiptsRemainUnavailableWithoutHealthOrRetirementClaims()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database), other = await SeedAsync(database);
        Window window = await WindowAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run failed = await BeginAsync(database, collector, runtime, target);
        await CommitAsync(runtime, Commit(failed, null, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing));
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        using HttpClient client = host.Client("viewer");
        using HttpResponseMessage response = await client.GetAsync(NativeListUrl(target, window));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement body = json.RootElement, coverage = body.GetProperty("coverage");
        Assert.Equal("unavailable", body.GetProperty("state").GetString());
        Assert.Equal("source_visibility_gap", body.GetProperty("reason").GetString());
        Assert.Equal(1, coverage.GetProperty("failedRunCount").GetInt32());
        Assert.Equal(0, coverage.GetProperty("retiredRunCount").GetInt32());
        Assert.Equal(0, coverage.GetProperty("retainedSnapshotCount").GetInt32());
        Assert.True(coverage.GetProperty("hasVisibilityGap").GetBoolean());
        Assert.Equal("unqualified", coverage.GetProperty("availabilityQualification").GetString());
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ProductionHttpContinuationRejectsRealCadenceChangeWhileExactCopiedProofRemainsAvailable()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database), other = await SeedAsync(database);
        Window window = await WindowAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Run? previous = null;
        for (int index = 0; index < 3; index++)
        {
            Run run = await BeginAsync(database, collector, runtime, target, previous?.Lease);
            await CommitAsync(runtime, Commit(run, Snapshot(run, Engine(pid: 10 + index, ticks: 100 + index, start: LocalStart.AddMinutes(index)))));
            previous = run;
        }
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        using HttpClient client = host.Client("viewer");
        string firstUrl = NativeListUrl(target, window) + "&limit=1";
        using HttpResponseMessage firstResponse = await client.GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using JsonDocument first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        string token = first.RootElement.GetProperty("nextCursor").GetString()!;
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var reader = new PostgreSqlServerLifecycleHistoryRepositoryPort(server);
        var item = Assert.Single(Assert.IsType<ServerLifecycleHistoryPage>(await reader.ListAsync(List(target, window, 1), CancellationToken.None)).Items);
        string exactUrl = NativeExactUrl(target, window, item);
        using HttpResponseMessage beforeResponse = await client.GetAsync(exactUrl);
        Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);
        using JsonDocument before = JsonDocument.Parse(await beforeResponse.Content.ReadAsStringAsync());
        await ExecuteAsync(database, """
            UPDATE control.collector_schedule SET collection_interval=interval '1 minute',
              schedule_revision=schedule_revision+1,updated_at=clock_timestamp()
            WHERE instance_id=@target AND collector_id='server.lifecycle';
            """, ("target", target));
        using HttpResponseMessage refusal = await client.GetAsync(firstUrl + "&cursor=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await refusal.Content.ReadAsStringAsync());
        Assert.Equal("history_context_changed", problem.RootElement.GetProperty("code").GetString());
        using HttpResponseMessage afterResponse = await client.GetAsync(exactUrl);
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        using JsonDocument after = JsonDocument.Parse(await afterResponse.Content.ReadAsStringAsync());
        foreach (string property in new[] { "event", "beforeEngine", "afterEngine", "readEpoch" })
            Assert.Equal(before.RootElement.GetProperty(property).GetRawText(), after.RootElement.GetProperty(property).GetRawText());
    }

    private static string NativeTimestamp(DateTimeOffset value) => Uri.EscapeDataString(
        value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static string NativeListUrl(Guid target, Window window) =>
        $"/api/v1/observation-targets/{target:D}/lifecycle/history?targetRevision=1&fromUtc={NativeTimestamp(window.FromUtc)}&toUtc={NativeTimestamp(window.ToUtc)}";
    private static string NativeExactUrl(Guid target, Window window, ServerLifecycleHistoryEvent item) =>
        $"/api/v1/observation-targets/{target:D}/lifecycle/history/{item.EventId:D}?targetRevision=1&fromUtc={NativeTimestamp(window.FromUtc)}&toUtc={NativeTimestamp(window.ToUtc)}&sampleTargetRevision={item.SampleTargetRevision}&afterRunId={item.AfterRunId:D}&detectedAtUtc={NativeTimestamp(item.DetectedAtUtc)}";
}

internal sealed class ServerLifecycleNativeHttpFactory(string connection, Guid target, Guid other)
    : WebApplicationFactory<ServerAssembly::Program>
{
    internal HttpClient Client(string? identity)
    {
        HttpClient client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Lifecycle-Native-Identity", identity);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", new NpgsqlConnectionStringBuilder(connection)
        { Options = "-c role=sqlobserver_server", Pooling = false }.ConnectionString);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        string[] roles = ["Viewer", "Operator", "TargetAdministrator", "Viewer", "Auditor", "SecurityAdministrator"];
        for (int index = 0; index < roles.Length; index++)
        {
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:GroupSid", ServerLifecycleNativeAuthentication.Group(index));
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:Roles:0", roles[index]);
            builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:AllTargets", index == 5 ? "true" : "false");
            if (index != 5)
                builder.UseSetting($"SqlObserver:Authorization:Bindings:{index}:TargetIds:0", (index == 3 ? other : target).ToString("D"));
        }
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = ServerLifecycleNativeAuthentication.SchemeName;
            options.DefaultChallengeScheme = ServerLifecycleNativeAuthentication.SchemeName;
            options.DefaultForbidScheme = ServerLifecycleNativeAuthentication.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, ServerLifecycleNativeAuthentication>(ServerLifecycleNativeAuthentication.SchemeName, static _ => { }));
    }
}

internal sealed class ServerLifecycleNativeAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Lifecycle.Native.Http.Tests";
    internal static string Group(int index) => "S-1-5-21-6500-" + (index + 1).ToString(CultureInfo.InvariantCulture);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? identity = Request.Headers["X-Lifecycle-Native-Identity"];
        if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
        int group = identity switch { "viewer" => 0, "operator" => 1, "admin" => 2, "mixed" => 3, "auditor" => 4, "security" => 5, _ => -1 };
        if (group < 0) return Task.FromResult(AuthenticateResult.Fail("Unknown fixture identity."));
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "S-1-5-21-6500-100"), new(ClaimTypes.GroupSid, Group(group)) };
        if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid, Group(4)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}
