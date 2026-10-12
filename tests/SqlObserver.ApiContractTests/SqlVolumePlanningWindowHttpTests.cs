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

public sealed class SqlVolumePlanningWindowHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Run = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), OlderRun = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To = Round(DateTimeOffset.UtcNow), From = To.AddHours(-1);
    private static readonly string Epoch = new('a', 64);
    private static DateTimeOffset Round(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static string Query(int? limit = null) => "targetRevision=5&fromUtc=" + Utc(From) + "&toUtc=" + Utc(To) +
        (limit is { } value ? "&limit=" + value.ToString(CultureInfo.InvariantCulture) : "");
    private static string List(int? limit = null) => $"/api/v1/observation-targets/{Target:D}/resources/volume-planning/history?" + Query(limit);
    private static string Exact(int? limit = null) => $"/api/v1/observation-targets/{Target:D}/resources/volume-planning/history/{Run:D}?" + Query(limit) + "&sampleTargetRevision=2";
    private static HttpClient Client(Factory factory, string? identity = "viewer")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add("X-File-History-Identity", identity);
        return client;
    }
    [Fact]
    public async Task RealMountedPipelineRoutesRequireIdentityAndKeepPrivateCanonicalReceipts()
    {
        await using var factory = new Factory(); using var client = Client(factory); using var response = await client.GetAsync(List());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        Assert.Equal(25, factory.Repository.ListRequest!.Limit); Assert.Equal(TimeSpan.FromSeconds(5), factory.Repository.ListRequest.Timeout.Value);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(Target, root.GetProperty("targetId").GetGuid()); Assert.False(root.TryGetProperty("nextAnchor", out _));
        Assert.EndsWith("Z", root.GetProperty("fromUtc").GetString(), StringComparison.Ordinal);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.Contains("/resources/volume-planning/history", StringComparison.Ordinal) == true).ToArray();
        static void Protected(RouteEndpoint endpoint)
        { Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()); Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()); }
        Assert.Collection(routes, Protected, Protected);
    }
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task SameTargetReadRolesCanReadBothSources(string identity)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(List())).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Exact())).StatusCode);
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)] [InlineData("mixed", HttpStatusCode.Forbidden)] [InlineData("auditor", HttpStatusCode.Forbidden)]
    public async Task AnonymousAndMixedGrantsCannotReachStorage(string? identity, HttpStatusCode status)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(status, (await client.GetAsync(List())).StatusCode); Assert.Equal(status, (await client.GetAsync(Exact())).StatusCode); Assert.Equal(0, factory.Repository.Reads);
    }
    [Fact]
    public async Task ReceiptAndFileContinuationsAreProtectedAndCannotCrossKindsOrRunWindow()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var first = JsonDocument.Parse(await client.GetStringAsync(List(1))); string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var second = JsonDocument.Parse(await client.GetStringAsync(List(1) + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(OlderRun, second.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        using var files = JsonDocument.Parse(await client.GetStringAsync(Exact(1))); string fileCursor = files.RootElement.GetProperty("nextCursor").GetString()!;
        using var lastFile = JsonDocument.Parse(await client.GetStringAsync(Exact(1) + "&cursor=" + Uri.EscapeDataString(fileCursor)));
        Assert.Equal(new string('b',64), lastFile.RootElement.GetProperty("items")[0].GetProperty("volumeKey").GetString());
        int reads = factory.Repository.Reads;
        foreach (string route in new[] { Exact(1), List(2), List(1).Replace("targetRevision=5", "targetRevision=6", StringComparison.Ordinal),
            List(1).Replace(Utc(From), Utc(From.AddTicks(10)), StringComparison.Ordinal) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Exact(1).Replace(Run.ToString("D"), OlderRun.ToString("D"), StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(fileCursor))).StatusCode);
        Assert.Equal(reads, factory.Repository.Reads);
    }
    [Fact]
    public void OriginalIssuedExpiryAndPurposeAreProtected()
    {
        var provider = new EphemeralDataProtectionProvider(); var protector = new SqlVolumePlanningWindowCursorProtector(provider);
        var cursor = new SqlVolumePlanningWindowCursor("receipts", Target, 5, From, To, To, To, Epoch, 1, null, null, From.AddMinutes(10), Run, null);
        Assert.Equal(cursor, protector.Unprotect(protector.Protect(cursor)));
        var expired = cursor with { CutoffUtc = To.AddMinutes(-16), IssuedAtUtc = To.AddMinutes(-16) };
        Assert.Throws<ArgumentException>(() => protector.Unprotect(protector.Protect(expired)));
        var foreign = new ServerLifecycleHistoryCursorProtector(provider).Protect(new(Target, 5, From, To, To, To, Epoch, 1, From.AddMinutes(10), Run));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(foreign));
        string token = protector.Protect(cursor);
        byte[] forged = Convert.FromBase64String(token); forged[^1] ^= 1;
        Assert.Throws<ArgumentException>(() => protector.Unprotect(Convert.ToBase64String(forged)));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(new string('a', SqlVolumePlanningWindowCursorProtector.MaximumTokenLength + 1)));
    }
    [Theory]
    [InlineData("targetRevision", "05")] [InlineData("targetRevision", "0")] [InlineData("targetRevision", "9007199254740992")]
    [InlineData("limit", "101")] [InlineData("limit", "01")] [InlineData("fromUtc", "2026-10-03T00:00:00+00:00")]
    [InlineData("fromUtc", "2026-10-03T00:00:00.0000000Z")] [InlineData("fromUtc", "2026-02-30T00:00:00Z")]
    public async Task InvalidCanonicalQueriesFailBeforeStorage(string field, string value)
    {
        await using var factory = new Factory(); using var client = Client(factory);
        string route = Remove(List(), field) + "&" + field + "=" + Uri.EscapeDataString(value);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route)).StatusCode); Assert.Equal(0, factory.Repository.Reads);
    }
    [Fact]
    public async Task MandatoryUnknownRepeatedAndForgedSourceFieldsAreRejected()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        foreach (string field in new[] { "targetRevision", "fromUtc", "toUtc" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Remove(List(), field))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Remove(Exact(), "sampleTargetRevision"))).StatusCode);
        foreach (string route in new[] { List() + "&targetRevision=5", List() + "&TargetRevision=5", List() + "&cursor=", List() + "&cutoffUtc=" + Utc(To),
            List() + "&readEpoch=" + Epoch, Exact() + "&sampleTargetRevision=2", Exact().Replace("sampleTargetRevision=2", "sampleTargetRevision=6", StringComparison.Ordinal) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(0, factory.Repository.Reads);
    }
    [Fact]
    public async Task MissingExactRunRemainsExplicitWithoutFallback()
    {
        await using var factory = new Factory(); factory.Repository.Missing = true; using var client = Client(factory);
        using var json = JsonDocument.Parse(await client.GetStringAsync(Exact()));
        Assert.Equal("snapshot_not_retained", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("receipt").ValueKind); Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
    }
    [Theory]
    [InlineData(409, "history_context_changed")] [InlineData(422, "history_window_overflow")] [InlineData(503, "repository_unavailable")]
    public async Task NativeRefusalsPreserveStatusAndCorrelation(int status, string code)
    {
        await using var factory = new Factory(); factory.Repository.Failure = new(code, "Narrow the window or retry after recovery.", status); using var client = Client(factory);
        using var response = await client.GetAsync(List()); Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), json.RootElement.GetProperty("correlationId").GetString());
    }
    [Fact]
    public async Task CorruptNativeAccountingFailsClosedWithoutProviderDetails()
    {
        await using var factory = new Factory(); factory.Repository.Corrupt = true; using var client = Client(factory);
        using var response = await client.GetAsync(List()); Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("private-provider-detail", body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body); Assert.Equal("request_failed", json.RootElement.GetProperty("code").GetString());
    }
    [Fact]
    public async Task CallerCancellationReachesOwnedNativeRead()
    {
        await using var factory = new Factory(); factory.Repository.Wait = true; using var client = Client(factory); using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync(List(), cancellation.Token);
        try
        {
            await NativeReadTestBarrier.WaitForStartAsync(factory.Repository.Started.Task, pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await factory.Repository.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }
    }
    private static string Remove(string route, string field) => route.Split('?')[0] + "?" + string.Join('&', route.Split('?')[1].Split('&').Where(value => !value.StartsWith(field + "=", StringComparison.Ordinal)));
    private static SqlVolumePlanningWindowReceipt Receipt(Guid run,int minute,int count=2) => new(run,2,From.AddMinutes(minute),"succeeded","none",0,count,count,"available","observed_volumes");
    private static SqlVolumePlanningWindowVolume File(int id) => new(new string(id==1?'a':'b',64),"volume_id",1,From.AddMinutes(10).AddSeconds(-1),From.AddMinutes(10),"9007199254740993","9007199254740992",new("visibility_gaps",0,null,[]));
    private static SqlVolumePlanningWindowLookback Summary() => new(From.AddMinutes(10).AddDays(-14),From.AddMinutes(10),"partial","visibility_gaps",new(2,1,1,0,1,0,0,2,true));
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISqlVolumePlanningWindowReadPort>(); services.AddSingleton<ISqlVolumePlanningWindowReadPort>(Repository);
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new("S-1-5-21-1111"), [ApplicationRole.Viewer], false, [new(Target)]),
                    new(new("S-1-5-21-2222"), [ApplicationRole.Operator], false, [new(Target)]),
                    new(new("S-1-5-21-3333"), [ApplicationRole.TargetAdministrator], false, [new(Target)]),
                    new(new("S-1-5-21-4444"), [ApplicationRole.Viewer], false, [new(Other)]),
                    new(new("S-1-5-21-5555"), [ApplicationRole.Auditor], false, [new(Target)])]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions, Authentication>(Authentication.SchemeName, static _ => { });
            });
        }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "VolumePlanning.Window.History.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-File-History-Identity"];
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer" => "S-1-5-21-1111", "operator" => "S-1-5-21-2222", "admin" => "S-1-5-21-3333", "mixed" => "S-1-5-21-4444", "auditor" => "S-1-5-21-5555", _ => "" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "S-1-5-21-7777"), new(ClaimTypes.GroupSid, group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid, "S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
    private sealed class Repository : ISqlVolumePlanningWindowReadPort
    {
        internal int Reads { get; private set; }
        internal bool Missing { get; set; }
        internal bool Corrupt { get; set; }
        internal bool Wait { get; set; }
        internal SqlVolumePlanningWindowReadException? Failure { get; set; }
        internal SqlVolumePlanningWindowListRequest? ListRequest { get; private set; }
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SqlVolumePlanningWindowPage?> ListAsync(SqlVolumePlanningWindowListRequest request, CancellationToken cancellationToken)
        {
            Reads++; ListRequest = request; Started.TrySetResult();
            if (Wait)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            if (Failure is not null) throw Failure;
            if (Corrupt) throw new InvalidDataException("private-provider-detail");
            var all = new[] { Receipt(Run, 10), Receipt(OlderRun, 5) };
            var items = all.Where(item => request.Cursor is null || item.CompletedAtUtc < request.Cursor.AfterCompletedAtUtc).Take(request.Limit).ToArray();
            var next = request.Limit == 1 && request.Cursor is null ? new SqlVolumePlanningWindowCursor("receipts", Target, 5, From, To, To, To, Epoch, 1, null, null, items[0].CompletedAtUtc, items[0].RunId, null) : null;
            return new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow), "partial", "revision_boundary", new(2, 2, 0, 0, 0, 0, 0, 4, true), items, next);
        }
        public ValueTask<SqlVolumePlanningWindowSnapshotPage?> GetAsync(SqlVolumePlanningWindowSnapshotRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (Failure is not null) throw Failure;
            var all = new[] { File(1), File(2) };
            SqlVolumePlanningWindowVolume[] items = Missing ? [] : all.Where(item => request.Cursor is null || string.CompareOrdinal(item.VolumeKey, request.Cursor.AfterVolumeKey) > 0).Take(request.Limit).ToArray();
            var next = !Missing && items.Length < all.Length && request.Cursor is null ? new SqlVolumePlanningWindowCursor("snapshot", Target, 5, From, To, To, To, Epoch, request.Limit,
                Run, 2, null, null, items[^1].VolumeKey) : null;
            return ValueTask.FromResult<SqlVolumePlanningWindowSnapshotPage?>(new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow),
                Missing ? "unavailable" : "available", Missing ? "snapshot_not_retained" : "observed_volumes", Run, 2, Missing ? null : Receipt(Run, 10, all.Length), Missing ? null : Summary(), items, next));
        }
    }
}
