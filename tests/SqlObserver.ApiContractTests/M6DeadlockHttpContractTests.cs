using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class M6DeadlockHttpContractTests : IClassFixture<M6DeadlockApiFactory>
{
    private readonly M6DeadlockApiFactory factory;
    public M6DeadlockHttpContractTests(M6DeadlockApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task AuthorizedListAndDetailAreTargetScopedAndBounded()
    {
        using HttpClient client = CreateClient("viewer");
        HttpResponseMessage list = await client.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks?limit=1");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        HttpResponseMessage detail = await client.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks/{M6DeadlockApiFactory.EventId:D}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.DoesNotContain("xml", await detail.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=257")]
    [InlineData("fromUtc=2026-08-24T00:00:00Z&toUtc=2026-08-23T00:00:00Z")]
    [InlineData("cursor=not-a-cursor")]
    public async Task InvalidBoundsAndCursorFailAsBadRequest(string query)
    {
        using HttpClient client = CreateClient("viewer");
        HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizationAndMalformedRepositoryDataFailSafely()
    {
        using HttpClient anonymous = CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks")).StatusCode);
        using HttpClient scoped = CreateClient("scoped");
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks")).StatusCode);
        factory.Service.Invalid = true;
        try
        {
            using HttpClient client = CreateClient("viewer");
            HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("provider", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally { factory.Service.Invalid = false; }
    }

    [Fact]
    public async Task DetailWithMismatchedRequestedEventIdFailsSafely()
    {
        factory.Service.InvalidDetailIdentity = true;
        try
        {
            using HttpClient client = CreateClient("viewer");
            HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M6DeadlockApiFactory.TargetId:D}/deadlocks/{M6DeadlockApiFactory.EventId:D}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally { factory.Service.InvalidDetailIdentity = false; }
    }

    private HttpClient CreateClient(string? identity = null)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
}

public sealed class M6DeadlockApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid TargetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid EventId = DeadlockObservation.ComputeEventId(new MonitoredInstanceId(TargetId), new string('f', 64));
    internal FakeDeadlockQueryService Service { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDeadlockProjectionQueryService>(); services.RemoveAll<WindowsGroupRoleResolver>(); services.AddSingleton<IDeadlockProjectionQueryService>(Service);
            services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.Viewer], true), new WindowsGroupRoleBinding(new ActorSecurityIdentifier(TestAuthenticationHandler.ScopedGroupSid), [ApplicationRole.Viewer], false, [new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"))])]));
            services.AddAuthentication(options => { options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName; options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName; }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, static _ => { });
        });
    }
}

internal sealed class FakeDeadlockQueryService : IDeadlockProjectionQueryService
{
    internal bool Invalid { get; set; }
    internal bool InvalidDetailIdentity { get; set; }
    private static readonly DateTimeOffset At = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
    public ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksQuery query, CancellationToken cancellationToken)
    {
        if (!query.Authorization.CanAccess(ApplicationRole.Viewer, query.TargetId)) throw new UnauthorizedAccessException();
        if (Invalid) throw new InvalidDataException("bounded projection contract failed");
        return ValueTask.FromResult<DeadlockPage?>(new DeadlockPage(query.TargetId, At, [Summary(query.TargetId)], null));
    }
    public ValueTask<DeadlockDetailDto?> GetDeadlockAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken) { if (!authorization.CanAccess(ApplicationRole.Viewer, targetId)) throw new UnauthorizedAccessException(); if (InvalidDetailIdentity) throw new InvalidDataException("deadlock detail identity failed validation"); return ValueTask.FromResult<DeadlockDetailDto?>(new DeadlockDetailDto(Summary(targetId), [], [])); }
    private static DeadlockSummaryDto Summary(MonitoredInstanceId target, bool invalidIdentity = false) => new(target, invalidIdentity ? Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc") : M6DeadlockApiFactory.EventId, At, new string('f', 64), 0, 0, false, At);
}
