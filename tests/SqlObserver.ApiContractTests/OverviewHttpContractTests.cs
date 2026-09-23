using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class OverviewHttpContractTests : IClassFixture<OverviewApiFactory>
{
    private readonly OverviewApiFactory factory;
    public OverviewHttpContractTests(OverviewApiFactory factory) => this.factory=factory;
    private HttpClient Client(bool authenticated=true)
    {
        var client=factory.CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false});
        if(authenticated)client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader,"viewer");
        return client;
    }
    [Fact]
    public async Task RequiresAuthentication()
    {
        using var client=Client(false);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z")).StatusCode);
    }
    [Fact]
    public async Task ScopeAndUtcWindowAreBoundIntoOneNonCachedEnvelope()
    {
        using var client=Client();
        var response=await client.GetAsync("/api/v1/overview?targetId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl?.NoStore);
        var snapshot=await response.Content.ReadFromJsonAsync<OverviewSnapshot>();
        Assert.Equal(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),snapshot!.TargetId);
        Assert.Equal(TimeSpan.FromDays(1),snapshot.ToUtc-snapshot.FromUtc);
        Assert.DoesNotContain("+00:00",await response.Content.ReadAsStringAsync());
    }
    [Theory]
    [InlineData("fromUtc=garbage&toUtc=2026-09-05T00:00:00Z")]
    [InlineData("targetId=not-a-guid&fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z")]
    [InlineData("fromUtc=2026-09-04T00:00:00Z")]
    public async Task RejectsMalformedQueryBeforeDispatch(string query)
    {
        using var client=Client();Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/v1/overview?"+query)).StatusCode);
    }

    [Fact]
    public void OnlyOverviewOverridesTheFifteenSecondDefaultWithThirtySeconds()
    {
        using var client = Client();
        RequestTimeoutOptions policies = factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(15), policies.DefaultPolicy!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), policies.Policies[ServerRequestTimeouts.OverviewPolicyName].Timeout);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        RouteEndpoint overview = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/api/v1/overview");
        Assert.Equal(ServerRequestTimeouts.OverviewPolicyName, overview.Metadata.GetMetadata<RequestTimeoutAttribute>()!.PolicyName);
        Assert.DoesNotContain(endpoints.Where(endpoint => endpoint != overview),
            endpoint => endpoint.Metadata.GetMetadata<RequestTimeoutAttribute>()?.PolicyName == ServerRequestTimeouts.OverviewPolicyName);
    }

    [Fact]
    public async Task HostReturnsPartialEvidenceAfterFifteenSeconds()
    {
        var service = new SlowOverviewHttpStub(TimeSpan.FromSeconds(16));
        using var slowFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService>(service);
        }));
        using var client = slowFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(timer.Elapsed.TotalSeconds, 15, 29);
        var snapshot = await response.Content.ReadFromJsonAsync<OverviewSnapshot>();
        Assert.Contains("Alerts unavailable", Assert.Single(snapshot!.Evidence).Gaps);
        Assert.False(service.Cancelled);
    }

    [Fact]
    public async Task HostCancelsOverviewAtItsThirtySecondDeadline()
    {
        var service = new SlowOverviewHttpStub(Timeout.InfiniteTimeSpan);
        using var slowFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService>(service);
        }));
        using var client = slowFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Correlation-ID"));
        Assert.InRange(timer.Elapsed.TotalSeconds, 28, 38);
        Assert.True(service.Cancelled);
    }

    [Fact]
    public async Task InventoryDeadlineReturnsGatewayTimeoutWithCorrelationBeforeHttpDeadline()
    {
        var inventory = new StalledOverviewInventory();
        var service = new OverviewQueryService(inventory,
            DispatchProxy.Create<IHealthProjectionQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IAlertQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IActivityProjectionQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IDeadlockProjectionQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IOperationalHealthQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IMetricSeriesQueryService, UnusedOverviewProjection>(),
            DispatchProxy.Create<IOverviewHistoryRepositoryPort, UnusedOverviewProjection>());
        using var slowFactory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService>(service);
        }));
        using var client = slowFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        string correlation = Guid.NewGuid().ToString("D");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        Assert.InRange(timer.Elapsed.TotalSeconds, 4, 14);
        Assert.True(inventory.Cancelled);
    }
}

public class UnusedOverviewProjection : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        throw new InvalidOperationException("Evidence must not load while inventory is unavailable.");
}

internal sealed class StalledOverviewInventory : IObservationTargetStatusQueryService
{
    internal bool Cancelled { get; private set; }
    public ValueTask<ObservationTargetStatusSnapshot?> GetAsync(GetObservationTargetStatusQuery query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Only the bounded inventory page should be read.");
    public async ValueTask<ObservationTargetStatusPage> ListAsync(ListObservationTargetsQuery query, CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        throw new InvalidOperationException("The inventory read must be cancelled.");
    }
}

internal sealed class SlowOverviewHttpStub(TimeSpan delay) : IOverviewQueryService
{
    internal bool Cancelled { get; private set; }
    public async Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { Cancelled = true; throw; }
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var unknown = new OverviewValue(null, "unavailable", null);
        return new(DateTimeOffset.UtcNow, query.FromUtc, query.ToUtc, query.TargetId,
            [new(id, "SQL test", "active")], 0,
            [new(id, "SQL test", "partial", null, unknown, unknown, unknown, [], [], [], ["Alerts unavailable"])]);
    }
}
public sealed class OverviewApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Reuse the application's established test authentication configuration.
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureTestServices(services=>
        {
            services.RemoveAll<IOverviewQueryService>();services.AddSingleton<IOverviewQueryService,OverviewHttpStub>();
            services.AddAuthentication(options=>{options.DefaultAuthenticateScheme=TestAuthenticationHandler.SchemeName;options.DefaultChallengeScheme=TestAuthenticationHandler.SchemeName;})
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName,static _=>{});
        });
    }
}
public sealed class OverviewHttpStub : IOverviewQueryService
{
    public Task<OverviewSnapshot> ReadAsync(OverviewQuery query,CancellationToken cancellationToken) =>
        Task.FromResult(new OverviewSnapshot(DateTimeOffset.UtcNow,query.FromUtc,query.ToUtc,query.TargetId,[],0,[]));
}
