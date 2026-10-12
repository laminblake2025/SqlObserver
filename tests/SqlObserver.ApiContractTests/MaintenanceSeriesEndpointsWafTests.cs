using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.ApiContractTests;

public sealed class MaintenanceSeriesEndpointsWafTests
{
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Series = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static string Path => $"/api/v1/observation-targets/{Target:D}/alerts/maintenance/series";
    private static Dictionary<string, object?> Definition() => new()
    {
        ["frequency"] = "daily", ["interval"] = 1, ["startDate"] = "2026-11-01", ["endDate"] = "2026-11-03",
        ["localStartTime"] = "01:30", ["durationMinutes"] = 120, ["timeZoneId"] = "America/New_York",
        ["weekdays"] = Array.Empty<int>(), ["dayOfMonth"] = null, ["gapPolicy"] = "skip", ["foldPolicy"] = "later", ["reason"] = "Planned maintenance",
    };
    private static Dictionary<string, object?> CreateBody() => new() { ["operationId"] = "dddddddd-dddd-4ddd-8ddd-dddddddddddd", ["seriesId"] = Series.ToString("D"), ["definition"] = Definition() };
    private static HttpClient Client(Factory factory, string? identity = "admin")
    { HttpClient client = factory.CreateClient(); if (identity is not null) client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader, identity); return client; }
    [Fact]
    public async Task CreateUsesProductionExpansionAndReturnsImmutableCountsThenCurrentRead()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        using var response = await client.PostAsJsonAsync(Path, CreateBody()); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.IsType<MaintenanceSeriesWriteRequest>(factory.Repository.Last);
        Assert.Equal(AdministrativeAuditAction.CreateMaintenanceSeries, request.Audit.Action); Assert.Equal(3, request.Occurrences.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), request.Occurrences[0].StartsAtUtc);
        Assert.True(request.Occurrences[0].Ambiguous); Assert.Equal(TimeSpan.FromHours(2), request.Occurrences[0].EndsAtUtc - request.Occurrences[0].StartsAtUtc);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("createdOccurrences").GetInt32()); Assert.Equal(0, json.RootElement.GetProperty("updatedOccurrences").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("definition", out _));
        using var current = await client.GetAsync($"{Path}/{Series:D}"); Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("no-store", current.Headers.CacheControl!.ToString()); using var status = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal("later", status.RootElement.GetProperty("definition").GetProperty("foldPolicy").GetString());
        Assert.Equal("01:30", status.RootElement.GetProperty("definition").GetProperty("localStartTime").GetString());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("cancelledFromDate").ValueKind);
    }
    [Fact]
    public async Task UpdateAndCancelBindNativeRevisionAndCivilCutoff()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        var update = new { operationId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd", expectedRevision = 7, effectiveFromDate = "2026-11-02", definition = Definition() };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Path}/{Series:D}", update)).StatusCode);
        Assert.Equal(7, factory.Repository.Last!.ExpectedRevision); Assert.Equal(new DateOnly(2026, 11, 2), factory.Repository.Last.EffectiveFromDate);
        Assert.Equal(3, factory.Repository.Last.Occurrences.Count); string updateDigest = factory.Repository.Last.RequestDigest;
        var cancel = new { operationId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", expectedRevision = 8, effectiveFromDate = "2026-11-03", reason = "Cancel remaining changes" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Path}/{Series:D}/cancel", cancel)).StatusCode);
        Assert.Equal(AdministrativeAuditAction.CancelMaintenanceSeries, factory.Repository.Last.Audit.Action); Assert.Empty(factory.Repository.Last.Occurrences);
        Assert.Null(factory.Repository.Last.Definition); Assert.NotEqual(updateDigest, factory.Repository.Last.RequestDigest);
    }
    [Fact]
    public async Task PreviewUsesServerClockAndNamesSkippedCalendarDatesWithoutWrites()
    {
        await using var factory = new Factory(); using var client = Client(factory); var definition = Definition();
        definition["frequency"] = "monthly"; definition["startDate"] = "2027-01-31"; definition["endDate"] = "2027-03-31"; definition["dayOfMonth"] = 31;
        using var response = await client.PostAsJsonAsync($"{Path}/preview", new { seriesId = Series, definition, effectiveFromDate = "2027-02-01" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(factory.Repository.Last);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal("civil-v1", root.GetProperty("expansionVersion").GetString()); Assert.Equal(64, root.GetProperty("definitionDigest").GetString()!.Length);
        var row = Assert.Single(root.GetProperty("occurrences").EnumerateArray()); Assert.Equal("2027-03-31", row.GetProperty("localDate").GetString());
        var skipped = Assert.Single(root.GetProperty("skippedDates").EnumerateArray()); Assert.Equal("2027-02-31", skipped.GetProperty("localDate").GetString());
        Assert.Equal("missing_month_day", skipped.GetProperty("reason").GetString()); Assert.NotEqual(Guid.Empty, row.GetProperty("windowId").GetGuid());
    }
    [Fact]
    public async Task PreviewOfEntirelySkippedGapReturnsExplanationRatherThanFabricatedWindow()
    {
        await using var factory = new Factory(); using var client = Client(factory); var definition = Definition();
        definition["startDate"] = "2027-03-14"; definition["endDate"] = "2027-03-14"; definition["localStartTime"] = "02:30";
        using var response = await client.PostAsJsonAsync($"{Path}/preview", new { seriesId = Series, definition, effectiveFromDate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Null(factory.Repository.Last);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Empty(json.RootElement.GetProperty("occurrences").EnumerateArray());
        Assert.Equal("nonexistent_local_time", Assert.Single(json.RootElement.GetProperty("skippedDates").EnumerateArray()).GetProperty("reason").GetString());
    }
    [Theory]
    [InlineData("foldPolicy", null)] [InlineData("gapPolicy", null)] [InlineData("gapPolicy", "move")]
    [InlineData("timeZoneId", "Eastern Standard Time")] [InlineData("localStartTime", "01:30:00")]
    [InlineData("endDate", "2032-11-01")] [InlineData("frequency", "hourly")]
    public async Task InvalidDefinitionsNeverReachStorage(string field, string? value)
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = CreateBody(); var definition = (Dictionary<string, object?>)body["definition"]!; definition[field] = value;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, body)).StatusCode); Assert.Null(factory.Repository.Last);
    }
    [Theory]
    [InlineData("requestDigest")] [InlineData("occurrences")] [InlineData("expansionVersion")] [InlineData("actorSid")]
    public async Task ServerOwnedMutationFieldsAreRejected(string field)
    {
        await using var factory = new Factory(); using var client = Client(factory); var body = CreateBody(); body[field] = "caller-owned";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, body)).StatusCode); Assert.Null(factory.Repository.Last);
    }
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)] [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndTargetAdministratorAreRequired(string? identity, HttpStatusCode expected)
    {
        await using var factory = new Factory(); using var client = Client(factory, identity);
        Assert.Equal(expected, (await client.GetAsync(Path)).StatusCode); Assert.Equal(expected, (await client.PostAsJsonAsync(Path, CreateBody())).StatusCode);
        Assert.Null(factory.Repository.Last);
    }
    [Fact]
    public async Task MixedTargetGrantsDoNotBorrowTargetAdministratorAuthority()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        string other = Path.Replace(Target.ToString("D"), Factory.Other.ToString("D"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(other)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(other, CreateBody())).StatusCode); Assert.Null(factory.Repository.Last);
    }
    [Theory]
    [InlineData(409)] [InlineData(404)] [InlineData(503)]
    public async Task NativeConflictsAndUnavailableResponsesKeepRealStatus(int status)
    {
        await using var factory = new Factory(); factory.Repository.FailureStatus = status; using var client = Client(factory);
        Assert.Equal((HttpStatusCode)status, (await client.PostAsJsonAsync(Path, CreateBody())).StatusCode);
    }
    [Fact]
    public async Task PageBoundsCanonicalAnchorsAndMissingSeriesAreHandledWithoutPrivateSequence()
    {
        await using var factory = new Factory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?limit=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Path}?beforeSeriesId=not-an-id")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Path}/{Guid.NewGuid():D}")).StatusCode);
        Guid anchor = Guid.NewGuid(); Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Path}/{Series:D}/occurrences?limit=50&beforeWindowId={anchor:D}")).StatusCode);
        Assert.Equal(anchor, factory.Repository.Anchor);
    }
    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        internal Repository Repository { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMaintenanceSeriesRepositoryPort>(); services.RemoveAll<IAdministrativeAuditPort>(); services.RemoveAll<TimeProvider>();
                services.AddSingleton<IMaintenanceSeriesRepositoryPort>(Repository); services.AddSingleton<IAdministrativeAuditPort>(new Audit()); services.AddSingleton<TimeProvider>(new Clock());
                services.AddSingleton(new WindowsGroupRoleResolver([
                    new(new(M8AlertTestAuthenticationHandler.AdminGroupSid), [ApplicationRole.TargetAdministrator], false, [new(Target)]),
                    new(new(M8AlertTestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.Viewer], true),
                ]));
                services.AddAuthentication(options => { options.DefaultAuthenticateScheme = SeriesAuthentication.SchemeName; options.DefaultChallengeScheme = SeriesAuthentication.SchemeName; })
                    .AddScheme<AuthenticationSchemeOptions, SeriesAuthentication>(SeriesAuthentication.SchemeName, static _ => { });
            });
        }
    }
    private sealed class SeriesAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "Maintenance.Series.Tests";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? identity = Request.Headers[M8AlertTestAuthenticationHandler.IdentityHeader];
            if (identity is null or "") return Task.FromResult(AuthenticateResult.NoResult());
            if (identity is not ("admin" or "viewer")) return Task.FromResult(AuthenticateResult.Fail("Unknown identity"));
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "S-1-5-21-2200"), new(ClaimTypes.GroupSid, M8AlertTestAuthenticationHandler.ViewerGroupSid) };
            if (identity == "admin") claims.Add(new(ClaimTypes.GroupSid, M8AlertTestAuthenticationHandler.AdminGroupSid));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero); }
    private sealed class Audit : IAdministrativeAuditPort
    { public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), new Clock().GetUtcNow())); }
    private sealed class Repository : IMaintenanceSeriesRepositoryPort
    {
        internal MaintenanceSeriesWriteRequest? Last { get; private set; } internal int? FailureStatus { get; set; } internal Guid? Anchor { get; private set; }
        public ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(MaintenanceSeriesWriteRequest request, CancellationToken cancellationToken)
        {
            Last = request; if (FailureStatus is int status) throw new AlertRepositoryOperationException(status == 409 ? "revision_conflict" : status == 404 ? "target_not_found" : "repository_unavailable", "Synthetic failure", status, new InvalidOperationException());
            var now = new Clock().GetUtcNow(); return ValueTask.FromResult(new MaintenanceSeriesWriteReceipt(new(new(Guid.NewGuid()), now), request.SeriesId,
                (request.ExpectedRevision ?? 0) + 1, now, request.Occurrences.Count, 0, 0, 0));
        }
        public ValueTask<MaintenanceSeriesStatus?> GetAsync(MonitoredInstanceId targetId, Guid seriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            var definition = new MaintenanceSeriesDefinition("America/New_York", new(2026, 11, 1), new(2026, 11, 3), new(1, 30), 120, MaintenanceSeriesFrequency.Daily, 1, [], null, "skip", MaintenanceFoldPolicy.Later, "Planned maintenance");
            var now = new Clock().GetUtcNow(); return ValueTask.FromResult<MaintenanceSeriesStatus?>(seriesId != Series ? null : new(targetId, seriesId, 1, definition, null, now, now, "civil-v1", now, 3));
        }
        public ValueTask<MaintenanceSeriesListPage> ListAsync(MonitoredInstanceId targetId, int limit, Guid? beforeSeriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        { Anchor = beforeSeriesId; return ValueTask.FromResult(new MaintenanceSeriesListPage(targetId, new Clock().GetUtcNow(), [], null)); }
        public ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(MonitoredInstanceId targetId, Guid seriesId, int limit, Guid? beforeWindowId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        { Anchor = beforeWindowId; return ValueTask.FromResult(new MaintenanceSeriesOccurrencePage(targetId, seriesId, new Clock().GetUtcNow(), [], null)); }
    }
}
