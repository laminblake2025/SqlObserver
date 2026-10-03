using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class AlertLifecycleHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Alert = Guid.Parse("33333333-3333-4333-8333-333333333333"), Episode = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid First = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Last = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To = Round(DateTimeOffset.UtcNow), From = To.AddHours(-1);
    private static readonly string Epoch = new('b',64);
    private static DateTimeOffset Round(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10,TimeSpan.Zero);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
    private static string Query(int? limit = null) => $"targetRevision=5&fromUtc={Utc(From)}&toUtc={Utc(To)}" + (limit is { } n ? $"&limit={n}" : "");
    private static string List(int? limit = null) => $"/api/v1/observation-targets/{Target:D}/alerts/history?{Query(limit)}";
    private static string Exact(bool legacy = false) => $"/api/v1/observation-targets/{Target:D}/alerts/history/{Last:D}?{Query()}&alertId={Alert:D}&episodeId={(legacy ? "legacy" : Episode.ToString("D"))}&eventAtUtc={Utc(From.AddMinutes(10))}";
    private static HttpClient Client(Factory factory,string? role = "viewer")
    { var client = factory.CreateClient(); if (role is not null) client.DefaultRequestHeaders.Add("X-Lifecycle-Identity",role); return client; }
    [Fact]
    public async Task ProductionPipelineReturnsResolvedExactEvidenceIndependentOfCurrentInboxAndNoStore()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync(Exact()); Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(Last,root.GetProperty("event").GetProperty("eventId").GetGuid()); Assert.Equal("resolved",root.GetProperty("event").GetProperty("kind").GetString());
        Assert.Equal("observed",root.GetProperty("event").GetProperty("timeBasis").GetString()); Assert.False(root.TryGetProperty("coverage",out _));
        Assert.Equal(From.AddMinutes(10),factory.Repository.Exact!.EventAtUtc); Assert.Equal(Episode,factory.Repository.Exact.EpisodeId);
    }
    [Fact]
    public async Task LegacyEpisodeMarkerRemainsNullAndMissingExactReceiptIsExplicit()
    {
        await using var factory = new Factory(); factory.Repository.Legacy = true; using var client = Client(factory);
        using var json = JsonDocument.Parse(await client.GetStringAsync(Exact(true)));
        Assert.Equal("partial",json.RootElement.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("event").GetProperty("episodeId").ValueKind);
        Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("event").GetProperty("recordedAtUtc").ValueKind); Assert.Null(factory.Repository.Exact!.EpisodeId);
        factory.Repository.Missing = true;
        using var missing = JsonDocument.Parse(await client.GetStringAsync(Exact(true))); Assert.Equal("event_not_retained",missing.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null,missing.RootElement.GetProperty("event").ValueKind);
    }
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")] [InlineData("security")] [InlineData("auditor")]
    public async Task AllExistingSameTargetReadRolesUseTheRealService(string role)
    {
        await using var factory = new Factory(); using var client = Client(factory,role);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(List())).StatusCode); Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(Exact())).StatusCode);
    }
    [Theory]
    [InlineData(null,401)] [InlineData("mixed",403)] [InlineData("text",403)]
    public async Task AnonymousAndMixedGrantUnionCannotReadProtectedEvents(string? role,int status)
    {
        await using var factory = new Factory(); using var client = Client(factory,role);
        Assert.Equal((HttpStatusCode)status,(await client.GetAsync(List())).StatusCode); Assert.Equal((HttpStatusCode)status,(await client.GetAsync(Exact())).StatusCode); Assert.Equal(0,factory.Repository.Reads);
    }
    [Fact]
    public async Task ProtectedContinuationBindsWindowRevisionPageSizeAndRejectsTamperingBeforeStorage()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var first = JsonDocument.Parse(await client.GetStringAsync(List(1))); string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length <= AlertLifecycleCursorProtector.MaximumTokenLength);
        using var last = JsonDocument.Parse(await client.GetStringAsync(List(1) + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(Last,last.RootElement.GetProperty("items")[0].GetProperty("eventId").GetGuid()); Assert.Equal(JsonValueKind.Null,last.RootElement.GetProperty("nextCursor").ValueKind);
        int reads = factory.Repository.Reads;
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List(2) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List(1).Replace("targetRevision=5","targetRevision=6",StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List(1).Replace(Utc(From),Utc(From.AddMinutes(1)),StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List(1) + "&cursor=" + Uri.EscapeDataString(cursor[..^4] + "AAAA"))).StatusCode); Assert.Equal(reads,factory.Repository.Reads);
    }
    [Fact]
    public void ProtectorExpiresFromOriginalNativeIssueAndPreservesMicrosecondAnchor()
    {
        var protector = new AlertLifecycleCursorProtector(new EphemeralDataProtectionProvider()); var issued = Round(DateTimeOffset.UtcNow);
        var cursor = new AlertLifecycleCursor(Target,5,From,To,issued,issued,Epoch,100,From.AddTicks(10),First);
        var token = protector.Protect(cursor); Assert.Equal(cursor,protector.Unprotect(token));
        string expired = protector.Protect(cursor with { CutoffUtc = issued.AddMinutes(-16),IssuedAtUtc = issued.AddMinutes(-16) });
        Assert.Throws<ArgumentException>(() => protector.Unprotect(expired));
    }
    [Theory]
    [InlineData("targetRevision","05")] [InlineData("targetRevision","0")] [InlineData("limit","101")]
    [InlineData("limit","2147483648")] [InlineData("fromUtc","2026-10-03T00:00:00+00:00")]
    [InlineData("fromUtc","2026-02-30T00:00:00Z")] [InlineData("fromUtc","2026-10-03T00:00:00.1234567Z")]
    public async Task StrictUtcAndCanonicalBoundedQueriesFailBeforeStorage(string field,string replacement)
    {
        await using var factory = new Factory(); using var client = Client(factory);
        string[] values = Query().Split('&').Where(value => !value.StartsWith(field + "=",StringComparison.Ordinal)).Append(field + "=" + Uri.EscapeDataString(replacement)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List().Split('?')[0] + "?" + string.Join('&',values))).StatusCode); Assert.Equal(0,factory.Repository.Reads);
    }
    [Fact]
    public async Task ExplicitCycleTimeAndNullMarkerAreMandatoryNoCallerMetadataOverrides()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Exact().Replace("&episodeId=" + Episode.ToString("D"),"",StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Exact().Replace("&eventAtUtc=" + Utc(From.AddMinutes(10)),"",StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Exact().Replace("episodeId=" + Episode.ToString("D"),"episodeId=null",StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Exact().Replace(Utc(From.AddMinutes(10)),Utc(To),StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List() + "&cutoffUtc=" + Utc(To))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List() + "&targetRevision=5")).StatusCode); Assert.Equal(0,factory.Repository.Reads);
    }
    [Theory]
    [InlineData(409,"lifecycle_context_changed")] [InlineData(422,"lifecycle_window_overflow")] [InlineData(503,"lifecycle_unavailable")]
    public async Task NativeRefusalsKeepTheirStatusAndNeverBecomeAnEmptyHealthyPage(int status,string code)
    {
        await using var factory = new Factory(); factory.Repository.Failure = new(code,"Narrow the window or retry this read.",status); using var client = Client(factory);
        using var response = await client.GetAsync(List()); Assert.Equal((HttpStatusCode)status,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code,json.RootElement.GetProperty("code").GetString());
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAlertLifecycleReadPort>(); services.AddSingleton<IAlertLifecycleReadPort>(Repository);
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new("S-1-5-21-1111"),[ApplicationRole.Viewer],false,[new(Target)]),
                    new(new("S-1-5-21-2222"),[ApplicationRole.Operator],false,[new(Target)]),
                    new(new("S-1-5-21-3333"),[ApplicationRole.TargetAdministrator],false,[new(Target)]),
                    new(new("S-1-5-21-4444"),[ApplicationRole.SecurityAdministrator],false,[new(Target)]),
                    new(new("S-1-5-21-5555"),[ApplicationRole.Auditor],false,[new(Target)]),
                    new(new("S-1-5-21-6666"),[ApplicationRole.Viewer],false,[new(Other)]),
                    new(new("S-1-5-21-8888"),[ApplicationRole.QueryTextReader],false,[new(Target)]),
                ]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions,Authentication>(Authentication.SchemeName,static _ => { });
            });
        }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        internal const string SchemeName = "Alert.Lifecycle.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? role = Request.Headers["X-Lifecycle-Identity"]; if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = role switch { "viewer"=>"S-1-5-21-1111","operator"=>"S-1-5-21-2222","admin"=>"S-1-5-21-3333","security"=>"S-1-5-21-4444","auditor"=>"S-1-5-21-5555","mixed"=>"S-1-5-21-6666","text"=>"S-1-5-21-8888",_=>"" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier,"S-1-5-21-7777"),new(ClaimTypes.GroupSid,group) };
            if (role == "mixed") claims.Add(new(ClaimTypes.GroupSid,"S-1-5-21-8888"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName)));
        }
    }
    private sealed class Repository : IAlertLifecycleReadPort
    {
        internal int Reads { get; private set; }
        internal bool Legacy { get; set; }
        internal bool Missing { get; set; }
        internal AlertLifecycleReadException? Failure { get; set; }
        internal AlertLifecycleExactRequest? Exact { get; private set; }
        private AlertLifecycleEvent Item(Guid id,int minute,string kind) => new(id,Alert,Legacy?null:Episode,Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),kind,"observed",From.AddMinutes(minute),Legacy?null:From.AddMinutes(minute).AddSeconds(1),
            "evaluation",Legacy?"legacy_partial":"complete",null,Legacy?null:3,Legacy?null:"cpu.high",kind=="fired"?"pending":"firing",kind=="fired"?"firing":"resolved",Legacy?null:95,"threshold",null,false,null,null,null,null,null);
        public ValueTask<AlertLifecyclePage?> ListAsync(AlertLifecycleListRequest request,CancellationToken cancellationToken)
        {
            Reads++; if (Failure is not null) throw Failure;
            AlertLifecycleEvent[] all = [Item(First,5,"fired"),Item(Last,10,"resolved")];
            var items = all.Where(item => request.Cursor is null || item.EventAtUtc > request.Cursor.AfterEventAtUtc).Take(request.Limit).ToArray();
            var next = request.Limit == 1 && request.Cursor is null ? new AlertLifecycleCursor(Target,5,From,To,To,To,Epoch,1,items[0].EventAtUtc,items[0].EventId) : null;
            return ValueTask.FromResult<AlertLifecyclePage?>(new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),Legacy?"partial":"available",Legacy?"legacy_provenance":"retained_lifecycle",new(2,Legacy?0:2,Legacy?2:0,1,From,null),items,next));
        }
        public ValueTask<AlertLifecycleExactPage?> GetAsync(AlertLifecycleExactRequest request,CancellationToken cancellationToken)
        {
            Reads++; Exact=request; if (Failure is not null) throw Failure;
            return ValueTask.FromResult<AlertLifecycleExactPage?>(new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),Missing?"unavailable":Legacy?"partial":"available",Missing?"event_not_retained":Legacy?"legacy_provenance":"retained_lifecycle",Missing?null:Item(Last,10,"resolved")));
        }
    }
}
