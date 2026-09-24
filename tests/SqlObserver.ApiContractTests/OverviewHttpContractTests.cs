using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;

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
    public async Task OverviewCanFinishAfterTheGlobalFifteenSecondTimeout()
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService, DelayedOverviewHttpStub>();
        }));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(25);
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

public sealed class DelayedOverviewHttpStub : IOverviewQueryService
{
    public async Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(16), cancellationToken);
        return new OverviewSnapshot(DateTimeOffset.UtcNow, query.FromUtc, query.ToUtc, query.TargetId, [], 0, []);
    }
}
