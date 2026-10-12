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
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class SqlVolumeWindowHistoryHttpTests
{
    private static readonly Guid Target = Guid.Parse("11111111-1111-4111-8111-111111111111"), Other = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly string Key = new('a', 64), OtherKey = new('c', 64), Epoch = new('b', 64);
    private static readonly DateTimeOffset To = Round(DateTimeOffset.UtcNow), From = To.AddHours(-1);
    private static readonly Guid FirstRun = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), LastRun = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static DateTimeOffset Round(DateTimeOffset value) => new(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    private static string Utc(DateTimeOffset value) => Uri.EscapeDataString(value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
    private static string Query(int? limit = null) => $"targetRevision=5&fromUtc={Utc(From)}&toUtc={Utc(To)}" + (limit is { } n ? $"&limit={n}" : "");
    private static string Catalog(int? limit = null) => $"/api/v1/observation-targets/{Target:D}/resources/volumes/history?" + Query(limit);
    private static string History(string? key = null, int? limit = null) => $"/api/v1/observation-targets/{Target:D}/resources/volumes/{key ?? Key}/history?sampleTargetRevision=2&" + Query(limit);
    private static HttpClient Client(Factory factory, string? identity = "viewer")
    { var client = factory.CreateClient(); if (identity is not null) client.DefaultRequestHeaders.Add("X-Volume-History-Identity", identity); return client; }
    [Fact]
    public async Task HistoricalReadUsesCompletionWindowCurrentFenceAndOlderExactSampleRevision()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.GetAsync(History()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.True(response.Headers.CacheControl.Private);
        var request = Assert.IsType<SqlVolumeWindowHistoryExactRequest>(factory.Repository.ExactRequest);
        Assert.Equal(5, request.TargetRevision); Assert.Equal(2, request.SampleTargetRevision); Assert.Equal(From, request.FromUtc); Assert.Equal(To, request.ToUtc);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout.Value);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("sampleTargetRevision").GetInt64()); Assert.Equal(Key, root.GetProperty("volumeKey").GetString());
        Assert.Equal("9223372036854775807", root.GetProperty("items")[0].GetProperty("totalBytes").GetString());
        Assert.Equal("0", root.GetProperty("items")[0].GetProperty("availableBytes").GetString());
        Assert.True(root.GetProperty("items")[0].TryGetProperty("completedAtUtc", out _));
        Assert.False(root.TryGetProperty("nextAnchor", out _)); Assert.DoesNotContain("volumePath", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    [Fact]
    public async Task CatalogContinuationIsProtectedAndBoundToWindowRevisionLimitAndReadFamily()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var first = JsonDocument.Parse(await client.GetStringAsync(Catalog(1)));
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length <= SqlObserver.Server.SqlVolumeWindowHistoryCursorProtector.MaximumTokenLength);
        using var second = JsonDocument.Parse(await client.GetStringAsync(Catalog(1) + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(OtherKey, second.RootElement.GetProperty("items")[0].GetProperty("volumeKey").GetString()); Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextCursor").ValueKind);
        int reads = factory.Repository.Reads;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog(2) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog(1).Replace("targetRevision=5", "targetRevision=6", StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog(1).Replace(Utc(From), Utc(From.AddMinutes(1)), StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(History(limit: 1) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog(1) + "&cursor=" + Uri.EscapeDataString(cursor[..^2] + "!!"))).StatusCode);
        Assert.Equal(reads, factory.Repository.Reads);
    }
    [Fact]
    public async Task ExactContinuationNeverSubstitutesAnotherVolumeOrSampleRevision()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var first = JsonDocument.Parse(await client.GetStringAsync(History(limit: 1))); string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var last = JsonDocument.Parse(await client.GetStringAsync(History(limit: 1) + "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(LastRun, last.RootElement.GetProperty("items")[0].GetProperty("runId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(History(OtherKey, 1) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(History(limit: 1).Replace("sampleTargetRevision=2", "sampleTargetRevision=3", StringComparison.Ordinal) + "&cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
    }
    [Fact]
    public void ProtectedCursorExpiryIsBoundToNativeIssuedTimeWithoutRenewal()
    {
        var now = Round(DateTimeOffset.UtcNow);
        var protector = new SqlObserver.Server.SqlVolumeWindowHistoryCursorProtector(new EphemeralDataProtectionProvider());
        var cursor = new SqlVolumeWindowHistoryCursor("catalog", Target, 5, From, To, To, now, Epoch, 25, null, null, 2, Key, null, null);
        string token = protector.Protect(cursor); Assert.Equal(cursor, protector.Unprotect(token));
        Assert.Throws<ArgumentException>(() => protector.Unprotect(token[..^2] + "!!"));
        var expired = cursor with { IssuedAtUtc = now.AddMinutes(-16), CutoffUtc = now.AddMinutes(-17) };
        Assert.Throws<ArgumentException>(() => protector.Unprotect(protector.Protect(expired)));
    }
    [Theory]
    [InlineData("operator")] [InlineData("admin")]
    public async Task SameTargetSupportedRolesReadBothRoutes(string identity)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Catalog())).StatusCode); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(History())).StatusCode);
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)] [InlineData("mixed", HttpStatusCode.Forbidden)] [InlineData("auditor", HttpStatusCode.Forbidden)]
    public async Task DeniedReaderNeverReachesRepository(string? identity, HttpStatusCode status)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(status, (await client.GetAsync(Catalog())).StatusCode); Assert.Equal(status, (await client.GetAsync(History())).StatusCode); Assert.Equal(0, factory.Repository.Reads);
    }
    [Theory]
    [InlineData("fromUtc=2026-10-03T11:00:00%2B00:00")] [InlineData("fromUtc=2026-10-03T11:00:00.0000001Z")]
    [InlineData("fromUtc=2026-02-30T11:00:00Z")] [InlineData("targetRevision=0")] [InlineData("targetRevision=05")]
    [InlineData("limit=999999999999")] [InlineData("limit=101")]
    public async Task MalformedBoundsNeverReachRepository(string replacement)
    {
        await using var factory = new Factory(); using var client = Client(factory);
        string field = replacement.Split('=')[0], original = Catalog().Split('?')[1];
        var fields = original.Split('&').Where(value => !value.StartsWith(field + "=", StringComparison.Ordinal)).Append(replacement);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog().Split('?')[0] + "?" + string.Join('&', fields))).StatusCode); Assert.Equal(0, factory.Repository.Reads);
    }
    [Fact]
    public async Task UnknownNativeMetadataAndRepeatedFieldsCannotOverrideProtectedContext()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog() + "&cutoffUtc=" + Utc(To))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog() + "&readEpoch=" + Epoch)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog() + "&targetRevision=5")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Catalog().Replace("&toUtc=" + Utc(To), "", StringComparison.Ordinal))).StatusCode); Assert.Equal(0, factory.Repository.Reads);
    }
    [Fact]
    public async Task PermissionDeniedCollectionRemains200GapRatherThanReaderDenialOrZero()
    {
        await using var factory = new Factory(); factory.Repository.CollectionDenied = true; using var client = Client(factory);
        using var response = await client.GetAsync(History()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("coverage").GetProperty("hasVisibilityGap").GetBoolean());
        Assert.Equal("permission_denied", json.RootElement.GetProperty("items")[0].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("items")[0].GetProperty("availableBytes").ValueKind);
    }
    [Theory]
    [InlineData(409, "history_context_changed")] [InlineData(422, "history_window_overflow")] [InlineData(503, "history_unavailable")]
    public async Task NativeEpochOverflowAndAvailabilityRefusalsKeepTheirStatus(int status, string code)
    {
        await using var factory = new Factory(); factory.Repository.Failure = new(code, "Choose a narrower window or retry this read.", status); using var client = Client(factory);
        using var response = await client.GetAsync(Catalog()); Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISqlVolumeWindowHistoryRepositoryPort>(); services.AddSingleton<ISqlVolumeWindowHistoryRepositoryPort>(Repository);
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new("S-1-5-21-1111"), [ApplicationRole.Viewer], false, [new(Target)]),
                    new(new("S-1-5-21-2222"), [ApplicationRole.Operator], false, [new(Target)]),
                    new(new("S-1-5-21-3333"), [ApplicationRole.TargetAdministrator], false, [new(Target)]),
                    new(new("S-1-5-21-4444"), [ApplicationRole.Viewer], false, [new(Other)]),
                    new(new("S-1-5-21-5555"), [ApplicationRole.Auditor], false, [new(Target)]),
                ]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = Authentication.SchemeName; options.DefaultChallengeScheme = Authentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions, Authentication>(Authentication.SchemeName, static _ => { });
            });
        }
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "Volume.History.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers["X-Volume-History-Identity"]; if (string.IsNullOrEmpty(identity)) return Task.FromResult(AuthenticateResult.NoResult());
            string group = identity switch { "viewer" => "S-1-5-21-1111", "operator" => "S-1-5-21-2222", "admin" => "S-1-5-21-3333", "mixed" => "S-1-5-21-4444", "auditor" => "S-1-5-21-5555", _ => "" };
            if (group.Length == 0) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "S-1-5-21-7777"), new(ClaimTypes.GroupSid, group) };
            if (identity == "mixed") claims.Add(new(ClaimTypes.GroupSid, "S-1-5-21-5555"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
    private sealed class Repository : ISqlVolumeWindowHistoryRepositoryPort
    {
        internal int Reads { get; private set; }
        internal bool CollectionDenied { get; set; }
        internal SqlVolumeWindowHistoryReadException? Failure { get; set; }
        internal SqlVolumeWindowHistoryExactRequest? ExactRequest { get; private set; }
        private static SqlVolumeWindowHistoryCatalogItem Item(string key) => new(2, key, "volume_id", 2, From.AddMinutes(5), From.AddMinutes(10),
            "9223372036854775807", "100", "9223372036854775807", "90", "eligible", "-10", "0");
        private static SqlVolumeWindowHistorySample Sample(Guid id, int minute) => new(id, From.AddMinutes(minute), From.AddMinutes(minute), From.AddMinutes(minute),
            "succeeded", "none", 0, "available", "observed_capacity", 3, "9223372036854775807", "0");
        public ValueTask<SqlVolumeWindowHistoryCatalogPage?> ReadCatalogAsync(SqlVolumeWindowHistoryCatalogRequest request, CancellationToken cancellationToken)
        {
            Reads++; if (Failure is not null) throw Failure;
            SqlVolumeWindowHistoryCatalogItem[] all = [Item(Key), Item(OtherKey)];
            var items = all.Where(item => request.Cursor is null || string.CompareOrdinal(item.VolumeKey, request.Cursor.AfterVolumeKey) > 0).Take(request.Limit).ToArray();
            SqlVolumeWindowHistoryCursor? next = request.Limit == 1 && request.Cursor is null
                ? new("catalog", Target, 5, From, To, To, To, Epoch, 1, null, null, 2, Key, null, null) : null;
            return ValueTask.FromResult<SqlVolumeWindowHistoryCatalogPage?>(new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow), "available", "retained_window", new(2, 2, 0, 4, 30, false), items, next));
        }
        public ValueTask<SqlVolumeWindowHistoryExactPage?> ReadHistoryAsync(SqlVolumeWindowHistoryExactRequest request, CancellationToken cancellationToken)
        {
            Reads++; ExactRequest = request; if (Failure is not null) throw Failure;
            SqlVolumeWindowHistorySample[] all = [Sample(FirstRun, 5), Sample(LastRun, 10)];
            if (CollectionDenied) all = [all[0] with { Outcome = "permission_denied", State = "unavailable", Reason = "collection_failed", ObservedAtUtc = null, CollectedAtUtc = null, MappedFileCount = null, TotalBytes = null, AvailableBytes = null }, all[1]];
            var items = all.Where(item => request.Cursor is null || item.CompletedAtUtc > request.Cursor.AfterCompletedAtUtc).Take(request.Limit).ToArray();
            SqlVolumeWindowHistoryCursor? next = request.Limit == 1 && request.Cursor is null
                ? new("history", Target, 5, From, To, To, To, Epoch, 1, 2, request.VolumeKey, null, null, all[0].CompletedAtUtc, FirstRun) : null;
            return ValueTask.FromResult<SqlVolumeWindowHistoryExactPage?>(new(Target, 5, From, To, To, Epoch, Round(DateTimeOffset.UtcNow), CollectionDenied ? "partial" : "available", CollectionDenied ? "visibility_gaps" : "retained_window",
                2, request.VolumeKey, "volume_id", new(2, CollectionDenied ? 1 : 2, CollectionDenied ? 1 : 0, CollectionDenied ? 1 : 2, 30, CollectionDenied), items, next));
        }
    }
}
