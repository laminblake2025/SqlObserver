using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;
using SqlObserver.Server;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.ApiContractTests;

public sealed class ServerLifecycleCollectionHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset To = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private static string Route => $"/api/v1/observation-targets/{Target:D}/lifecycle/collection";
    private static object Change => new {expectedTargetRevision=5,expectedScheduleRevision=3,expectedEnabled=false,enabled=true};
    private static HttpClient Client(Factory factory,string? identity="viewer")
    {
        var client=factory.CreateClient(new WebApplicationFactoryClientOptions {AllowAutoRedirect=false});
        if(identity is not null) client.DefaultRequestHeaders.Add("X-Lifecycle-Collection-Identity",identity);
        return client;
    }
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task StateReadIsSameTargetRevisionBoundAndNoStore(string identity)
    {
        await using var factory=new Factory(); using var client=Client(factory,identity);
        using var response=await client.GetAsync(Route+"?targetRevision=5");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root=json.RootElement;
        Assert.Equal(Target,root.GetProperty("targetId").GetGuid()); Assert.Equal(5,root.GetProperty("targetRevision").GetInt64());
        Assert.Equal(3,root.GetProperty("scheduleRevision").GetInt64()); Assert.False(root.GetProperty("collectorEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync(Route+"?targetRevision=4")).StatusCode);
    }
    [Theory]
    [InlineData("admin",HttpStatusCode.OK)] [InlineData("viewer",HttpStatusCode.Forbidden)] [InlineData("operator",HttpStatusCode.Forbidden)]
    [InlineData("mixed",HttpStatusCode.Forbidden)] [InlineData("auditor",HttpStatusCode.Forbidden)] [InlineData("security",HttpStatusCode.Forbidden)]
    public async Task MutationRequiresSameTargetAdministratorAndAuditsDenial(string identity,HttpStatusCode expected)
    {
        await using var factory=new Factory(); using var client=Client(factory,identity);
        using var response=await client.PutAsJsonAsync(Route,Change); Assert.Equal(expected,response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        if(expected==HttpStatusCode.OK)
        {
            var request=Assert.IsType<SetServerLifecycleCollectionRequest>(factory.Administration.Request);
            Assert.Equal(5,request.ExpectedTargetRevision);Assert.Equal(3,request.ExpectedScheduleRevision);Assert.False(request.ExpectedEnabled);Assert.True(request.Enabled);
            Assert.Equal(Target,request.Audit.TargetId!.Value);Assert.Equal(TimeSpan.FromSeconds(5),request.Timeout.Value);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(4,json.RootElement.GetProperty("scheduleRevision").GetInt64());
        }
        else {Assert.Null(factory.Administration.Request);Assert.Equal(1,factory.Audit.Calls);}
    }
    [Theory]
    [InlineData(null,HttpStatusCode.Unauthorized)] [InlineData("mixed",HttpStatusCode.Forbidden)]
    [InlineData("auditor",HttpStatusCode.Forbidden)] [InlineData("security",HttpStatusCode.Forbidden)]
    public async Task UnauthorizedReadsDoNotTouchRepository(string? identity,HttpStatusCode expected)
    {
        await using var factory=new Factory();using var client=Client(factory,identity);
        Assert.Equal(expected,(await client.GetAsync(Route+"?targetRevision=5")).StatusCode);Assert.Equal(0,factory.Administration.Reads);
    }
    [Theory]
    [InlineData("")] [InlineData("?targetRevision=0")] [InlineData("?targetRevision=05")]
    [InlineData("?targetRevision=5&targetRevision=5")] [InlineData("?targetRevision=5&other=1")]
    [InlineData("?targetRevision=9007199254740992")]
    public async Task InvalidReadsAreRejectedBeforeRepository(string query)
    {
        await using var factory=new Factory();using var client=Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Route+query)).StatusCode);Assert.Equal(0,factory.Administration.Reads);
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"expectedTargetRevision\":5,\"expectedScheduleRevision\":3,\"enabled\":true}")]
    [InlineData("{\"expectedTargetRevision\":5,\"expectedScheduleRevision\":0,\"expectedEnabled\":false,\"enabled\":true}")]
    [InlineData("{\"expectedTargetRevision\":5,\"expectedScheduleRevision\":3,\"expectedEnabled\":false,\"enabled\":true,\"extra\":true}")]
    [InlineData("{\"expectedTargetRevision\":5,\"expectedScheduleRevision\":9007199254740991,\"expectedEnabled\":false,\"enabled\":true}")]
    public async Task MissingOrInvalidExpectedStateNeverMutates(string json)
    {
        await using var factory=new Factory();using var client=Client(factory,"admin");
        using var content=new StringContent(json,System.Text.Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PutAsync(Route,content)).StatusCode);Assert.Null(factory.Administration.Request);
    }
    [Theory]
    [InlineData(ServerLifecycleCollectionMutationStatus.NotFound,HttpStatusCode.NotFound)]
    [InlineData(ServerLifecycleCollectionMutationStatus.Inactive,HttpStatusCode.Conflict)]
    [InlineData(ServerLifecycleCollectionMutationStatus.RevisionConflict,HttpStatusCode.Conflict)]
    public async Task RepositoryConflictsDoNotBecomeSuccess(ServerLifecycleCollectionMutationStatus status,HttpStatusCode expected)
    {
        await using var factory=new Factory();factory.Administration.Result=new(status,null,null);using var client=Client(factory,"admin");
        Assert.Equal(expected,(await client.PutAsJsonAsync(Route,Change)).StatusCode);
    }
    [Theory]
    [InlineData(3,true)] [InlineData(5,true)] [InlineData(4,false)]
    public async Task CorruptMutationReceiptFailsClosed(long revision,bool enabled)
    {
        await using var factory=new Factory();factory.Administration.Result=new(ServerLifecycleCollectionMutationStatus.Applied,revision,enabled);using var client=Client(factory,"admin");
        Assert.Equal(HttpStatusCode.InternalServerError,(await client.PutAsJsonAsync(Route,Change)).StatusCode);
    }
    [Fact]
    public async Task NoOpKeepsScheduleRevisionAndMissingStateReturnsNotFound()
    {
        await using var factory=new Factory();using var client=Client(factory,"admin");factory.Administration.Result=new(ServerLifecycleCollectionMutationStatus.Applied,3,false);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync(Route,new {expectedTargetRevision=5,expectedScheduleRevision=3,expectedEnabled=false,enabled=false})).StatusCode);
        factory.Administration.State=null;Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(Route+"?targetRevision=5")).StatusCode);
    }
    [Fact]
    public async Task RepositoryFailureRetainsSanitizedProblemAndNoStore()
    {
        await using var factory=new Factory();factory.Administration.Failure=new("lifecycle_collection_unavailable","Collection state is temporarily unavailable.",503,new InvalidOperationException("private connection secret"));using var client=Client(factory,"admin");
        using var response=await client.PutAsJsonAsync(Route,Change);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.True(response.Headers.CacheControl!.NoStore);
        string text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("private connection secret",text,StringComparison.Ordinal);Assert.Contains("lifecycle_collection_unavailable",text,StringComparison.Ordinal);
    }
    [Fact]
    public async Task RoutesRequireAuthenticationAndMutationRateLimit()
    {
        await using var factory=new Factory();using var client=Client(factory);await client.GetAsync(Route+"?targetRevision=5");
        var endpoints=factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e=>e.RoutePattern.RawText?.EndsWith("/lifecycle/collection",StringComparison.Ordinal)==true).ToArray();
        Assert.Equal(2,endpoints.Length);foreach(var endpoint in endpoints){Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());}
        var put=Assert.Single(endpoints,e=>e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        Assert.NotNull(put.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>());
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Administration Administration { get; } = new();
        internal Audit Audit { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServerLifecycleCollectionAdministrationPort>(); services.AddSingleton<IServerLifecycleCollectionAdministrationPort>(Administration);
                services.RemoveAll<IAdministrativeAuditPort>(); services.AddSingleton<IAdministrativeAuditPort>(Audit);
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new("S-1-5-21-1111"),[ApplicationRole.Viewer],false,[new(Target)]),
                    new(new("S-1-5-21-2222"),[ApplicationRole.Operator],false,[new(Target)]),
                    new(new("S-1-5-21-3333"),[ApplicationRole.TargetAdministrator],false,[new(Target)]),
                    new(new("S-1-5-21-4444"),[ApplicationRole.Viewer],false,[new(Other)]),
                    new(new("S-1-5-21-5555"),[ApplicationRole.Auditor],false,[new(Target)]),
                    new(new("S-1-5-21-6666"),[ApplicationRole.SecurityAdministrator],false,[new(Target)]),
                ]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions,Authentication>(Authentication.SchemeName,static _ => { });
            });
        }
    }
    private sealed class Administration : IServerLifecycleCollectionAdministrationPort
    {
        internal int Reads { get; private set; }
        internal SetServerLifecycleCollectionRequest? Request { get; private set; }
        internal ServerLifecycleCollectionState? State { get; set; } = new(5,3,false);
        internal ServerLifecycleCollectionMutationResult Result { get; set; } = new(ServerLifecycleCollectionMutationStatus.Applied,4,true);
        internal ServerLifecycleCollectionException? Failure { get; set; }
        public ValueTask<ServerLifecycleCollectionState?> GetAsync(MonitoredInstanceId target,long revision,RepositoryCallTimeout timeout,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; if(Failure is not null) throw Failure; return ValueTask.FromResult(State); }
        public ValueTask<ServerLifecycleCollectionMutationResult> SetAsync(SetServerLifecycleCollectionRequest request,CancellationToken token)
        { token.ThrowIfCancellationRequested(); Request=request; if(Failure is not null) throw Failure; return ValueTask.FromResult(Result); }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        internal int Calls { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request,CancellationToken token)
        {Calls++;return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()),To));}
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        internal const string SchemeName = "Lifecycle.Collection.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Lifecycle-Collection-Identity"];
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer"=>"S-1-5-21-1111","operator"=>"S-1-5-21-2222","admin"=>"S-1-5-21-3333",
                "mixed"=>"S-1-5-21-4444","auditor"=>"S-1-5-21-5555","security"=>"S-1-5-21-6666",_=>"" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier,"S-1-5-21-7777"),new(ClaimTypes.GroupSid,group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid,"S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName)));
        }
    }
}
