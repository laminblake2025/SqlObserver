using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

[Collection("M9 operational API")]
public sealed class M9OperationalHealthApiContractTests : IClassFixture<M9OperationalHealthApiFactory>
{
    private readonly M9OperationalHealthApiFactory factory;
    public M9OperationalHealthApiContractTests(M9OperationalHealthApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("backups", "viewer")]
    [InlineData("sql-agent/failures", "viewer")]
    [InlineData("tempdb", "viewer")]
    [InlineData("tempdb/files", "viewer")]
    [InlineData("availability-groups/replicas", "viewer")]
    [InlineData("availability-groups/databases", "viewer")]
    [InlineData("backups", "operator")]
    [InlineData("sql-agent/failures", "operator")]
    [InlineData("tempdb", "operator")]
    [InlineData("tempdb/files", "operator")]
    [InlineData("availability-groups/replicas", "operator")]
    [InlineData("availability-groups/databases", "operator")]
    [InlineData("backups", "administrator")]
    [InlineData("sql-agent/failures", "administrator")]
    [InlineData("tempdb", "administrator")]
    [InlineData("tempdb/files", "administrator")]
    [InlineData("availability-groups/replicas", "administrator")]
    [InlineData("availability-groups/databases", "administrator")]
    public async Task SixRoutesAreProductionHostedAndReturnRouteSpecificBoundedDtos(string route, string identity)
    {
        using HttpClient client = CreateClient(identity);
        HttpResponseMessage response = await client.GetAsync(Route(route));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);
        Assert.True(body.Length < 1_048_576);
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("targetId", body, StringComparison.Ordinal);
        Assert.Contains("targetRevision", body, StringComparison.Ordinal);
        Assert.Contains("hasMore", body, StringComparison.Ordinal);
        Assert.Contains("nextCursor", body, StringComparison.Ordinal);
        if (route == "tempdb") Assert.DoesNotContain("fileId", body, StringComparison.Ordinal);
        if (route == "tempdb/files") Assert.Contains("fileId", body, StringComparison.Ordinal);
        if (route == "availability-groups/replicas") Assert.Contains("replicaFingerprint", body, StringComparison.Ordinal);
        if (route == "availability-groups/databases") Assert.Contains("databaseFingerprint", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("administrator")]
    public async Task ViewerOperatorAndTargetAdministratorCanReadEveryRoute(string identity)
    {
        using HttpClient client = CreateClient(identity);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route("backups"))).StatusCode);
    }

    [Theory]
    [InlineData("backups")]
    [InlineData("sql-agent/failures")]
    [InlineData("tempdb")]
    [InlineData("tempdb/files")]
    [InlineData("availability-groups/replicas")]
    [InlineData("availability-groups/databases")]
    public async Task RoleAndTargetScopeDenialsAreForbidden(string route)
    {
        using HttpClient roleDenied = CreateClient("denied");
        Assert.Equal(HttpStatusCode.Forbidden, (await roleDenied.GetAsync(Route(route))).StatusCode);
        using HttpClient scopeDenied = CreateClient("scope");
        Assert.Equal(HttpStatusCode.Forbidden, (await scopeDenied.GetAsync(Route(route))).StatusCode);
    }

    [Fact]
    public async Task InvalidLimitCursorAndAgentWindowAreBadRequest()
    {
        using HttpClient client = CreateClient("viewer");
        foreach (string query in new[] { "limit=0", "limit=201", "cursor=", $"cursor={new string('x', 1025)}", "cursor=%%%" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route("backups") + "?" + query)).StatusCode);
        string wrongTargetCursor = new OperationalHealthCursor(new MonitoredInstanceId(M9OperationalHealthApiFactory.DriftTarget), new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero), "opaque").Encode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(Route("backups") + "?cursor=" + Uri.EscapeDataString(wrongTargetCursor))).StatusCode);
        factory.Repository.Mode = M9RepositoryMode.CursorDrift;
        string wrongRunCursor = new OperationalHealthCursor(new MonitoredInstanceId(M9OperationalHealthApiFactory.Target), new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero), "run-mismatch").Encode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route("backups") + "?cursor=" + Uri.EscapeDataString(wrongRunCursor))).StatusCode);
        factory.Repository.Mode = M9RepositoryMode.Normal;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route("sql-agent/failures") + "?fromUtc=2026-08-25T00:00:00-04:00&toUtc=2026-08-25T01:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route("sql-agent/failures") + "?fromUtc=2026-08-17T00:00:00Z&toUtc=2026-08-25T01:00:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Route("sql-agent/failures") + "?fromUtc=2026-08-25T01:00:00Z&toUtc=2026-08-25T00:00:00Z")).StatusCode);
    }

    [Fact]
    public async Task AgentDefaultsAndSevenDayUtcWindowAreBounded()
    {
        using HttpClient client = CreateClient("viewer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route("sql-agent/failures"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route("sql-agent/failures") + "?fromUtc=2026-08-18T00:00:00Z&toUtc=2026-08-25T00:00:00Z")).StatusCode);
        Assert.Equal(TimeSpan.Zero, factory.Repository.LastFromUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromDays(7), factory.Repository.LastToUtc!.Value - factory.Repository.LastFromUtc!.Value);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task AgentHttpProjectionDistinguishesJobOutcomesFromStepFailures(int step, bool jobFailure)
    {
        factory.Repository.AgentStepId = step;
        try
        {
            using HttpClient client = CreateClient("viewer");
            using HttpResponseMessage response = await client.GetAsync(Route("sql-agent/failures"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(step, item.GetProperty("stepId").GetInt32());
            Assert.Equal(jobFailure, item.GetProperty("isJobOutcome").GetBoolean());
            Assert.Equal(jobFailure, item.GetProperty("countsAsJobFailure").GetBoolean());
            Assert.Equal("firstObservedUtc", body.RootElement.GetProperty("coverage").GetString());
        }
        finally { factory.Repository.AgentStepId = 1; }
    }

    [Fact]
    public async Task MissingTargetIsHonestNotFoundAndNoDataIsExplicit()
    {
        using HttpClient client = CreateClient("viewer");
        factory.Repository.Mode = M9RepositoryMode.Normal;
        factory.Repository.NoData = false;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/observation-targets/{M9OperationalHealthApiFactory.MissingTarget:D}/backups")).StatusCode);
        factory.Repository.NoData = true;
        HttpResponseMessage response = await client.GetAsync(Route("backups"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"state\":\"NoData\"", body, StringComparison.Ordinal);
        Assert.Contains("\"runId\":null", body, StringComparison.Ordinal);
        Assert.Contains("\"evidence\":null", body, StringComparison.Ordinal);
        factory.Repository.NoData = false;
    }

    [Fact]
    public async Task RepositoryDriftTimeoutAndCancellationAreSafe()
    {
        using HttpClient client = CreateClient("viewer");
        factory.Repository.Mode = M9RepositoryMode.Drift;
        HttpResponseMessage drift = await client.GetAsync(Route("backups"));
        Assert.Equal(HttpStatusCode.Conflict, drift.StatusCode);
        Assert.DoesNotContain("target", await drift.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        factory.Repository.Mode = M9RepositoryMode.Timeout;
        HttpResponseMessage timeout = await client.GetAsync(Route("backups"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, timeout.StatusCode);
        Assert.DoesNotContain("provider", await timeout.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        factory.Repository.Mode = M9RepositoryMode.Delayed;
        HttpResponseMessage deadline = await client.GetAsync(Route("backups"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, deadline.StatusCode);
        Assert.True(factory.Repository.CancellationObserved);
        factory.Repository.Mode = M9RepositoryMode.Normal;
    }

    [Fact]
    public async Task RequestCancellationIsPropagatedThroughTheFifteenSecondEnvelope()
    {
        using HttpClient client = CreateClient("viewer");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Route("backups"), cancellation.Token));
    }

    [Fact]
    public async Task InFlightRequestCancellationReachesRepositoryAfterItHasStarted()
    {
        await using var isolatedFactory = new M9OperationalHealthApiFactory();
        using HttpClient client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(M9AuthenticationHandler.IdentityHeader, "viewer");
        isolatedFactory.Repository.Mode = M9RepositoryMode.Delayed;
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> request = client.GetAsync(Route("backups"), HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await isolatedFactory.Repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await isolatedFactory.Repository.CancellationObservedTask.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(isolatedFactory.Repository.CancellationObserved);
        try { await request; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task AllListSurfacesReturnAProductionCursorAndTerminalSecondPage()
    {
        using HttpClient client = CreateClient("viewer");
        foreach (string route in new[] { "backups", "sql-agent/failures", "tempdb/files", "availability-groups/replicas", "availability-groups/databases" })
        {
            JsonDocument first = JsonDocument.Parse(await (await client.GetAsync(Route(route) + "?limit=1")).Content.ReadAsStringAsync());
            string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
            Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
            JsonElement firstItem = first.RootElement.GetProperty("items")[0];
            JsonDocument second = JsonDocument.Parse(await (await client.GetAsync(Route(route) + "?limit=1&cursor=" + Uri.EscapeDataString(cursor))).Content.ReadAsStringAsync());
            Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Null(second.RootElement.GetProperty("nextCursor").GetString());
            Assert.NotEqual(firstItem.ToString(), second.RootElement.GetProperty("items")[0].ToString());
        }

        JsonDocument summary = JsonDocument.Parse(await (await client.GetAsync(Route("tempdb") + "?limit=1")).Content.ReadAsStringAsync());
        Assert.False(summary.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Null(summary.RootElement.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public void OperationalHealthTimeoutDefaultsToFifteenSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(15), new OperationalHealthServerOptions().RequestTimeout);

    [Fact]
    public async Task OversizedMappedDtoIsRejectedWith413AndNoProviderBody()
    {
        await using var oversizedFactory = new M9OversizedApiFactory();
        using HttpClient client = oversizedFactory.CreateClient();
        client.DefaultRequestHeaders.Add(M9AuthenticationHandler.IdentityHeader, "viewer");
        HttpResponseMessage response = await client.GetAsync(Route("backups"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateClient(string identity)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(M9AuthenticationHandler.IdentityHeader, identity);
        return client;
    }
    private static string Route(string route) => $"/api/v1/observation-targets/{M9OperationalHealthApiFactory.Target:D}/{route}";
}

[CollectionDefinition("M9 operational API", DisableParallelization = true)]
public sealed class M9OperationalApiGroup { }

public sealed class M9OperationalHealthApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    internal static readonly Guid MissingTarget = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    internal static readonly Guid DriftTarget = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    internal M9OperationalHealthRepository Repository { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("ContractTesting").ConfigureTestServices(services =>
    {
        services.RemoveAll<IOperationalHealthRepositoryPort>();
        services.RemoveAll<WindowsGroupRoleResolver>();
        services.AddSingleton<IOperationalHealthRepositoryPort>(Repository);
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M9AuthenticationHandler.ViewerSid), [ApplicationRole.Viewer], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M9AuthenticationHandler.OperatorSid), [ApplicationRole.Operator], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M9AuthenticationHandler.AdminSid), [ApplicationRole.TargetAdministrator], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M9AuthenticationHandler.ScopeSid), [ApplicationRole.Viewer], false, [new MonitoredInstanceId(MissingTarget)]),
        ]));
        services.PostConfigure<OperationalHealthServerOptions>(options => options.RequestTimeout = TimeSpan.FromMilliseconds(50));
        services.AddAuthentication(options => { options.DefaultAuthenticateScheme = M9AuthenticationHandler.SchemeName; options.DefaultChallengeScheme = M9AuthenticationHandler.SchemeName; }).AddScheme<AuthenticationSchemeOptions, M9AuthenticationHandler>(M9AuthenticationHandler.SchemeName, static _ => { });
    });
}

internal enum M9RepositoryMode { Normal, Drift, Timeout, CursorDrift, Delayed }

internal sealed class M9OperationalHealthRepository : IOperationalHealthRepositoryPort
{
    internal M9RepositoryMode Mode { get; set; }
    internal bool NoData { get; set; }
    internal int AgentStepId { get; set; } = 1;
    internal bool CancellationObserved { get; private set; }
    internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CancellationObservedTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal DateTimeOffset? LastFromUtc { get; private set; }
    internal DateTimeOffset? LastToUtc { get; private set; }
    private static readonly DateTimeOffset Observed = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly CollectorRunId Run = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly ObservationTargetRevision Revision = new(1);
    private static T RunCall<T>(OperationalHealthRequest request, Func<T> value) where T : class
    {
        if (request.TargetId.Value == M9OperationalHealthApiFactory.MissingTarget) return null!;
        return value();
    }
    private T Read<T>(OperationalHealthRequest request, Func<T> value, CancellationToken cancellationToken) where T : class
    {
        LastFromUtc = request.FromUtc; LastToUtc = request.ToUtc;
        if (Mode == M9RepositoryMode.Timeout) throw new TimeoutException("repository timeout");
        if (Mode == M9RepositoryMode.CursorDrift && request.Cursor is not null) throw new ArgumentException("cursor run binding drift");
        if (Mode == M9RepositoryMode.Delayed) return Delay<T>(cancellationToken);
        return RunCall(request, () => value());
    }
    private T Delay<T>(CancellationToken cancellationToken) where T : class
    {
        Started.TrySetResult(true);
        try { Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).GetAwaiter().GetResult(); throw new InvalidOperationException(); }
        catch (OperationCanceledException) { CancellationObserved = true; CancellationObservedTask.TrySetResult(true); throw; }
    }
    private MonitoredInstanceId Target(OperationalHealthRequest request) => new(Mode == M9RepositoryMode.Drift ? M9OperationalHealthApiFactory.DriftTarget : request.TargetId.Value);
    private string? Cursor(OperationalHealthRequest request, string route) => NoData || request.Cursor is not null ? null : new OperationalHealthCursor(request.TargetId, Observed, route + "-page-2").Encode();
    public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<BackupStatusSnapshot?>(Read(request, () => new BackupStatusSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, NoData ? [] : [new BackupStatusObservation(Target(request), Revision, new string('a', 64), BackupKind.Full, Observed, null, false, 100, false, true, false, BackupCoverage.Complete) { BackupSetId = request.Cursor is null ? 1 : 2 }], 1, false) { NextCursor = Cursor(request, "backups") }, cancellationToken));
    public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<SqlAgentFailureSnapshot?>(Read(request, () => new SqlAgentFailureSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, NoData ? [] : [new SqlAgentFailureObservation(Target(request), Revision, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), request.Cursor is null ? 1 : 2, AgentStepId, 0, AgentFailureKind.Failed, null, null, 0, 1, new DateTime(2026, 8, 25), TimeSpan.FromMinutes(1), Observed, new string('b', 64))], 1, false, request.FromUtc, request.ToUtc) { NextCursor = Cursor(request, "agent") }, cancellationToken));
    public ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<TempDbSnapshot?>(Read(request, () => new TempDbSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, 100, 50, 50, 25, [], false), cancellationToken));
    public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<TempDbSnapshot?>(Read(request, () => new TempDbSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, null, null, null, null, NoData ? [] : [new TempDbFileObservation(Target(request), Revision, request.Cursor is null ? 1 : 2, 100, 50, 50, TempDbComponentState.Healthy)], false) { NextCursor = Cursor(request, "tempdb-files") }, cancellationToken));
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => GetAvailabilityGroupReplicasAsync(request, cancellationToken);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(Read(request, () => new AvailabilityGroupsSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, NoData ? [] : [new AvailabilityReplicaObservation(Target(request), Revision, new string('c', 64), (request.Cursor is null ? "d" : "e").PadLeft(64, 'd'), "PRIMARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.PrimaryAllKnown, true)], [], false) { NextCursor = Cursor(request, "ag-replicas") }, cancellationToken));
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(Read(request, () => new AvailabilityGroupsSnapshot(Target(request), Revision, NoData ? null : Run, Observed, NoData ? OperationalObservationState.NoData : OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, [], NoData ? [] : [new AvailabilityDatabaseObservation(Target(request), Revision, new string('c', 64), (request.Cursor is null ? "e" : "f").PadLeft(64, 'e'), "SYNCHRONIZED", "ONLINE", AvailabilityVisibilityScope.PrimaryAllKnown, true)], false) { NextCursor = Cursor(request, "ag-databases") }, cancellationToken));
}

internal sealed class M9AuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "M9.Tests"; internal const string IdentityHeader = "X-SqlObserver-M9-Identity";
    internal const string ViewerSid = "S-1-5-21-9101"; internal const string OperatorSid = "S-1-5-21-9102"; internal const string AdminSid = "S-1-5-21-9103"; internal const string ScopeSid = "S-1-5-21-9104";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1) return Task.FromResult(AuthenticateResult.NoResult());
        string? sid = values[0] switch { "viewer" => ViewerSid, "operator" => OperatorSid, "administrator" => AdminSid, "scope" => ScopeSid, "denied" => "S-1-5-21-9199", _ => null };
        if (sid is null) return Task.FromResult(AuthenticateResult.Fail("unknown identity"));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-9200"), new Claim(ClaimTypes.GroupSid, sid) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

internal sealed class M9OversizedApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("ContractTesting").ConfigureTestServices(services =>
    {
        services.RemoveAll<IOperationalHealthQueryService>();
        services.AddSingleton<IOperationalHealthQueryService, M9OversizedQueryService>();
        services.RemoveAll<WindowsGroupRoleResolver>();
        services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M9AuthenticationHandler.ViewerSid), [ApplicationRole.Viewer], true)]));
        services.AddAuthentication(options => { options.DefaultAuthenticateScheme = M9AuthenticationHandler.SchemeName; options.DefaultChallengeScheme = M9AuthenticationHandler.SchemeName; }).AddScheme<AuthenticationSchemeOptions, M9AuthenticationHandler>(M9AuthenticationHandler.SchemeName, static _ => { });
    });
}

internal sealed class M9OversizedQueryService : IOperationalHealthQueryService
{
    private static readonly MonitoredInstanceId Target = new(M9OperationalHealthApiFactory.Target);
    private static readonly ObservationTargetRevision Revision = new(1);
    private static readonly CollectorRunId Run = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    private static readonly DateTimeOffset At = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static BackupStatusSnapshot Huge() => new(Target, Revision, Run, At, OperationalObservationState.Complete, Enumerable.Range(0, 200).Select(i => new BackupStatusObservation(Target, Revision, new string('a', 8_000), BackupKind.Full, At, null, false, 1, false, true, false, BackupCoverage.Complete) { BackupSetId = i }).ToArray(), 200, false);
    public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<BackupStatusSnapshot?>(Huge());
    public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<SqlAgentFailureSnapshot?>(null);
    public ValueTask<TempDbSnapshot?> GetTempDbAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<TempDbSnapshot?>(null);
    public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<TempDbSnapshot?>(null);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(AuthorizationContext a, OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
}
