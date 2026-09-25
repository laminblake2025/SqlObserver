using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class M5ActivityHttpContractTests : IClassFixture<M5ActivityApiFactory>
{
    private readonly M5ActivityApiFactory _factory;

    public M5ActivityHttpContractTests(M5ActivityApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("sessions")]
    [InlineData("requests")]
    [InlineData("waits")]
    [InlineData("waits/history?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-23T18:00:00Z")]
    [InlineData("blocking/current")]
    [InlineData("blocking/history?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-23T18:00:00Z")]
    public async Task AuthorizedActivityRoutesRemainTargetScopedAndBounded(string route)
    {
        using HttpClient client = CreateClient("viewer");
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/{route}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(_factory.Service.CallCount >= 1);
        Assert.Equal(M5ActivityApiFactory.TargetId, _factory.Service.LastTargetId);
        Assert.Equal(25, _factory.Service.LastLimit);
    }

    [Fact]
    public async Task AuthorizedActivityRoutesSerializeRepresentativePages()
    {
        _factory.Service.ReturnPages = true;
        try
        {
            using HttpClient client = CreateClient("viewer");
            foreach (string route in new[] { "sessions", "requests", "waits", "waits/history?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-23T18:00:00Z", "blocking/current", "blocking/history?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-23T18:00:00Z" })
            {
                HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/{route}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                string json = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("+00:00", json);
                Assert.Contains("Z\"", json);
            }
        }
        finally { _factory.Service.ReturnPages = false; }
    }

    [Fact]
    public async Task InvalidNonNullPageFailsSafelyWithoutProviderDetails()
    {
        _factory.Service.ReturnInvalidPage = true;
        try
        {
            using HttpClient client = CreateClient("viewer");
            HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/sessions");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("StackOverflow", await response.Content.ReadAsStringAsync());
            Assert.DoesNotContain("provider", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally { _factory.Service.ReturnInvalidPage = false; }
    }

    [Fact]
    public async Task UnauthenticatedActivityReadIsChallenged()
    {
        using HttpClient client = CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ScopedIdentityCannotReadAnotherTarget()
    {
        using HttpClient client = CreateClient("scoped");
        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WaitHistoryCursorCannotChangeTargetOrWindow()
    {
        using HttpClient client = CreateClient("viewer");
        string from = "2026-08-23T17:00:00Z", to = "2026-08-23T18:00:00Z";
        string payload = string.Join('\n', "vh", M5ActivityApiFactory.TargetId.ToString("N"),
            "2026-08-23T17:00:00.0000000+00:00", "2026-08-23T18:00:00.0000000+00:00",
            "2026-08-23T17:59:00.0000000+00:00",
            "cccccccccccc4ccc8ccccccccccccccc", "LCK_M_S");
        string cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        HttpResponseMessage changedWindow = await client.GetAsync(
            $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/waits/history?fromUtc=2026-08-23T16:00:00Z&toUtc={to}&cursor={cursor}");
        Assert.Equal(HttpStatusCode.BadRequest, changedWindow.StatusCode);
        HttpResponseMessage changedTarget = await client.GetAsync(
            $"/api/v1/observation-targets/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/activity/waits/history?fromUtc={from}&toUtc={to}&cursor={cursor}");
        Assert.Equal(HttpStatusCode.BadRequest, changedTarget.StatusCode);
    }

    [Fact]
    public async Task WaitTrendRequiresTheExactTargetRoleAndPreservesGaps()
    {
        using HttpClient viewer = CreateClient("viewer");
        const string window = "?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-23T18:00:00Z";
        string path = $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/waits/trend{window}";
        int before = _factory.Trends.Calls;
        HttpResponseMessage allowed = await viewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        string body = await allowed.Content.ReadAsStringAsync();
        Assert.Contains("\"waitMilliseconds\":\"25\"", body, StringComparison.Ordinal);
        Assert.Contains("\"waitMilliseconds\":null", body, StringComparison.Ordinal);
        Assert.Equal(before + 1, _factory.Trends.Calls);

        using HttpClient scoped = CreateClient("scoped");
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.GetAsync(
            $"/api/v1/observation-targets/{M5ActivityApiFactory.TargetId:D}/activity/waits/trend?fromUtc=2026-08-23T17:00:00Z&toUtc=2026-08-25T18:00:00Z")).StatusCode);
        Assert.Equal(before + 1, _factory.Trends.Calls);
    }

    private HttpClient CreateClient(string? identity = null)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
}

public sealed class M5ActivityApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid TargetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal FakeActivityQueryService Service { get; } = new();
    internal WaitTrendRepositoryProbe Trends { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IActivityProjectionQueryService>();
            services.RemoveAll<IServerWaitTrendRepositoryPort>();
            services.RemoveAll<IServerWaitTrendQueryService>();
            services.RemoveAll<WindowsGroupRoleResolver>();
            services.AddSingleton<IActivityProjectionQueryService>(Service);
            services.AddSingleton<IServerWaitTrendRepositoryPort>(Trends);
            services.AddSingleton<IServerWaitTrendQueryService, ServerWaitTrendQueryService>();
            services.AddSingleton(new WindowsGroupRoleResolver([
                new WindowsGroupRoleBinding(
                    new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid),
                    [ApplicationRole.Viewer], allTargets: true),
                new WindowsGroupRoleBinding(
                    new ActorSecurityIdentifier(TestAuthenticationHandler.ScopedGroupSid),
                    [ApplicationRole.Viewer], allTargets: false,
                    [new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))]),
            ]));
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, static _ => { });
        });
    }
}

internal sealed class WaitTrendRepositoryProbe : IServerWaitTrendRepositoryPort
{
    public int Calls { get; private set; }

    public ValueTask<ServerWaitTrendPage?> ReadAsync(ServerWaitTrendRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        string[] categories = ["Lock", "I/O", "CPU/signal", "Memory", "Parallelism", "Log", "Other"];
        ServerWaitTrendPoint[] points = categories.Select(category => new ServerWaitTrendPoint(
            request.ToUtc.AddMinutes(-10), category, category == "Lock" ? 25m : 0m,
            1, 0, 0, 0, 1)).Concat(categories.Select(category => new ServerWaitTrendPoint(
            request.ToUtc.AddMinutes(-5), category, null, 1, 1, 0, 0, 0))).ToArray();
        return ValueTask.FromResult<ServerWaitTrendPage?>(new ServerWaitTrendPage(request.TargetId,
            request.FromUtc, request.ToUtc, request.ToUtc, points));
    }
}

internal sealed class FakeActivityQueryService : IActivityProjectionQueryService
{
    private static readonly DateTimeOffset RepositoryAt = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = RepositoryAt.AddMinutes(-1);
    internal bool ReturnPages { get; set; }
    internal bool ReturnInvalidPage { get; set; }
    internal int CallCount { get; private set; }
    internal Guid LastTargetId { get; private set; }
    internal int LastLimit { get; private set; }

    public ValueTask<ActivitySessionPage?> ListSessionsAsync(ListActivitySessionsQuery query, CancellationToken cancellationToken) => ReturnPages || ReturnInvalidPage ? ValueTask.FromResult<ActivitySessionPage?>(SessionPage(query)) : Missing<ActivitySessionPage>(query.Authorization, query.TargetId, query.MaxResults);
    public ValueTask<ActivityRequestPage?> ListRequestsAsync(ListActivityRequestsQuery query, CancellationToken cancellationToken) => ReturnPages ? ValueTask.FromResult<ActivityRequestPage?>(RequestPage(query)) : Missing<ActivityRequestPage>(query.Authorization, query.TargetId, query.MaxResults);
    public ValueTask<ServerWaitSummaryPage?> ListWaitSummaryAsync(ListServerWaitSummaryQuery query, CancellationToken cancellationToken) => ReturnPages ? ValueTask.FromResult<ServerWaitSummaryPage?>(WaitPage(query)) : Missing<ServerWaitSummaryPage>(query.Authorization, query.TargetId, query.MaxResults);
    public ValueTask<ServerWaitHistoryPage?> ListServerWaitHistoryAsync(ListServerWaitHistoryQuery query, CancellationToken cancellationToken) => ReturnPages ? ValueTask.FromResult<ServerWaitHistoryPage?>(WaitHistoryPage(query)) : Missing<ServerWaitHistoryPage>(query.Authorization, query.TargetId, query.MaxResults);
    public ValueTask<CurrentBlockingPage?> ListCurrentBlockingAsync(ListCurrentBlockingQuery query, CancellationToken cancellationToken) => ReturnPages ? ValueTask.FromResult<CurrentBlockingPage?>(BlockingPage(query)) : Missing<CurrentBlockingPage>(query.Authorization, query.TargetId, query.MaxResults);
    public ValueTask<BlockingHistoryPage?> ListBlockingHistoryAsync(ListBlockingHistoryQuery query, CancellationToken cancellationToken) => ReturnPages ? ValueTask.FromResult<BlockingHistoryPage?>(HistoryPage(query)) : Missing<BlockingHistoryPage>(query.Authorization, query.TargetId, query.MaxResults);

    private static ActivitySnapshotEvidence Evidence(string collector) => new(new MonitoredInstanceId(M5ActivityApiFactory.TargetId), new CollectorRunId(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc")), new ObservationTargetRevision(1), new CollectorId(collector), CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, CollectorLossEvidence.None, ObservedAt);
    private ActivitySessionPage SessionPage(ListActivitySessionsQuery query) => new(query.TargetId, Evidence(ReturnInvalidPage ? "activity.requests" : "activity.sessions"), [new ActivitySessionSnapshotItem(1, ActivitySessionStatus.Running, true, 5, 0, 1, 2, 3, 4, 5, 6, ObservedAt)], null, RepositoryAt);
    private static ActivityRequestPage RequestPage(ListActivityRequestsQuery query) => new(query.TargetId, Evidence("activity.requests"), [new ActivityRequestSnapshotItem(1, 1, ActivityRequestStatus.Running, ActivityRequestCommand.Select, 5, 1, 2, 3, 4, 5, 6, 25, ObservedAt)], null, RepositoryAt);
    private static ServerWaitSummaryPage WaitPage(ListServerWaitSummaryQuery query) => new(query.TargetId, Evidence("waits.server"), null, [new ServerWaitSummaryItem(new SqlServerWaitType("LCK_M_S"), 1, 2, 3, 1, false, false, null, null, null, ObservedAt)], null, RepositoryAt);
    private static ServerWaitHistoryPage WaitHistoryPage(ListServerWaitHistoryQuery query) => new(query.TargetId, query.FromUtc, query.ToUtc, [new ServerWaitHistoryItem(Evidence("waits.server"), null, new ServerWaitSummaryItem(new SqlServerWaitType("LCK_M_S"), 1, 2, 3, 1, false, false, null, null, null, ObservedAt))], null, RepositoryAt);
    private static CurrentBlockingPage BlockingPage(ListCurrentBlockingQuery query) => new(query.TargetId, Evidence("blocking.current"), [new BlockingEdgeSnapshotItem(1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK_M_S"), 1, 2, 2, 1, BlockingChainState.Resolved, ObservedAt)], null, RepositoryAt);
    private static BlockingHistoryPage HistoryPage(ListBlockingHistoryQuery query) => new(query.TargetId, query.FromUtc, query.ToUtc, [new BlockingHistoryItem(Evidence("blocking.current"), new BlockingEdgeSnapshotItem(1, BlockingBlockerKind.Session, 2, new SqlServerWaitType("LCK_M_S"), 1, 2, 2, 1, BlockingChainState.Resolved, ObservedAt))], null, RepositoryAt);

    private ValueTask<T?> Missing<T>(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit) where T : class
    {
        if (!authorization.CanAccess(ApplicationRole.Viewer, targetId)) throw new UnauthorizedAccessException();
        CallCount++;
        LastTargetId = targetId.Value;
        LastLimit = limit;
        return ValueTask.FromResult<T?>(null);
    }
}
