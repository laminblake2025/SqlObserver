using System.Globalization;
using System.Net;
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
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ServerLifecycleHistoryHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset To = Round(DateTimeOffset.UtcNow), From = To.AddHours(-1);
    private static readonly string Epoch = new('a',64);
    private static DateTimeOffset Round(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10,TimeSpan.Zero);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture));
    private static string Query(int? limit = null) => "targetRevision=5&fromUtc=" + Utc(From) + "&toUtc=" + Utc(To) +
        (limit is { } value ? "&limit=" + value.ToString(CultureInfo.InvariantCulture) : "");
    private static string List(int? limit = null) => $"/api/v1/observation-targets/{Target:D}/lifecycle/history?" + Query(limit);
    private static string Exact(ServerLifecycleHistoryEvent item) => $"/api/v1/observation-targets/{Target:D}/lifecycle/history/{item.EventId:D}?" + Query() +
        $"&sampleTargetRevision=2&afterRunId={item.AfterRunId:D}&detectedAtUtc=" + Utc(item.DetectedAtUtc);
    private static HttpClient Client(Factory factory,string? identity = "viewer")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Server-Lifecycle-Identity",identity);
        return client;
    }

    [Fact]
    public async Task MountedRoutesRequireAuthenticationMetadataAndUseRealDefaultListService()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync(List()); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal(25,factory.Repository.ListRequest!.Limit); Assert.Equal(TimeSpan.FromSeconds(5),factory.Repository.ListRequest.Timeout.Value);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(Target,root.GetProperty("targetId").GetGuid()); Assert.False(root.TryGetProperty("nextAnchor",out _));
        Assert.Equal("unqualified",root.GetProperty("coverage").GetProperty("availabilityQualification").GetString());
        Assert.False(root.GetProperty("coverage").GetProperty("sourceConsistencyVerified").GetBoolean());
        Assert.EndsWith("Z",root.GetProperty("fromUtc").GetString(),StringComparison.Ordinal);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.Contains("/lifecycle/history",StringComparison.Ordinal) == true).ToArray();
        static void RequiresIdentity(RouteEndpoint endpoint)
        { Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()); Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()); }
        Assert.Collection(endpoints,RequiresIdentity,RequiresIdentity);
    }

    [Fact]
    public async Task ExactEventKeepsItsOwnImmutableProofNullOccurrenceAndExactCounters()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        var item = factory.Repository.Items[1]; using var response = await client.GetAsync(Exact(item));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(item.EventId,root.GetProperty("event").GetProperty("eventId").GetGuid());
        Assert.Equal(2,root.GetProperty("event").GetProperty("sampleTargetRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null,root.GetProperty("event").GetProperty("occurredAtUtc").ValueKind);
        Assert.Equal("9223372036854775807",root.GetProperty("afterEngine").GetProperty("currentMsTicks").GetString());
        Assert.DoesNotContain("Z",root.GetProperty("afterEngine").GetProperty("sourceStartLocal").GetString(),StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("coverage",out _)); Assert.Equal(item.AfterRunId,factory.Repository.ExactRequest!.AfterRunId);
        Assert.Equal(item.DetectedAtUtc,factory.Repository.ExactRequest.DetectedAtUtc);
    }

    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task SupportedSameTargetRolesCanReadBothRoutes(string identity)
    {
        await using var factory = new Factory(); using var client = Client(factory,identity);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(List())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(Exact(factory.Repository.Items[1]))).StatusCode);
    }

    [Theory]
    [InlineData(null,HttpStatusCode.Unauthorized)] [InlineData("mixed",HttpStatusCode.Forbidden)]
    [InlineData("auditor",HttpStatusCode.Forbidden)] [InlineData("security",HttpStatusCode.Forbidden)]
    public async Task AnonymousAndWrongScopedRolesCannotReachRepository(string? identity,HttpStatusCode status)
    {
        await using var factory = new Factory(); using var client = Client(factory,identity);
        Assert.Equal(status,(await client.GetAsync(List())).StatusCode);
        Assert.Equal(status,(await client.GetAsync(Exact(factory.Repository.Items[1]))).StatusCode); Assert.Equal(0,factory.Repository.Reads);
    }

    [Fact]
    public async Task NativeCursorIsProtectedAndFullContextBoundBeforeStorage()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var first = JsonDocument.Parse(await client.GetStringAsync(List(1))); string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length <= ServerLifecycleHistoryCursorProtector.MaximumTokenLength);
        Assert.False(first.RootElement.TryGetProperty("nextAnchor",out _));
        using var last = JsonDocument.Parse(await client.GetStringAsync(List(1) + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(factory.Repository.Items[1].EventId,last.RootElement.GetProperty("items")[0].GetProperty("eventId").GetGuid());
        Assert.Equal(JsonValueKind.Null,last.RootElement.GetProperty("nextCursor").ValueKind);
        int reads = factory.Repository.Reads;
        foreach (string route in new[] { List(2),List(1).Replace("targetRevision=5","targetRevision=6",StringComparison.Ordinal),
            List(1).Replace(Utc(From),Utc(From.AddTicks(10)),StringComparison.Ordinal),
            List(1).Replace(Target.ToString("D"),Other.ToString("D"),StringComparison.Ordinal) })
        {
            using var result = await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor));
            Assert.True(result.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden);
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List(1) + "&cursor=" + Uri.EscapeDataString(cursor[..^4] + "AAAA"))).StatusCode);
        Assert.Equal(reads,factory.Repository.Reads);
    }

    [Fact]
    public void ProtectorUsesOriginalIssuedExpiryAndRejectsOtherPurposeOrOversizeToken()
    {
        var provider = new EphemeralDataProtectionProvider(); var protector = new ServerLifecycleHistoryCursorProtector(provider);
        var now = Round(DateTimeOffset.UtcNow); var item = Item(5);
        var cursor = new ServerLifecycleHistoryCursor(Target,5,From,To,To,now,Epoch,25,item.DetectedAtUtc,item.EventId);
        Assert.Equal(cursor,protector.Unprotect(protector.Protect(cursor)));
        var expired = cursor with { CutoffUtc = now.AddMinutes(-16),IssuedAtUtc = now.AddMinutes(-16),AfterDetectedAtUtc = From.AddMinutes(1) };
        Assert.Throws<ArgumentException>(() => protector.Unprotect(protector.Protect(expired)));
        var foreign = new AlertLifecycleCursorProtector(provider).Protect(new(Target,5,From,To,To,now,Epoch,25,item.DetectedAtUtc,item.EventId));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(foreign));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(new string('a',ServerLifecycleHistoryCursorProtector.MaximumTokenLength + 1)));
    }

    [Theory]
    [InlineData("targetRevision","05")] [InlineData("targetRevision","0")] [InlineData("targetRevision","9007199254740992")]
    [InlineData("limit","101")] [InlineData("limit","2147483648")] [InlineData("limit","01")]
    [InlineData("fromUtc","2026-10-03T00:00:00+00:00")] [InlineData("fromUtc","2026-02-30T00:00:00Z")]
    [InlineData("fromUtc","2026-10-03T00:00:00.0000001Z")] [InlineData("fromUtc","2026-10-03T00:00:00.0000000Z")]
    public async Task InvalidCanonicalQueryCannotReachRepository(string field,string replacement)
    {
        await using var factory = new Factory(); using var client = Client(factory);
        string[] fields = Query().Split('&').Where(value => !value.StartsWith(field + "=",StringComparison.Ordinal))
            .Append(field + "=" + Uri.EscapeDataString(replacement)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(List().Split('?')[0] + "?" + string.Join('&',fields))).StatusCode);
        Assert.Equal(0,factory.Repository.Reads);
    }

    [Fact]
    public async Task MissingRepeatedAndUnknownFieldsCannotSupplyNativeProvenance()
    {
        await using var factory = new Factory(); using var client = Client(factory); string list = List(); string exact = Exact(factory.Repository.Items[1]);
        foreach (string field in new[] { "targetRevision","fromUtc","toUtc" })
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Remove(list,field))).StatusCode);
        foreach (string field in new[] { "targetRevision","sampleTargetRevision","fromUtc","toUtc","afterRunId","detectedAtUtc" })
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(Remove(exact,field))).StatusCode);
        foreach (string route in new[] { list + "&targetRevision=5",list + "&TargetRevision=5",list + "&cutoffUtc=" + Utc(To),
            list + "&readEpoch=" + Epoch,list + "&cursor=",exact + "&cursor=ignored",exact + "&detectedAtUtc=" + Utc(To) })
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(route)).StatusCode);
        Assert.Equal(0,factory.Repository.Reads);
    }

    [Fact]
    public async Task ExactNativeIdentityAndTimeAreMandatoryWithNoLatestSubstitution()
    {
        await using var factory = new Factory(); using var client = Client(factory); var item = factory.Repository.Items[1]; string exact = Exact(item);
        foreach (string route in new[] { exact.Replace(item.AfterRunId.ToString("D"),"00000000-0000-0000-0000-000000000000",StringComparison.Ordinal),
            exact.Replace(item.AfterRunId.ToString("D"),item.AfterRunId.ToString("N"),StringComparison.Ordinal),
            exact.Replace(Utc(item.DetectedAtUtc),Utc(To),StringComparison.Ordinal),exact.Replace("sampleTargetRevision=2","sampleTargetRevision=6",StringComparison.Ordinal) })
            Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(exact.Replace(item.EventId.ToString("D"),"not-a-uuid",StringComparison.Ordinal))).StatusCode);
        Assert.Equal(0,factory.Repository.Reads);
        factory.Repository.Missing = true;
        using var missing = JsonDocument.Parse(await client.GetStringAsync(exact));
        Assert.Equal("event_not_retained",missing.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null,missing.RootElement.GetProperty("event").ValueKind);
        Assert.Equal(JsonValueKind.Null,missing.RootElement.GetProperty("beforeEngine").ValueKind);
    }

    [Theory]
    [InlineData(409,"lifecycle_context_changed")] [InlineData(422,"lifecycle_window_overflow")] [InlineData(503,"repository_unavailable")]
    public async Task NativeRefusalsPreserveStatusCacheAndCorrelation(int status,string code)
    {
        await using var factory = new Factory(); factory.Repository.Failure = new(code,"Retry or narrow the retained window.",status);
        using var client = Client(factory); using var response = await client.GetAsync(List());
        Assert.Equal((HttpStatusCode)status,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code,json.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(),json.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task RepositoryContractViolationFailsClosed500WithoutLeakingDetails()
    {
        await using var factory = new Factory(); factory.Repository.Corrupt = true; using var client = Client(factory);
        Guid correlation = Guid.NewGuid(); client.DefaultRequestHeaders.Add("X-Correlation-ID",correlation.ToString("D"));
        using var response = await client.GetAsync(List()); Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        string body = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(body);
        Assert.Equal("request_failed",json.RootElement.GetProperty("code").GetString());
        Assert.Equal(correlation.ToString("D"),json.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("private-provider-detail",body,StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealServiceRejectsAnAlteredImmutableProofAsSafe500()
    {
        await using var factory = new Factory(); factory.Repository.CorruptProof = true; using var client = Client(factory);
        using var response = await client.GetAsync(Exact(factory.Repository.Items[1]));
        Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request_failed",json.RootElement.GetProperty("code").GetString()); Assert.False(json.RootElement.TryGetProperty("event",out _));
    }

    [Fact]
    public async Task CallerCancellationReachesRepositoryWithoutTurningIntoSuccess()
    {
        await using var factory = new Factory(); factory.Repository.Wait = true; using var client = Client(factory);
        using var cancellation = new CancellationTokenSource(); var pending = client.GetAsync(List(),cancellation.Token);
        await factory.Repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await factory.Repository.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static string Remove(string route,string field) => route.Split('?')[0] + "?" +
        string.Join('&',route.Split('?')[1].Split('&').Where(value => !value.StartsWith(field + "=",StringComparison.Ordinal)));
    private static ServerLifecycleHistoryEvent Item(int minute)
    {
        Guid before = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        Guid after = Guid.Parse(minute == 5 ? "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" : "cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        string identity = new('b',64);
        Guid eventId = ServerLifecycleEventIdentity.Compute(Target,2,1,ServerLifecycleEventKind.RestartDetected,before,after,identity);
        return new(eventId,Target,2,before,after,"restart_detected",identity,From.AddMinutes(minute).AddSeconds(-2),
            From.AddMinutes(minute).AddSeconds(-1),From.AddMinutes(minute),null,1);
    }
    private static ServerLifecycleHistoryEngineProof Proof(bool after)
    {
        DateTime local = new(2026,10,3,after ? 10 : 9,0,0,DateTimeKind.Unspecified);
        int pid = after ? 22 : 11; long start = after ? 200_000 : 100_000, current = after ? long.MaxValue : 150_000;
        var engine = new ServerLifecycleEngineEvidence(new('b',64),new('c',64),pid,local,start,current);
        return new(engine.PhysicalNodeFingerprint!,engine.LogicalInstanceFingerprint!,engine.StartupIncarnationFingerprint!,pid,local,
            start.ToString(CultureInfo.InvariantCulture),current.ToString(CultureInfo.InvariantCulture),"complete");
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                // Cursor cryptography is exercised with a real isolated provider, independent of host key storage.
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.RemoveAll<IServerLifecycleHistoryReadPort>(); services.AddSingleton<IServerLifecycleHistoryReadPort>(Repository);
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
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        internal const string SchemeName = "Server.Lifecycle.History.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Server-Lifecycle-Identity"];
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer"=>"S-1-5-21-1111","operator"=>"S-1-5-21-2222","admin"=>"S-1-5-21-3333",
                "mixed"=>"S-1-5-21-4444","auditor"=>"S-1-5-21-5555","security"=>"S-1-5-21-6666",_=>"" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier,"S-1-5-21-7777"),new(ClaimTypes.GroupSid,group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid,"S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName)));
        }
    }
    private sealed class Repository : IServerLifecycleHistoryReadPort
    {
        internal ServerLifecycleHistoryEvent[] Items { get; } = [Item(5),Item(10)];
        internal int Reads { get; private set; }
        internal bool Missing { get; set; }
        internal bool Corrupt { get; set; }
        internal bool CorruptProof { get; set; }
        internal bool Wait { get; set; }
        internal ServerLifecycleHistoryReadException? Failure { get; set; }
        internal ServerLifecycleHistoryListRequest? ListRequest { get; private set; }
        internal ServerLifecycleHistoryExactRequest? ExactRequest { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ServerLifecycleHistoryPage?> ListAsync(ServerLifecycleHistoryListRequest request,CancellationToken cancellationToken)
        {
            Reads++; ListRequest = request; Started.TrySetResult();
            if (Wait)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan,cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (Failure is not null) throw Failure;
            if (Corrupt) throw new InvalidDataException("private-provider-detail");
            var items = Items.Where(item => request.Cursor is null || item.DetectedAtUtc > request.Cursor.AfterDetectedAtUtc).Take(request.Limit).ToArray();
            var next = request.Limit == 1 && request.Cursor is null ? new ServerLifecycleHistoryCursor(Target,5,From,To,To,To,Epoch,1,items[0].DetectedAtUtc,items[0].EventId) : null;
            return new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),"available","retained_source_coverage",
                new(3,3,3,0,0,0,0,1,2,From,false,"unqualified",false),items,next);
        }
        public ValueTask<ServerLifecycleHistoryExactPage?> GetAsync(ServerLifecycleHistoryExactRequest request,CancellationToken cancellationToken)
        {
            Reads++; ExactRequest = request; cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            var after = CorruptProof ? Proof(true) with { StartupIncarnationFingerprint = new('f',64) } : Proof(true);
            return ValueTask.FromResult<ServerLifecycleHistoryExactPage?>(new(Target,5,From,To,To,Epoch,Round(DateTimeOffset.UtcNow),
                Missing ? "unavailable" : "available",Missing ? "event_not_retained" : "retained_lifecycle_event",
                Missing ? null : Items[1],Missing ? null : Proof(false),Missing ? null : after));
        }
    }
}
