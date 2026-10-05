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

public sealed class CpuComponentWindowHistoryHttpTests
{
    private static readonly Guid Target=Guid.Parse("11111111-1111-4111-8111-111111111111"),Other=Guid.Parse("22222222-2222-4222-8222-222222222222"),Run=Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),Failed=Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To=Round(DateTimeOffset.UtcNow),From=To.AddHours(-1);
    private static readonly string Epoch=new('a',64);
    private static DateTimeOffset Round(DateTimeOffset value)=>new(value.Ticks-value.Ticks%10,TimeSpan.Zero);
    private static string Utc(DateTimeOffset value)=>Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
    private static string Query()=>"targetRevision=5&fromUtc="+Utc(From)+"&toUtc="+Utc(To);
    private static string List()=> $"/api/v1/observation-targets/{Target:D}/resources/cpu/observations?"+Query();
    private static string Exact()=> $"/api/v1/observation-targets/{Target:D}/resources/cpu/observations/{Run:D}?"+Query()+"&sampleTargetRevision=2";
    private static HttpClient Client(Factory factory,string? identity="viewer")
    {var client=factory.CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false});if(identity is not null)client.DefaultRequestHeaders.Add("X-Cpu-Capture-Identity",identity);return client;}
    [Fact]
    public async Task ProductionCompositionHasAuthenticatedRoutesAndNoStoreBoundedPrivateRows()
    {
        await using var factory=new Factory();using var client=Client(factory);using var response=await client.GetAsync(List());Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl!.Private);Assert.True(response.Headers.CacheControl.NoStore);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var root=json.RootElement;Assert.Equal(Failed,root.GetProperty("items")[0].GetProperty("runId").GetGuid());Assert.False(root.TryGetProperty("nativeQualification",out _));Assert.False(root.TryGetProperty("targetComparisonAdmitted",out _));Assert.False(root.TryGetProperty("nextAnchor",out _));Assert.Equal(25,factory.Repository.ListRequest!.Limit);Assert.Equal(TimeSpan.FromSeconds(5),factory.Repository.ListRequest.Timeout.Value);
        var endpoints=factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(static e=>e.RoutePattern.RawText?.Contains("/resources/cpu/observations",StringComparison.Ordinal)==true).ToArray();Assert.Equal(2,endpoints.Length);foreach(var endpoint in endpoints){Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());}
    }
    [Fact]
    public async Task ExactAndListIncludeBothIndependentComponentsWithoutPrivateNativeCounters()
    {
        await using var factory=new Factory();using var client=Client(factory);using var response=await client.GetAsync(Exact());Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        string text=await response.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(text);var observation=json.RootElement.GetProperty("receipt").GetProperty("observation");
        Assert.Equal("9007199254740993",observation.GetProperty("sqlProcess").GetProperty("fromNative100ns").GetString());
        Assert.Equal("provider_adjusted",observation.GetProperty("operatingSystem").GetProperty("quality").GetString());
        Assert.Equal(-0.1m,observation.GetProperty("operatingSystem").GetProperty("rawPercent").GetDecimal());
        Assert.False(observation.GetProperty("exactPairEligible").GetBoolean());
        Assert.DoesNotContain("rawBefore",text,StringComparison.Ordinal);Assert.DoesNotContain("Fingerprint",text,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SessionId",text,StringComparison.OrdinalIgnoreCase);Assert.False(json.RootElement.TryGetProperty("capture",out _));
    }
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task NativeSameTargetReadRolesCanReachBothRoutes(string identity)
    {await using var factory=new Factory();using var client=Client(factory,identity);Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(List())).StatusCode);Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(Exact())).StatusCode);}
    [Theory]
    [InlineData(null,HttpStatusCode.Unauthorized)] [InlineData("mixed",HttpStatusCode.Forbidden)] [InlineData("auditor",HttpStatusCode.Forbidden)] [InlineData("security",HttpStatusCode.Forbidden)]
    public async Task UnauthenticatedAndMixedRoleScopesCannotReachRepository(string? identity,HttpStatusCode expected)
    {await using var factory=new Factory();using var client=Client(factory,identity);Assert.Equal(expected,(await client.GetAsync(List())).StatusCode);Assert.Equal(expected,(await client.GetAsync(Exact())).StatusCode);Assert.Equal(0,factory.Repository.Reads);}
    [Theory]
    [InlineData("&unknown=1")] [InlineData("&targetRevision=5")] [InlineData("&limit=101")] [InlineData("&limit=0")] [InlineData("&limit=01")] [InlineData("&cursor=opaque")]
    public async Task StrictQueriesFailBeforeNativeReads(string suffix)
    {await using var factory=new Factory();using var client=Client(factory);using var response=await client.GetAsync(List()+suffix);Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,factory.Repository.Reads);Assert.Equal("application/problem+json",response.Content.Headers.ContentType!.MediaType);using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("invalid_request",json.RootElement.GetProperty("code").GetString());Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("correlationId").GetString()));}
    [Theory]
    [InlineData("fromUtc","2026-10-04T00:00:00.1234567Z")]
    [InlineData("fromUtc","2026-10-04T00:00:00+00:00")]
    [InlineData("fromUtc","2026-02-30T00:00:00Z")]
    [InlineData("targetRevision","9007199254740992")]
    public async Task ContextClocksAndRevisionsAreStrictBeforeNativeAccess(string field,string value)
    {await using var factory=new Factory();using var client=Client(factory);string route=List();string query=string.Join('&',route.Split('?')[1].Split('&').Where(v=>!v.StartsWith(field+"=",StringComparison.Ordinal)));using var response=await client.GetAsync(route.Split('?')[0]+"?"+query+"&"+field+"="+Uri.EscapeDataString(value));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(0,factory.Repository.Reads);}
    [Theory]
    [InlineData("&limit=1")] [InlineData("&cursor=abc")] [InlineData("&sampleTargetRevision=2")]
    public async Task ExactReadHasNoPagingOrDuplicateIdentityFields(string suffix)
    {await using var factory=new Factory();using var client=Client(factory);Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Exact()+suffix)).StatusCode);Assert.Equal(0,factory.Repository.Reads);}
    [Fact]
    public async Task ProtectedContinuationKeepsCutoffAndTailWhileRebindingFails()
    {
        await using var factory=new Factory();using var client=Client(factory);using var first=await client.GetAsync(List()+"&limit=1");using var json=JsonDocument.Parse(await first.Content.ReadAsStringAsync());string cursor=Uri.EscapeDataString(json.RootElement.GetProperty("nextCursor").GetString()!);using var second=await client.GetAsync(List()+"&limit=1&cursor="+cursor);Assert.Equal(HttpStatusCode.OK,second.StatusCode);using var tail=JsonDocument.Parse(await second.Content.ReadAsStringAsync());Assert.Equal(Run,tail.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List()+"&limit=2&cursor="+cursor)).StatusCode);
    }
    [Fact]
    public async Task ExpiredAndForeignSignedCursorCannotReachRepository()
    {
        await using var factory=new Factory();using var client=Client(factory);var protector=factory.Services.GetRequiredService<CpuComponentWindowHistoryCursorProtector>();var cursor=new CpuComponentWindowHistoryCursor(Other,5,From,To,To,To,Epoch,1,To.AddMinutes(-5),Failed);string token=Uri.EscapeDataString(protector.Protect(cursor));Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List()+"&limit=1&cursor="+token)).StatusCode);var expired=cursor with{TargetId=Target,CutoffUtc=To.AddHours(-1),IssuedAtUtc=To.AddHours(-1),FromUtc=From.AddHours(-1),ToUtc=To.AddHours(-1),AfterCompletedAtUtc=To.AddHours(-1).AddMinutes(-5)};string old=Uri.EscapeDataString(protector.Protect(expired));Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List()+"&limit=1&cursor="+old)).StatusCode);Assert.Equal(0,factory.Repository.Reads);
    }
    [Fact]
    public async Task NativeOverflowRemainsIndependentFromExactAndCorruptionIsSanitized()
    {await using var factory=new Factory();using var client=Client(factory);factory.Repository.Failure=new("cpu_capture_window_overflow","Choose a narrower window.",422);Assert.Equal(HttpStatusCode.UnprocessableEntity,(await client.GetAsync(List())).StatusCode);Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(Exact())).StatusCode);factory.Repository.Failure=null;factory.Repository.Corrupt=true;using var response=await client.GetAsync(List());Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode);Assert.DoesNotContain("private-provider-detail",await response.Content.ReadAsStringAsync(),StringComparison.Ordinal);}
    [Fact]
    public async Task SelectedMissingReceiptHasNoSubstituteAndNoFakeCoverage()
    {await using var factory=new Factory();factory.Repository.Missing=true;using var client=Client(factory);using var response=await client.GetAsync(Exact());using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal("observation_not_retained",json.RootElement.GetProperty("reason").GetString());Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("receipt").ValueKind);Assert.False(json.RootElement.TryGetProperty("observation",out _));Assert.False(json.RootElement.TryGetProperty("coverage",out _));}
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task CollectionStateReadIsRevisionBoundAndNoStore(string identity)
    {
        await using var factory=new Factory();using var client=Client(factory,identity);
        using var response=await client.GetAsync($"/api/v1/observation-targets/{Target:D}/resources/cpu/collection?targetRevision=5");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);Assert.True(response.Headers.CacheControl!.NoStore);
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.False(json.RootElement.GetProperty("collectorEnabled").GetBoolean());Assert.Equal(1,json.RootElement.GetProperty("scheduleRevision").GetInt64());
        using var stale=await client.GetAsync($"/api/v1/observation-targets/{Target:D}/resources/cpu/collection?targetRevision=4");Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
    }
    [Theory]
    [InlineData("admin",HttpStatusCode.OK)] [InlineData("viewer",HttpStatusCode.Forbidden)] [InlineData("operator",HttpStatusCode.Forbidden)] [InlineData("mixed",HttpStatusCode.Forbidden)]
    public async Task CollectionOptInRequiresSameTargetAdministratorAndPreservesExpectedState(string identity,HttpStatusCode expected)
    {
        await using var factory=new Factory();using var client=Client(factory,identity);
        using var response=await client.PutAsJsonAsync($"/api/v1/observation-targets/{Target:D}/resources/cpu/collection",new {expectedTargetRevision=5,expectedEnabled=false,enabled=true});
        Assert.Equal(expected,response.StatusCode);
        if(expected==HttpStatusCode.OK){Assert.NotNull(factory.Administration.Request);Assert.False(factory.Administration.Request.ExpectedEnabled);Assert.True(factory.Administration.Request.Enabled);Assert.Equal(Target,factory.Administration.Request.Audit.TargetId!.Value);}
        else {Assert.Null(factory.Administration.Request);Assert.Equal(1,factory.Audit.Calls);}
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        internal Administration Administration { get; } = new();
        internal Audit Audit { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICpuComponentsCollectionAdministrationPort>(); services.AddSingleton<ICpuComponentsCollectionAdministrationPort>(Administration);
                services.RemoveAll<IAdministrativeAuditPort>(); services.AddSingleton<IAdministrativeAuditPort>(Audit);
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.RemoveAll<ICpuComponentWindowHistoryReadPort>(); services.AddSingleton<ICpuComponentWindowHistoryReadPort>(Repository);
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
    private sealed class Administration : ICpuComponentsCollectionAdministrationPort
    {
        internal SetCpuComponentsCollectionRequest? Request { get; private set; }
        public ValueTask<CpuComponentsCollectionState?> GetAsync(MonitoredInstanceId target,long revision,RepositoryCallTimeout timeout,CancellationToken token)
        {token.ThrowIfCancellationRequested();return ValueTask.FromResult<CpuComponentsCollectionState?>(new(5,1,false));}
        public ValueTask<CpuComponentsCollectionMutationResult> SetAsync(SetCpuComponentsCollectionRequest request,CancellationToken token)
        {token.ThrowIfCancellationRequested();Request=request;return ValueTask.FromResult(new CpuComponentsCollectionMutationResult(CpuComponentsCollectionMutationStatus.Applied,2,true));}
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
        internal const string SchemeName = "Cpu.Capture.History.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Cpu-Capture-Identity"];
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer"=>"S-1-5-21-1111","operator"=>"S-1-5-21-2222","admin"=>"S-1-5-21-3333",
                "mixed"=>"S-1-5-21-4444","auditor"=>"S-1-5-21-5555","security"=>"S-1-5-21-6666",_=>"" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier,"S-1-5-21-7777"),new(ClaimTypes.GroupSid,group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid,"S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName)));
        }
    }
    private sealed class Repository:ICpuComponentWindowHistoryReadPort
    {
        internal int Reads{get;private set;} internal bool Missing{get;set;}internal bool Corrupt{get;set;}internal CpuComponentWindowHistoryReadException? Failure{get;set;}internal CpuComponentWindowHistoryListRequest? ListRequest{get;private set;}
        private static CpuComponentWindowHistoryReceipt Item()=>new(Run,2,To.AddMinutes(-10),"succeeded","none",0,true,"partial","component_quality",Observation());
        private static CpuComponentWindowHistoryReceipt[] Items()=>[new(Failed,3,To.AddMinutes(-5),"timed_out","none",0,false,"unavailable","collection_failed",null),Item()];
        public ValueTask<CpuComponentWindowHistoryPage?> ListAsync(CpuComponentWindowHistoryListRequest request,CancellationToken cancellationToken)
        {Reads++;ListRequest=request;cancellationToken.ThrowIfCancellationRequested();if(Failure is not null)throw Failure;if(Corrupt)throw new InvalidDataException("private-provider-detail");var items=Items().Where(i=>request.Cursor is null || i.CompletedAtUtc<request.Cursor.AfterCompletedAtUtc).Take(request.Limit).ToArray();var next=request.Limit==1 && request.Cursor is null?new CpuComponentWindowHistoryCursor(Target,5,From,To,To,To,Epoch,1,items[0].CompletedAtUtc,items[0].RunId):null;return ValueTask.FromResult<CpuComponentWindowHistoryPage?>(new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),"partial","visibility_gaps",new(2,2,0,2,1,0,0,true),items,next));}
        private static CpuComponentWindowHistoryObservation Observation()
        {
            var process=new CpuComponentWindowHistoryValue(5,5,"available","None","9007199254740993","9007199255740993","2026-10-04T11:59:00.1234567Z","2026-10-04T11:59:01.1234567Z",4,"pdh_calculate_exact_raw_pair",0,0,true,"None",20);
            var os=process with{Percent=0,RawPercent=-0.1m,Quality="provider_adjusted",Reason="ProviderAdjusted",FromNative100ns="9007199254740999",NormalizationLogicalProcessors=1,RawFormulaEligible=false,RawFormulaReason="CounterRangeInvalid",ProviderRawPercent=0};
            return new(1,"partial","component_quality",true,"None",false,process,os);
        }
        public ValueTask<CpuComponentWindowHistoryExactPage?> GetAsync(CpuComponentWindowHistoryExactRequest request,CancellationToken cancellationToken)
        {Reads++;cancellationToken.ThrowIfCancellationRequested();return ValueTask.FromResult<CpuComponentWindowHistoryExactPage?>(new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),Missing?"unavailable":"partial",Missing?"observation_not_retained":"component_quality",Run,2,Missing?null:Item()));}

    }
}
