using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class TargetReadAuthorizationHttpTests : IClassFixture<TargetReadAuthorizationApiFactory>
{
    private readonly TargetReadAuthorizationApiFactory factory;

    public TargetReadAuthorizationHttpTests(TargetReadAuthorizationApiFactory factory) => this.factory = factory;

    public static TheoryData<string, string> ReadRoutes => new()
    {
        { "deadlocks", "mixed-auditor" },
        { "deadlocks/detail", "mixed-auditor" },
        { "query-performance/status", "mixed-auditor" },
        { "query-performance/top?metric=cpu", "mixed-auditor" },
        { $"query-performance/databases/5/history/{new string('a', 64)}", "mixed-auditor" },
        { $"query-performance/databases/5/plans/{new string('b', 64)}?queryFingerprint={new string('a', 64)}", "mixed-auditor" },
        // Auditor is intentionally an alert reader. CollectorService is not.
        { "alerts/active", "mixed-collector" },
        { "backups", "mixed-auditor" },
        { "sql-agent/failures", "mixed-auditor" },
        { "tempdb", "mixed-auditor" },
        { "tempdb/files", "mixed-auditor" },
        { "availability-groups/replicas", "mixed-auditor" },
        { "availability-groups/databases", "mixed-auditor" },
    };

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task MixedGrantsCannotBorrowAReadRoleFromAnotherTarget(string route, string identity)
    {
        using HttpClient client = CreateClient(identity);
        int readsBefore = factory.Repository.ReadTargets.Count;

        using HttpResponseMessage response = await client.GetAsync(Route(TargetReadAuthorizationApiFactory.TargetB, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(readsBefore, factory.Repository.ReadTargets.Count);
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task TheSameMixedGrantCanReadItsViewerTarget(string route, string identity)
    {
        await AssertReadableAsync(TargetReadAuthorizationApiFactory.TargetA, route, identity);
    }

    [Theory]
    [MemberData(nameof(ReadRoutes))]
    public async Task AViewerGrantOnTheRequestedTargetCanRead(string route, string _)
    {
        await AssertReadableAsync(TargetReadAuthorizationApiFactory.TargetB, route, "viewer-b");
    }

    [Theory]
    [InlineData("mixed-auditor")]
    [InlineData("mixed-security-administrator")]
    public async Task AuditAndSecurityRolesRemainAllowedAlertReaders(string identity)
    {
        await AssertReadableAsync(TargetReadAuthorizationApiFactory.TargetB, "alerts/active", identity);
    }

    [Fact]
    public async Task EveryRuntimeRouteRequiresAnAuthenticatedIdentity()
    {
        using HttpClient client = factory.CreateClient();
        // Start the real host and materialize its mapped endpoint data sources.
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await EndpointAuthorizationAssertions.RequireAuthenticationAsync(factory.Services, webInterfaceConfigured: false);
    }

    private async Task AssertReadableAsync(MonitoredInstanceId target, string route, string identity)
    {
        using HttpClient client = CreateClient(identity);
        int readsBefore = factory.Repository.ReadTargets.Count;
        using HttpResponseMessage response = await client.GetAsync(Route(target, route));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(readsBefore + 1, factory.Repository.ReadTargets.Count);
        Assert.Equal(target, factory.Repository.ReadTargets[^1]);
    }

    private HttpClient CreateClient(string identity)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TargetReadTestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }

    private static string Route(MonitoredInstanceId target, string route)
    {
        if (route == "deadlocks/detail")
            route = $"deadlocks/{DeadlockObservation.ComputeEventId(target, new string('f', 64)):D}";
        return $"/api/v1/observation-targets/{target.Value:D}/{route}";
    }
}

internal static class EndpointAuthorizationAssertions
{
    internal static async Task RequireAuthenticationAsync(IServiceProvider services, bool webInterfaceConfigured)
    {
        RouteEndpoint[] endpoints = services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText?.StartsWith("/mcp/", StringComparison.Ordinal) == true);
        Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/overview");
        if (webInterfaceConfigured)
            Assert.Contains(endpoints, endpoint => endpoint.RoutePattern.RawText == "/assets/{**assetPath}");
        IAuthorizationPolicyProvider provider = services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (RouteEndpoint endpoint in endpoints)
        {
            string route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "unnamed route";
            Assert.True(endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null, $"Anonymous route: {route}");
            IReadOnlyList<IAuthorizeData> metadata = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            // Scaffold descriptors deliberately use the authenticated fallback policy.
            // Diagnostic, administrative, web and MCP endpoints must declare authorization.
            bool scaffold = route is "/health" or "/api/v1/service" || route == "/" && !webInterfaceConfigured;
            if (!scaffold) Assert.True(metadata.Count > 0, $"Missing authorization metadata: {route}");
            AuthorizationPolicy? policy = await AuthorizationPolicy.CombineAsync(
                provider, metadata, endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
            Assert.True(policy?.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any() == true,
                $"Route does not require authentication: {route}");
        }
    }
}

public sealed class TargetReadAuthorizationApiFactory : WebApplicationFactory<Program>
{
    internal static readonly MonitoredInstanceId TargetA = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    internal static readonly MonitoredInstanceId TargetB = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
    internal TargetReadRepository Repository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureTestServices(services =>
        {
            // Keep all four production query services and the production HTTP pipeline.
            services.RemoveAll<IDeadlockProjectionRepositoryPort>();
            services.RemoveAll<IQueryPerformanceApiRepositoryPort>();
            services.RemoveAll<IAlertRepositoryPort>();
            services.RemoveAll<IOperationalHealthRepositoryPort>();
            services.AddSingleton<IDeadlockProjectionRepositoryPort>(Repository);
            services.AddSingleton<IQueryPerformanceApiRepositoryPort>(Repository);
            services.AddSingleton<IAlertRepositoryPort>(Repository);
            services.AddSingleton<IOperationalHealthRepositoryPort>(Repository);
            services.RemoveAll<WindowsGroupRoleResolver>();
            services.AddSingleton(new WindowsGroupRoleResolver([
                Binding(TargetReadTestAuthenticationHandler.ViewerASid, ApplicationRole.Viewer, TargetA),
                Binding(TargetReadTestAuthenticationHandler.ViewerBSid, ApplicationRole.Viewer, TargetB),
                Binding(TargetReadTestAuthenticationHandler.AuditorBSid, ApplicationRole.Auditor, TargetB),
                Binding(TargetReadTestAuthenticationHandler.CollectorBSid, ApplicationRole.CollectorService, TargetB),
                Binding(TargetReadTestAuthenticationHandler.SecurityAdministratorBSid, ApplicationRole.SecurityAdministrator, TargetB),
            ]));
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TargetReadTestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TargetReadTestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TargetReadTestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TargetReadTestAuthenticationHandler>(
                TargetReadTestAuthenticationHandler.SchemeName, static _ => { });
        });
    }

    private static WindowsGroupRoleBinding Binding(string sid, ApplicationRole role, MonitoredInstanceId target) =>
        new(new ActorSecurityIdentifier(sid), [role], false, [target]);
}

internal sealed class TargetReadTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "TargetRead.Tests";
    internal const string IdentityHeader = "X-TargetRead-Test-Identity";
    internal const string ViewerASid = "S-1-5-21-3101";
    internal const string ViewerBSid = "S-1-5-21-3102";
    internal const string AuditorBSid = "S-1-5-21-3103";
    internal const string CollectorBSid = "S-1-5-21-3104";
    internal const string SecurityAdministratorBSid = "S-1-5-21-3105";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());
        string[] groups = values[0] switch
        {
            "mixed-auditor" => [ViewerASid, AuditorBSid],
            "mixed-collector" => [ViewerASid, CollectorBSid],
            "mixed-security-administrator" => [ViewerASid, SecurityAdministratorBSid],
            "viewer-b" => [ViewerBSid],
            _ => [],
        };
        if (groups.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown test identity."));
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-3200") };
        claims.AddRange(groups.Select(group => new Claim(ClaimTypes.GroupSid, group)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal sealed class TargetReadRepository : IDeadlockProjectionRepositoryPort,
    IQueryPerformanceApiRepositoryPort, IAlertRepositoryPort, IOperationalHealthRepositoryPort
{
    private static readonly DateTimeOffset At = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly ObservationTargetRevision Revision = new(1);
    internal List<MonitoredInstanceId> ReadTargets { get; } = [];

    private ValueTask<T> Read<T>(MonitoredInstanceId target, T value)
    {
        ReadTargets.Add(target);
        return ValueTask.FromResult(value);
    }

    public ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksRepositoryRequest request, CancellationToken cancellationToken) =>
        Read<DeadlockPage?>(request.TargetId, new(request.TargetId, At, [], null));

    public ValueTask<DeadlockDetailDto?> GetDeadlockAsync(MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        Read<DeadlockDetailDto?>(targetId, new(new(targetId, eventId, At, new string('f', 64), 0, 0, false, At), [], []));

    public ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(QueryPerformanceStatusRequest request, CancellationToken cancellationToken) =>
        Read<QueryPerformanceStatusDto?>(request.TargetId, new(request.TargetId, At, QueryPerformanceSource.Unavailable,
            "unavailable", QueryCoverage.Unavailable, false, false, false, null, []));

    public ValueTask<TopQueryPage> GetTopAsync(TopQueryRequest request, CancellationToken cancellationToken) =>
        Read(request.TargetId, new TopQueryPage([], false, At));

    public ValueTask<QueryHistoryPage> GetHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken) =>
        Read(request.TargetId, new QueryHistoryPage([], false, At));

    public ValueTask<QueryPlanMetadataDto?> GetPlanAsync(QueryPlanMetadataRequest request, CancellationToken cancellationToken) =>
        Read<QueryPlanMetadataDto?>(request.TargetId, new(request.TargetId, request.Plan, QueryPerformanceSource.QueryStore, At, QueryCoverage.Complete, false));

    public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(MonitoredInstanceId targetId, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<AlertActiveDto>>(targetId, []);

    public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        Read<BackupStatusSnapshot?>(request.TargetId, new(request.TargetId, Revision, null, At, OperationalObservationState.NoData, [], 0, false));

    public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        Read<SqlAgentFailureSnapshot?>(request.TargetId, new(request.TargetId, Revision, null, At, OperationalObservationState.NoData, [], 0, false, request.FromUtc, request.ToUtc));

    public ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        Read<TempDbSnapshot?>(request.TargetId, new(request.TargetId, Revision, null, At, OperationalObservationState.NoData, null, null, null, null, [], false));

    public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        GetTempDbAsync(request, cancellationToken);

    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        Read<AvailabilityGroupsSnapshot?>(request.TargetId, new(request.TargetId, Revision, null, At, OperationalObservationState.NoData,
            AvailabilityVisibilityScope.PrimaryAllKnown, [], [], false));

    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        GetAvailabilityGroupsAsync(request, cancellationToken);

    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest request, CancellationToken cancellationToken) =>
        GetAvailabilityGroupsAsync(request, cancellationToken);

    // Writes and worker operations are outside these read-boundary tests and fail if reached.
    public ValueTask<IReadOnlyList<AlertEvaluationWork>> ClaimDueEvaluationsAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(MonitoredInstanceId targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AlertRuleState?> GetStateAsync(MonitoredInstanceId targetId, Guid ruleId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AlertEvaluationOutcome> EvaluateAndPersistAsync(AlertEvaluationBatch request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AlertRuleWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(MaintenanceWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AlertAcknowledgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AlertDestinationWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAdminAsync(AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<IReadOnlyList<AlertDeliveryWork>> ClaimDueDeliveriesAsync(WorkerLeaseIdentity lease, int limit, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<AlertDeliveryResult> CompleteDeliveryAsync(AlertDeliveryResult result, WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
}
