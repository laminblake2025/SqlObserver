using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    [Fact]
    public async Task SelectedServerAndFleetCanOptIntoLatestStatusWithoutChangingTheDefault()
    {
        var capture = new CapturingOverviewHttpStub();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService>(capture);
        }));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        const string selection = "targetId=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa&fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z";
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/overview?" + selection)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/overview?" + selection + "&useLatestStatus=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z&useLatestStatus=true")).StatusCode);
        Assert.Equal([false, true, true], capture.ReadModes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BackupSummaryWireHasSeparateChecksAndNoDatabaseOrLedgerIdentities(bool missing)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOverviewQueryService>();
            services.AddSingleton<IOverviewQueryService>(new BackupOverviewHttpStub(missing));
        }));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        using HttpResponseMessage response = await client.GetAsync("/api/v1/overview?fromUtc=2026-09-04T00:00:00Z&toUtc=2026-09-05T00:00:00Z&useLatestStatus=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement summary = body.RootElement.GetProperty("evidence")[0].GetProperty("backupSummary");
        if (missing) { Assert.Equal(JsonValueKind.Null, summary.ValueKind); return; }
        Assert.Equal("policy_violated", summary.GetProperty("status").GetString());
        Assert.Equal("2026-09-30T12:00:00.000001Z", summary.GetProperty("assessedAtUtc").GetString());
        Assert.Equal(1, summary.GetProperty("full").GetProperty("policyViolated").GetInt32());
        Assert.Equal(1, summary.GetProperty("log").GetProperty("indeterminate").GetInt32());
        Assert.Equal("other_replica_evidence_required", summary.GetProperty("log").GetProperty("unknownReasons")[0].GetProperty("reason").GetString());
        Assert.Equal(11, summary.EnumerateObject().Count());
        Assert.False(summary.TryGetProperty("context", out _)); Assert.False(summary.TryGetProperty("runId", out _));
        Assert.False(summary.TryGetProperty("databaseFingerprint", out _));
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
public sealed class BackupOverviewHttpStub(bool missing) : IOverviewQueryService
{
    public Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        Guid target = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        DateTimeOffset at = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero).AddTicks(TimeSpan.TicksPerMicrosecond);
        OverviewBackupSummary? summary = missing ? null : new(target, at, at.AddMinutes(2), "policy_violated", "retained", "complete", 1, true,
            new(0, 1, 0, 0, 0, []), new(0, 0, 1, 0, 0, [new("other_replica_evidence_required", 1)]), 0);
        return Task.FromResult(new OverviewSnapshot(at, query.FromUtc, query.ToUtc, query.TargetId, [], 0,
            [new(target, "SQL", "current", at, new(null, "unavailable", null), new(null, "unavailable", null), new(null, "unavailable", null), [], [], [], [], summary)]));
    }
}
public sealed class OverviewApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Reuse the application's established test authentication configuration.
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureLogging(static logging => logging.ClearProviders());
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

public sealed class CapturingOverviewHttpStub : IOverviewQueryService
{
    public List<bool> ReadModes { get; } = [];

    public Task<OverviewSnapshot> ReadAsync(OverviewQuery query, CancellationToken cancellationToken)
    {
        ReadModes.Add(query.UseLatestStatus);
        return Task.FromResult(new OverviewSnapshot(DateTimeOffset.UtcNow, query.FromUtc,
            query.ToUtc, query.TargetId, [], 0, []));
    }
}
