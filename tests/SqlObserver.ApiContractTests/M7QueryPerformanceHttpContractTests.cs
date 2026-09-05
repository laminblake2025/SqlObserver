using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class M7QueryPerformanceHttpContractTests : IClassFixture<M7QueryPerformanceApiFactory>
{
    private readonly M7QueryPerformanceApiFactory factory;
    public M7QueryPerformanceHttpContractTests(M7QueryPerformanceApiFactory factory) => this.factory = factory;

    [Theory]
    [InlineData("cpu")]
    [InlineData("duration")]
    [InlineData("executions")]
    [InlineData("logical_reads")]
    [InlineData("writes")]
    [InlineData("rows")]
    public async Task BrowserMetricNamesAreAccepted(string metric)
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/top?metric={metric}&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RealTopRouteReplaysItsIssuedCursorWithExactBindings()
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        const string from = "2026-08-24T11:00:00Z", to = "2026-08-24T12:00:00Z";
        HttpResponseMessage first = await client.GetAsync($"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/top?metric=cpu&fromUtc={from}&toUtc={to}&limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstDocument = await System.Text.Json.JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        string cursor = firstDocument.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.NotEmpty(cursor);
        HttpResponseMessage second = await client.GetAsync($"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/top?metric=cpu&fromUtc={from}&toUtc={to}&limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotNull(factory.Service.LastCursor);
        Assert.Equal(QueryPerformanceMetric.CpuMilliseconds, factory.Service.LastCursor!.Metric);
        Assert.Equal(DateTimeOffset.Parse(from, CultureInfo.InvariantCulture), factory.Service.LastCursor.FromUtc);
        Assert.Equal(DateTimeOffset.Parse(to, CultureInfo.InvariantCulture), factory.Service.LastCursor.ToUtc);
    }

    [Fact]
    public async Task RealHistoryRouteIssuesAndReplaysCursorWhenCpuIsNull()
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        const string from = "2026-08-24T11:00:00Z", to = "2026-08-24T12:00:00Z", query = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string route = $"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/databases/5/history/{query}?fromUtc={from}&toUtc={to}&limit=1";
        HttpResponseMessage first = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync());
        string cursor = document.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.NotEmpty(cursor);
        Assert.True(document.RootElement.GetProperty("items")[0].GetProperty("cpuMilliseconds").ValueKind == System.Text.Json.JsonValueKind.Null);
        HttpResponseMessage second = await client.GetAsync(route + $"&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotNull(factory.Service.LastHistoryCursor);
        Assert.Equal(DateTimeOffset.Parse(from, CultureInfo.InvariantCulture), factory.Service.LastHistoryCursor!.FromUtc);
    }

    [Fact]
    public async Task RealStatusRouteExposesTargetFailureEvidence()
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"source\":\"unavailable\"", body, StringComparison.Ordinal);
        Assert.Contains("\"targetStatus\":\"deadline_exceeded\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=201")]
    public async Task RealTopRouteRejectsInvalidBounds(string query)
    {
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/v1/observation-targets/{M7QueryPerformanceApiFactory.TargetId:D}/query-performance/top?metric=cpu&{query}")).StatusCode);
    }
}

public sealed class M7QueryPerformanceApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid TargetId = Guid.Parse("abababab-abab-4aba-8aba-abababababab");
    internal FakeM7QueryPerformanceService Service { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("ContractTesting").ConfigureTestServices(services =>
    {
        services.RemoveAll<IQueryPerformanceApiQueryService>();
        services.RemoveAll<WindowsGroupRoleResolver>();
        services.AddSingleton<IQueryPerformanceApiQueryService>(Service);
        services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.Viewer], true)]));
        services.AddAuthentication(options => { options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName; options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName; }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, static _ => { });
    });
}

internal sealed class FakeM7QueryPerformanceService : IQueryPerformanceApiQueryService
{
    internal QueryPerformanceCursorEnvelope? LastCursor { get; private set; }
    internal QueryPerformanceCursorEnvelope? LastHistoryCursor { get; private set; }
    private static readonly DateTimeOffset From = new(2026, 8, 24, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    public ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(AuthorizationContext authorization, QueryPerformanceStatusRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<QueryPerformanceStatusDto?>(new QueryPerformanceStatusDto(request.TargetId, To, QueryPerformanceSource.Unavailable, "unavailable", QueryCoverage.Unavailable, false, false, false, null, [], "deadline_exceeded", "deadline_exceeded"));
    public ValueTask<TopQueryPage> GetTopAsync(AuthorizationContext authorization, TopQueryRequest request, CancellationToken cancellationToken)
    {
        LastCursor = request.Cursor;
        var query = new QueryOpaqueIdentity(5, new string('a', 64));
        var plan = new PlanOpaqueIdentity(query, new string('b', 64));
        var item = new TopQueryDto(request.TargetId, query, plan, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, request.Metric, 42, QueryMetricSemantics.QueryStoreInterval, From, To, QueryCoverage.Complete, true, false, false, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), new string('d', 32));
        return ValueTask.FromResult(new TopQueryPage([item], request.Cursor is null, To));
    }
    public ValueTask<QueryHistoryPage> GetHistoryAsync(AuthorizationContext authorization, QueryHistoryRequest request, CancellationToken cancellationToken)
    {
        LastHistoryCursor = request.Cursor;
        var item = new QueryHistoryDto(request.TargetId, request.Query, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, new QueryPerformanceMetricSet(null, 2, 3, 4, 5, 6), QueryMetricSemantics.QueryStoreInterval, From, To, false, true, false, false, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), QueryCoverage.Complete, new string('b', 64), new string('d', 32));
        return ValueTask.FromResult(new QueryHistoryPage([item], true, To));
    }
    public ValueTask<QueryPlanMetadataDto?> GetPlanAsync(AuthorizationContext authorization, QueryPlanMetadataRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<QueryPlanMetadataDto?>(null);
}
