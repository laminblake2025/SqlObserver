using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

[Collection("M10 analytics API")]
public sealed class M10AnalyticsApiContractTests : IClassFixture<M10AnalyticsApiFactory>
{
    private readonly M10AnalyticsApiFactory factory;

    public M10AnalyticsApiContractTests(M10AnalyticsApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task RuntimeRoutesReturnBoundedTargetScopedDtos()
    {
        using HttpClient client = CreateClient("viewer");
        string prefix = $"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}";
        var requests = new[]
        {
            $"{prefix}/analytics/series?metricKey=host.cpu.percent&fromUtc=2026-08-25T00:00:00Z&toUtc=2026-08-25T01:00:00Z",
            $"{prefix}/analytics/rollups?metricKey=host.cpu.percent&fromUtc=2026-08-25T00:00:00Z&toUtc=2026-08-25T01:00:00Z&limit=1",
            $"{prefix}/analytics/compare?metricKey=host.cpu.percent&leftFromUtc=2026-08-25T00:00:00Z&leftToUtc=2026-08-25T01:00:00Z&rightFromUtc=2026-08-26T00:00:00Z&rightToUtc=2026-08-26T01:00:00Z",
            $"{prefix}/analytics/baselines?metricKey=host.cpu.percent&fromUtc=2026-08-25T00:00:00Z&toUtc=2026-08-25T01:00:00Z",
            $"{prefix}/analytics/forecasts?metricKey=host.cpu.percent&horizonDays=7",
            $"{prefix}/analytics/incidents?fromUtc=2026-08-25T00:00:00Z&toUtc=2026-08-25T01:00:00Z",
            $"{prefix}/analytics/jobs?limit=1",
            $"{prefix}/analytics/host/status?limit=1",
            $"{prefix}/analytics/host/metrics?limit=1",
            $"{prefix}/analytics/replication/status?limit=1",
            $"{prefix}/analytics/replication/evidence?limit=1",
            $"{prefix}/analytics/diagnostics/search?limit=1",
            $"{prefix}/analytics/evidence-packets?limit=1",
            $"{prefix}/analytics/backfill?limit=1",
            $"{prefix}/host/status?limit=1",
            $"{prefix}/replication/status?limit=1",
        };

        foreach (string request in requests)
        {
            HttpResponseMessage response = await client.GetAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(body.Length < 1_048_576, request);
            Assert.Contains("targetId", body, StringComparison.Ordinal);
            Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(factory.Repository.TargetIds, target => Assert.Equal(M10AnalyticsApiFactory.Target, target));
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("operator")]
    [InlineData("administrator")]
    public async Task ViewerOperatorAndTargetAdministratorCanReadAnalytics(string identity)
    {
        using HttpClient client = CreateClient(identity);
        HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RoleAndTargetScopeDenialsAreForbiddenBeforeRepositoryCall()
    {
        factory.Repository.Clear();
        using HttpClient denied = CreateClient("denied");
        using HttpClient scope = CreateClient("scope");
        string route = $"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host/metrics";
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scope.GetAsync(route)).StatusCode);
        Assert.Empty(factory.Repository.TargetIds);
    }

    [Fact]
    public async Task RouteBoundsAndMalformedInputAreBadRequest()
    {
        factory.Repository.Clear();
        using HttpClient client = CreateClient("viewer");
        string prefix = $"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}";
        var badRequests = new[]
        {
            $"{prefix}/analytics/series?metricKey=host.cpu.percent&limit=1001",
            $"{prefix}/analytics/series?metricKey=host.cpu.percent&fromUtc=2026-08-25T00:00:00Z",
            $"{prefix}/analytics/rollups?metricKey=host.cpu.percent&interval=week",
            $"{prefix}/analytics/rollups?metricKey=host.cpu.percent&cursor=%25%25%25",
            $"{prefix}/analytics/rollups?metricKey=host.cpu.percent&limit=201",
            $"{prefix}/analytics/compare?metricKey=host.cpu.percent&leftFromUtc=2026-08-25T00:00:00Z&leftToUtc=2026-08-25T01:00:00Z&rightFromUtc=2026-08-25T00:30:00Z&rightToUtc=2026-08-25T01:30:00Z",
            $"{prefix}/analytics/baselines?metricKey=host.cpu.percent&limit=201",
            $"{prefix}/analytics/forecasts?metricKey=host.cpu.percent&horizonDays=367",
            $"{prefix}/analytics/host/metrics?limit=201",
            $"{prefix}/analytics/host/metrics?cursor=%25%25%25",
            $"{prefix}/analytics/host/metrics?fromUtc=2026-08-25T00:00:00Z",
        };

        foreach (string request in badRequests)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(request)).StatusCode);
        Assert.Empty(factory.Repository.TargetIds);
    }

    [Fact]
    public async Task HostBindingRejectsPayloadTargetMismatchBeforeRepositoryMutation()
    {
        factory.Repository.Clear();
        using HttpClient client = CreateClient("administrator");
        string route = $"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host-binding";
        using var body = new StringContent($"{{\"targetId\":\"{M10AnalyticsApiFactory.OtherTarget:D}\"}}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, body)).StatusCode);
        Assert.Empty(factory.Repository.TargetIds);
    }

    [Fact]
    public async Task ListCursorsProduceTerminalSecondPagesWithStableTargetEnvelope()
    {
        using HttpClient client = CreateClient("viewer");
        string route = $"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host/metrics?limit=1";
        using JsonDocument first = JsonDocument.Parse(await (await client.GetAsync(route)).Content.ReadAsStringAsync());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length > 0);
        Assert.NotNull(cursor);
        Assert.Equal(M10AnalyticsApiFactory.Target.ToString("D"), first.RootElement.GetProperty("targetId").GetString());

        using JsonDocument second = JsonDocument.Parse(await (await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor))).Content.ReadAsStringAsync());
        Assert.Null(second.RootElement.GetProperty("nextCursor").GetString());
        Assert.Equal(M10AnalyticsApiFactory.Target.ToString("D"), second.RootElement.GetProperty("targetId").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("items")[0].ToString(), second.RootElement.GetProperty("items")[0].ToString());
    }

    [Fact]
    public async Task CancellationReachesRepositoryAfterRequestStarts()
    {
        await using var isolatedFactory = new M10AnalyticsApiFactory();
        using HttpClient client = isolatedFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(M10AnalyticsAuthenticationHandler.IdentityHeader, "viewer");
        isolatedFactory.Repository.Mode = M10RepositoryMode.Delayed;
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage> request = client.GetAsync($"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host/metrics", HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await isolatedFactory.Repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await isolatedFactory.Repository.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(isolatedFactory.Repository.CancellationObserved.Task.IsCompletedSuccessfully);
        try { await request; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task OversizedMappedDtoIsRejectedWithoutProviderBody()
    {
        await using var oversizedFactory = new M10AnalyticsApiFactory();
        using HttpClient client = oversizedFactory.CreateClient();
        client.DefaultRequestHeaders.Add(M10AnalyticsAuthenticationHandler.IdentityHeader, "viewer");
        oversizedFactory.Repository.Mode = M10RepositoryMode.Oversized;
        HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{M10AnalyticsApiFactory.Target:D}/analytics/host/metrics");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain("provider", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyticsSurfaceIsBoundedAndRepositoryBacked()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "src/SqlObserver.Server/AnalyticsEndpoints.cs"));
        Assert.Contains("MapGet(\"/series\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/rollups\"", source, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/compare\"", source, StringComparison.Ordinal);
        Assert.Contains("MaxMetricPoints = 1000", source, StringComparison.Ordinal);
        Assert.Contains("MaxPage = 200", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlConnection", source, StringComparison.Ordinal);
        Assert.Contains("BackfillMutationRequest", File.ReadAllText(Path.Combine(root, "src/SqlObserver.Application/Ports/AnalyticsPorts.cs")), StringComparison.Ordinal);
        Assert.Contains("HostBindingMutationRequest", File.ReadAllText(Path.Combine(root, "src/SqlObserver.Application/Ports/AnalyticsPorts.cs")), StringComparison.Ordinal);
        Assert.Contains("profile", source, StringComparison.Ordinal);
        Assert.Contains("X-Correlation-ID", source, StringComparison.Ordinal);
        Assert.Contains("MutationDigestV1.Backfill", source, StringComparison.Ordinal);
        Assert.Contains("MutationDigestV1.HostBinding", source, StringComparison.Ordinal);
        Assert.Contains("MutationDigestV1.Attestation", source, StringComparison.Ordinal);
        Assert.Contains("MutationDigestV1.Retention", source, StringComparison.Ordinal);
        Assert.DoesNotContain("items = rows, hasMore = false", source, StringComparison.Ordinal);
        Assert.Contains("GetRollupPageAsync", source, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx"))) path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        return path;
    }

    private HttpClient CreateClient(string identity)
    {
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(M10AnalyticsAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
}

[CollectionDefinition("M10 analytics API", DisableParallelization = true)]
public sealed class M10AnalyticsApiGroup { }

public sealed class M10AnalyticsApiFactory : WebApplicationFactory<Program>
{
    internal static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    internal static readonly Guid OtherTarget = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    internal M10AnalyticsRepository Repository { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("ContractTesting").ConfigureTestServices(services =>
    {
        services.RemoveAll<IAnalyticsRepositoryPort>();
        services.RemoveAll<IAnalyticsSurfaceRepositoryPort>();
        services.RemoveAll<IAnalyticsQueryService>();
        services.RemoveAll<IdentityFingerprintKey>();
        services.AddSingleton(new IdentityFingerprintKey(Enumerable.Repeat((byte)0xA5, IdentityFingerprintKey.RequiredLength).ToArray()));
        services.AddSingleton<IAnalyticsRepositoryPort>(Repository);
        services.AddSingleton<IAnalyticsSurfaceRepositoryPort>(Repository);
        services.AddSingleton<IAnalyticsQueryService, SqlObserver.Application.Services.AnalyticsQueryService>();
        services.RemoveAll<WindowsGroupRoleResolver>();
        services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M10AnalyticsAuthenticationHandler.ViewerSid), [ApplicationRole.Viewer], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M10AnalyticsAuthenticationHandler.OperatorSid), [ApplicationRole.Operator], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M10AnalyticsAuthenticationHandler.AdminSid), [ApplicationRole.TargetAdministrator], true),
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(M10AnalyticsAuthenticationHandler.ScopeSid), [ApplicationRole.Viewer], false, [new MonitoredInstanceId(OtherTarget)]),
        ]));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = M10AnalyticsAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = M10AnalyticsAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, M10AnalyticsAuthenticationHandler>(M10AnalyticsAuthenticationHandler.SchemeName, static _ => { });
    });
}

internal enum M10RepositoryMode { Normal, Delayed, Oversized }

internal sealed class M10AnalyticsRepository : IAnalyticsRepositoryPort, IAnalyticsSurfaceRepositoryPort
{
    internal M10RepositoryMode Mode { get; set; }
    internal List<Guid> TargetIds { get; } = [];
    internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly DateTimeOffset At = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Cursor = "m10-next-page";

    private void Observe(Guid targetId)
    {
        lock (TargetIds) TargetIds.Add(targetId);
    }

    internal void Clear()
    {
        lock (TargetIds) TargetIds.Clear();
    }

    private async Task DelayIfNeeded(CancellationToken cancellationToken)
    {
        if (Mode != M10RepositoryMode.Delayed) return;
        Started.TrySetResult(true);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException)
        {
            CancellationObserved.TrySetResult(true);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<MetricPoint>> ReadMetricPointsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken)
    {
        Observe(request.TargetId.Value);
        await DelayIfNeeded(cancellationToken);
        return [new MetricPoint(At, request.MetricKey, 42)];
    }

    public async ValueTask<AnalyticsRollupPage> ReadRollupPageAsync(AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken)
    {
        Observe(request.TargetId.Value);
        await DelayIfNeeded(cancellationToken);
        RollupResult row = new()
        {
            Interval = interval, BucketStartUtc = At, BucketEndUtc = At.AddHours(1), MetricKey = request.MetricKey,
            DimensionsSha256 = CanonicalDimensions.Sha256(null), Count = 1, Expected = 1, Mean = cursor is null ? 42 : 43,
            Min = 42, Max = 43, Sum = cursor is null ? 42 : 43, Last = cursor is null ? 42 : 43,
            Dimensions = new Dictionary<string, string>(), Generation = 1,
        };
        return new AnalyticsRollupPage([row], cursor is null, cursor is null ? Cursor : null, 2, 3, At, At);
    }

    public ValueTask<IReadOnlyList<RollupResult>> ReadRollupsAsync(AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<RollupResult>>([]);

    public ValueTask<IReadOnlyList<BaselineResult>> ReadBaselinesAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<BaselineResult>>([new BaselineResult { MetricKey = request.MetricKey, HourOfWeek = 1, CompleteDays = 1, SampleCount = 1, Mean = 42, Confidence = 1, Coverage = 1 }]);

    public ValueTask<IReadOnlyList<ForecastResult>> ReadForecastsAsync(AnalyticsQueryRequest request, TimeSpan horizon, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ForecastResult>>([new ForecastResult { MetricKey = request.MetricKey, HorizonStartUtc = At, HorizonEndUtc = At.Add(horizon), Estimate = 42, Confidence = 1, Residual = 0 }]);

    public ValueTask<IReadOnlyList<IncidentThread>> ReadIncidentsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<IncidentThread>>([new IncidentThread(Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), At, null, [], 1)]);

    public ValueTask StoreRollupsAsync(AnalyticsJobRequest request, IReadOnlyList<RollupResult> rollups, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StoreBaselineAsync(AnalyticsJobRequest request, IReadOnlyList<BaselineResult> baselines, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StoreForecastAsync(AnalyticsJobRequest request, ForecastResult forecast, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StoreEvidenceAsync(AnalyticsJobRequest request, EvidencePacket packet, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask StoreIncidentAsync(AnalyticsJobRequest request, IncidentThread thread, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask<AnalyticsSurfacePage> ReadSurfaceAsync(Guid targetId, string surface, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken cancellationToken)
    {
        Observe(targetId);
        await DelayIfNeeded(cancellationToken);
        if (Mode == M10RepositoryMode.Oversized)
        {
            using JsonDocument huge = JsonDocument.Parse(JsonSerializer.Serialize(new { value = new string('x', 8_000) }));
            return new AnalyticsSurfacePage(surface, "complete", Enumerable.Repeat(huge.RootElement.Clone(), 200).ToArray(), null, At, 3)
            {
                TargetRevision = 2, SnapshotUtc = At,
            };
        }

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new { id = cursor is null ? 1 : 2, surface }));
        return new AnalyticsSurfacePage(surface, "complete", [document.RootElement.Clone()], cursor is null ? Cursor : null, At, 3)
        {
            TargetRevision = 2, SnapshotUtc = At,
        };
    }

    public ValueTask<AnalyticsMutationReceipt> StartBackfillAsync(BackfillMutationRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AnalyticsMutationReceipt(request.OperationId, "queued", 1, At));
    public ValueTask<AnalyticsMutationReceipt> BindHostAsync(HostBindingMutationRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AnalyticsMutationReceipt(request.OperationId, "accepted", 1, At));
    public ValueTask<AnalyticsMutationReceipt> RecordAttestationAsync(AttestationMutationRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AnalyticsMutationReceipt(request.OperationId, "accepted", 1, At));
}

internal sealed class M10AnalyticsAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "M10.Tests";
    internal const string IdentityHeader = "X-SqlObserver-M10-Identity";
    internal const string ViewerSid = "S-1-5-21-10101";
    internal const string OperatorSid = "S-1-5-21-10102";
    internal const string AdminSid = "S-1-5-21-10103";
    internal const string ScopeSid = "S-1-5-21-10104";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());
        string? sid = values[0] switch
        {
            "viewer" => ViewerSid,
            "operator" => OperatorSid,
            "administrator" => AdminSid,
            "scope" => ScopeSid,
            "denied" => "S-1-5-21-10199",
            _ => null,
        };
        if (sid is null) return Task.FromResult(AuthenticateResult.Fail("unknown identity"));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-10200"), new Claim(ClaimTypes.GroupSid, sid) };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}
