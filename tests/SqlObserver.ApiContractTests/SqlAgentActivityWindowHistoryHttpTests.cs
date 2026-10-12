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

public sealed class SqlAgentActivityWindowHistoryHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222"),
        Run = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Failed = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset To = Round(DateTimeOffset.UtcNow), From = To.AddHours(-1);
    private static readonly string Epoch = new('a', 64);
    private static DateTimeOffset Round(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static string Query() => "targetRevision=5&fromUtc=" + Utc(From) + "&toUtc=" + Utc(To);
    private static string List() => $"/api/v1/observation-targets/{Target:D}/sql-agent/activity/observations?" + Query();
    private static string Exact() => $"/api/v1/observation-targets/{Target:D}/sql-agent/activity/observations/{Run:D}?" + Query() + "&sampleTargetRevision=2";
    private static HttpClient Client(Factory factory, string? identity = "viewer")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null) client.DefaultRequestHeaders.Add("X-Agent-Activity-History-Identity", identity);
        return client;
    }
    [Fact]
    public async Task ProductionRoutesAreAuthenticatedAndListsRemainBoundedSummaryOnly()
    {
        await using var factory = new Factory(); using var client = Client(factory); using var response = await client.GetAsync(List());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(Failed, root.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean()); Assert.False(root.GetProperty("serviceSessionAssociationQualified").GetBoolean());
        Assert.Equal("Absent", root.GetProperty("nativeQualification").GetString()); Assert.False(root.TryGetProperty("nextAnchor", out _));
        Assert.False(root.GetProperty("items")[1].TryGetProperty("jobs", out _)); Assert.False(root.GetProperty("items")[1].TryGetProperty("capture", out _));
        Assert.Equal(25, factory.Repository.ListRequest!.Limit); Assert.Equal(TimeSpan.FromSeconds(5), factory.Repository.ListRequest.Timeout.Value);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(static e => e.RoutePattern.RawText?.Contains("/sql-agent/activity/observations", StringComparison.Ordinal) == true).ToArray();
        Assert.Equal(2, endpoints.Length);
        foreach (var endpoint in endpoints) { Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()); Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()); }
    }
    [Fact]
    public async Task ExactDetailPreservesSevenDigitLocalSourceDatesAndWithholdsPrivateEnrollment()
    {
        await using var factory = new Factory(); using var client = Client(factory); using var response = await client.GetAsync(Exact());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); string text = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(text);
        var capture = json.RootElement.GetProperty("capture"); var job = capture.GetProperty("jobs")[0];
        Assert.Equal(1, capture.GetProperty("sourceSchemaVersion").GetInt32()); Assert.Equal(3, capture.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal("ServiceNotRunning", capture.GetProperty("gap").GetString()); Assert.Equal("Unknown", job.GetProperty("candidateState").GetString());
        Assert.Equal("2026-10-07T11:02:00.1234567", job.GetProperty("startExecutionLocal").GetString());
        Assert.Equal("2026-10-07T11:49:59.1234567Z", capture.GetProperty("before").GetProperty("sourceObservedAtUtc").GetString());
        Assert.DoesNotContain("enrollmentId", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("evidenceSha256", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedWindowsSid", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("definitionSha256", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(job.TryGetProperty("elapsedSeconds", out _)); Assert.False(job.TryGetProperty("currentStep", out _)); Assert.False(job.TryGetProperty("startedAtUtc", out _));
    }
    [Fact]
    public async Task FullFiveHundredTwelveJobExactReceiptFitsBoundAndRetainsTruncation()
    {
        await using var factory = new Factory(); factory.Repository.MaximumJobs = true; using var client = Client(factory);
        using var response = await client.GetAsync(Exact()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(); Assert.True(bytes.Length <= 512 * 1024);
        using var json = JsonDocument.Parse(bytes); var capture = json.RootElement.GetProperty("capture");
        Assert.Equal(512, capture.GetProperty("jobs").GetArrayLength()); Assert.Equal(513, capture.GetProperty("sourceRowsRead").GetInt32());
        Assert.True(capture.GetProperty("truncated").GetBoolean()); Assert.Equal("SourceTruncated", capture.GetProperty("gap").GetString());
        Assert.All(capture.GetProperty("jobs").EnumerateArray(), job => Assert.Equal("Unknown", job.GetProperty("candidateState").GetString()));
    }
    [Theory]
    [InlineData("viewer")] [InlineData("operator")] [InlineData("admin")]
    public async Task SameTargetReadRolesCanReachHistoryAndIndependentDetail(string identity)
    { await using var factory = new Factory(); using var client = Client(factory, identity); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(List())).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Exact())).StatusCode); }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)] [InlineData("mixed", HttpStatusCode.Forbidden)] [InlineData("auditor", HttpStatusCode.Forbidden)] [InlineData("security", HttpStatusCode.Forbidden)]
    public async Task UnauthorizedAndMixedTargetReadsNeverReachStorage(string? identity, HttpStatusCode expected)
    { await using var factory = new Factory(); using var client = Client(factory, identity); Assert.Equal(expected, (await client.GetAsync(List())).StatusCode); Assert.Equal(expected, (await client.GetAsync(Exact())).StatusCode); Assert.Equal(0, factory.Repository.Reads); }
    [Theory]
    [InlineData("&unknown=1")] [InlineData("&targetRevision=5")] [InlineData("&limit=101")] [InlineData("&limit=0")] [InlineData("&limit=01")] [InlineData("&cursor=opaque")]
    public async Task StrictQueriesFailBeforeHistoryReads(string suffix)
    {
        await using var factory = new Factory(); using var client = Client(factory); using var response = await client.GetAsync(List() + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, factory.Repository.Reads);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal("invalid_request", json.RootElement.GetProperty("code").GetString());
    }
    [Theory]
    [InlineData("fromUtc", "2026-10-07T00:00:00.1234567Z")] [InlineData("fromUtc", "2026-10-07T00:00:00+00:00")]
    [InlineData("fromUtc", "2026-02-30T00:00:00Z")] [InlineData("targetRevision", "9007199254740992")]
    public async Task RepositoryWindowClocksAndRevisionsRemainStrict(string field, string value)
    {
        await using var factory = new Factory(); using var client = Client(factory); string route = List();
        string query = string.Join('&', route.Split('?')[1].Split('&').Where(v => !v.StartsWith(field + "=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route.Split('?')[0] + "?" + query + "&" + field + "=" + Uri.EscapeDataString(value))).StatusCode);
        Assert.Equal(0, factory.Repository.Reads);
    }
    [Theory]
    [InlineData("&limit=1")] [InlineData("&cursor=abc")] [InlineData("&sampleTargetRevision=2")]
    public async Task ExactPinsRejectPagingAndRepeatedIdentity(string suffix)
    { await using var factory = new Factory(); using var client = Client(factory); Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Exact() + suffix)).StatusCode); Assert.Equal(0, factory.Repository.Reads); }
    [Fact]
    public async Task ProtectedCursorRetainsOriginalCutoffAndCannotRebindLimitOrTarget()
    {
        await using var factory = new Factory(); using var client = Client(factory); using var first = await client.GetAsync(List() + "&limit=1");
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync()); string cursor = Uri.EscapeDataString(json.RootElement.GetProperty("nextCursor").GetString()!);
        using var second = await client.GetAsync(List() + "&limit=1&cursor=" + cursor); Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var tail = JsonDocument.Parse(await second.Content.ReadAsStringAsync()); Assert.Equal(Run, tail.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal(json.RootElement.GetProperty("cutoffUtc").GetString(), tail.RootElement.GetProperty("cutoffUtc").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(List() + "&limit=2&cursor=" + cursor)).StatusCode);
        var protector = factory.Services.GetRequiredService<SqlAgentActivityWindowHistoryCursorProtector>();
        var foreign = new SqlAgentActivityWindowHistoryCursor(Other, 5, From, To, To, To, Epoch, 1, To.AddMinutes(-5), Failed);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(List() + "&limit=1&cursor=" + Uri.EscapeDataString(protector.Protect(foreign)))).StatusCode);
    }
    [Fact]
    public async Task ExpiredCursorAndCorruptDetailCannotBecomeRetainedSuccess()
    {
        await using var factory = new Factory(); using var client = Client(factory); var protector = factory.Services.GetRequiredService<SqlAgentActivityWindowHistoryCursorProtector>();
        var expired = new SqlAgentActivityWindowHistoryCursor(Target, 5, From.AddHours(-1), To.AddHours(-1), To.AddHours(-1), To.AddHours(-1), Epoch, 1, To.AddHours(-1).AddMinutes(-5), Failed);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(List() + "&limit=1&cursor=" + Uri.EscapeDataString(protector.Protect(expired)))).StatusCode);
        Assert.Equal(0, factory.Repository.Reads); factory.Repository.BadGap = true;
        using var response = await client.GetAsync(Exact()); Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("ServiceNotRunning", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task ListOverflowLeavesExactReadAndMissingCanonicalSelectionHasNoSubstitute()
    {
        await using var factory = new Factory(); using var client = Client(factory); factory.Repository.Failure = new("agent_activity_window_overflow", "Narrow the window.", 422);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(List())).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Exact())).StatusCode);
        factory.Repository.Missing = true; using var response = await client.GetAsync(Exact()); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("observation_not_retained", json.RootElement.GetProperty("reason").GetString()); Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("receipt").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("capture").ValueKind); Assert.False(json.RootElement.TryGetProperty("coverage", out _));
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataProtectionProvider>(); services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.RemoveAll<ISqlAgentActivityWindowHistoryReadPort>(); services.AddSingleton<ISqlAgentActivityWindowHistoryReadPort>(Repository);
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
        internal const string SchemeName = "Agent.Activity.History.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Agent-Activity-History-Identity"];
            if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer"=>"S-1-5-21-1111","operator"=>"S-1-5-21-2222","admin"=>"S-1-5-21-3333",
                "mixed"=>"S-1-5-21-4444","auditor"=>"S-1-5-21-5555","security"=>"S-1-5-21-6666",_=>"" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier,"S-1-5-21-7777"),new(ClaimTypes.GroupSid,group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid,"S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims,SchemeName)),SchemeName)));
        }
    }
    private sealed class Repository : ISqlAgentActivityWindowHistoryReadPort
    {
        internal int Reads { get; private set; }
        internal bool Missing { get; set; }
        internal bool MaximumJobs { get; set; }
        internal bool BadGap { get; set; }
        internal SqlAgentActivityWindowHistoryReadException? Failure { get; set; }
        internal SqlAgentActivityWindowHistoryListRequest? ListRequest { get; private set; }
        private static SqlAgentActivityWindowHistoryReceipt Item() => new(Run, 2, To.AddMinutes(-10), "succeeded", "completed", "none", 0, true, true, true, 1, 1, false, "available", "source_complete");
        private static SqlAgentActivityWindowHistoryReceipt[] Items() => [new(Failed, 3, To.AddMinutes(-5), "timed_out", "deadline_exceeded", "none", 0, true, true, false, null, null, null, "unavailable", "collection_failed"), Item()];
        public ValueTask<SqlAgentActivityWindowHistoryPage?> ListAsync(SqlAgentActivityWindowHistoryListRequest request, CancellationToken cancellationToken)
        {
            Reads++; ListRequest = request; cancellationToken.ThrowIfCancellationRequested(); if (Failure is not null) throw Failure;
            var items = Items().Where(i => request.Cursor is null || i.CompletedAtUtc < request.Cursor.AfterCompletedAtUtc).Take(request.Limit).ToArray();
            var next = request.Limit == 1 && request.Cursor is null ? new SqlAgentActivityWindowHistoryCursor(Target, 5, From, To, To, To, Epoch, 1, items[0].CompletedAtUtc, items[0].RunId) : null;
            return ValueTask.FromResult<SqlAgentActivityWindowHistoryPage?>(new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow), "partial", "visibility_gaps", new(2, 2, 1, 1, 1, 1, 0, 0, true), items, next));
        }
        private static SqlAgentActivityWindowHistoryCapture Capture() => new(1, 3,
            new("2026-10-07T11:49:59.1234567Z", new(1, 1, null, null), new(4, "2026-10-07T11:00:00.1234567")),
            new("2026-10-07T11:50:00.1234567Z", new(1, 1, null, null), new(4, "2026-10-07T11:00:00.1234567")),
            [new(Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"), 4, "2026-10-07T11:01:00.1234567", null,
                "2026-10-07T11:02:00.1234567", null, 3, "2026-10-07T11:01:30.1234567", "Unknown")], 1, false, "ServiceNotRunning");
        public ValueTask<SqlAgentActivityWindowHistoryExactPage?> GetAsync(SqlAgentActivityWindowHistoryExactRequest request, CancellationToken cancellationToken)
        {
            Reads++; cancellationToken.ThrowIfCancellationRequested(); var item = Item(); var capture = Capture();
            if (MaximumJobs)
            {
                var original = capture.Jobs[0];
                var jobs = Enumerable.Range(1, 512).Select(index => original with { JobId = Guid.Parse("00000000-0000-4000-8000-" + index.ToString("D12", CultureInfo.InvariantCulture)) }).ToArray();
                capture = capture with { Jobs = jobs, SourceRowsRead = 513, Truncated = true, Gap = "SourceTruncated" };
                item = item with { Outcome = "partial", CanonicalReason = "source_row_limit", LossKind = "source_row_limit", MinimumLostItems = 1, CountIsExact = false,
                    SourceRowsRead = 513, RetainedJobCount = 512, Truncated = true, State = "partial", Reason = "source_truncated" };
            }
            if (BadGap) capture = capture with { Gap = "None" };
            return ValueTask.FromResult<SqlAgentActivityWindowHistoryExactPage?>(new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow),
                Missing ? "unavailable" : item.State, Missing ? "observation_not_retained" : item.Reason, Run, 2, Missing ? null : item, Missing ? null : capture));
        }
    }
}
