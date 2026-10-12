using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed partial class QueryPerformanceV2NativeCollectorTests
{
    [Theory]
    [InlineData(PlanCacheContentCaptureState.Enabled, false)]
    [InlineData(PlanCacheContentCaptureState.Enabled, true)]
    [InlineData(PlanCacheContentCaptureState.Disabled, false)]
    [InlineData(PlanCacheContentCaptureState.ProtectionUnavailable, false)]
    public async Task V3CombinedSourceKeepsQualifiedContentAndChargesItsFrameOnce(PlanCacheContentCaptureState state, bool capped)
    {
        var request = Request(); var cache = Cache(request, capped: capped);
        PlanCacheStatementContentObservation[] items = state == PlanCacheContentCaptureState.Enabled
            ? [new(cache.Output.Items[0], PlanCacheContentValue.Protected(new(SensitivePayloadKind.QueryText, new(new byte[32]),
                "AES-256-GCM", "fixture", new byte[12], new byte[16], new byte[8])),
                PlanCacheContentValue.Missing(SensitivePayloadKind.ExecutionPlan, PlanCacheContentState.Unavailable))] : [];
        var content = new PlanCacheContentOutputBatch(cache.Output, items.Length, items.Length, 0, items.Length * 16, items, state);
        var capture = cache with { Content = content };
        var port = new Port(request);
        var collector = new SqlServerQueryPerformanceV3Collector(SqlServerQueryPerformanceV3AssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), null,
            (_, _) => Task.FromResult(capture), port);
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(capped ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.CollectorManifestVersion); Assert.Equal(3, result.OutputSchemaVersion);
        Assert.Same(content, result.Payload.PlanCacheContent);
        Assert.Equal(1 + capture.SourceRowsRead, result.Accounting.SourceRowsRead);
        Assert.Equal(280 + capture.ResponseBytes, result.Accounting.ResponseBytes);
        Assert.Equal(2 + items.Length, result.Payload.ItemCount);
        Assert.Equal(result.Payload.EstimatedSizeBytes, result.Accounting.OutputBytes);
        new CollectorOutputValidator(SqlServerQueryPerformanceV3Collector.OutputContract).Validate(collector.Manifest, request, result);
        Assert.Throws<InvalidDataException>(() => QueryPerformanceV2OutputValidation.Validate(result.Payload, request.TargetId,
            request.TargetRevision, result.Outcome, result.Accounting, result.Loss));
    }

    [Fact]
    public async Task V3QueryStoreOnlyNeedsNoCacheReceiptAndFailedQueryStoreCannotPublishEarlierContent()
    {
        var withoutPermission = PlanCacheRuntimeTargetSourceTests.Request();
        var catalog = SqlServerQueryPerformanceV3AssetCatalog.LoadEmbedded();
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        var queryStoreOnly = new SqlServerQueryPerformanceV3Collector(catalog, factory, null,
            (_, _) => throw new InvalidOperationException("Cache must not execute without permission"), new Port(withoutPermission));
        var only = await queryStoreOnly.CollectAsync(withoutPermission, default);
        Assert.Equal(CollectorRunOutcome.Succeeded, only.Outcome); Assert.Null(only.Payload.PlanCacheContent);
        var request = Request(); var cache = Cache(request);
        var content = new PlanCacheContentOutputBatch(cache.Output, 0, 0, 0, 0, [], PlanCacheContentCaptureState.ProtectionUnavailable);
        var failed = new SqlServerQueryPerformanceV3Collector(catalog, factory, null,
            (_, _) => Task.FromResult(cache with { Content = content }), new Port(request) { InventoryFailure = true });
        var failure = await failed.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.PermanentFailure, failure.Outcome);
        Assert.Null(failure.Payload.PlanCacheContent); Assert.Null(failure.Payload.PlanCacheRuntime);
        Assert.Equal(0, failure.Payload.ItemCount);
    }

    [Fact]
    public async Task V3CombinedByteBudgetBoundsQueryStoreAfterTwoMiBOfProtectedCacheContent()
    {
        var request = Request(TimeSpan.FromSeconds(30)); var cache = Cache(request, count: 2000);
        var plan = new ProtectedSensitivePayload(SensitivePayloadKind.ExecutionPlan, new(new byte[32]), "AES-256-GCM", "fixture",
            new byte[12], new byte[16], new byte[1024 * 1024]);
        var content = new PlanCacheContentOutputBatch(cache.Output, 2, 2, 0, 2 * 1024 * 1024,
            cache.Output.Items.Take(2).Select(statement => new PlanCacheStatementContentObservation(statement,
                PlanCacheContentValue.Missing(SensitivePayloadKind.QueryText, PlanCacheContentState.Unavailable),
                PlanCacheContentValue.Protected(plan))).ToArray());
        var capture = cache with { Content = content };
        var collector = new SqlServerQueryPerformanceV3Collector(SqlServerQueryPerformanceV3AssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), null,
            (_, _) => Task.FromResult(capture), new Port(request, count: 20_000, maximumCounters: true));
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.True(result.Loss.HasLoss); Assert.Same(content, result.Payload.PlanCacheContent);
        Assert.InRange(result.Payload.QueryPerformance.Items.Count, 1, 19_999);
        Assert.InRange(result.Accounting.OutputBytes, 1, QueryPerformanceV3OutputValidation.MaximumResponseBytes);
        Assert.InRange(QueryPerformanceV3PersistenceBudget.Measure(result.Payload.QueryPerformance.Items,
            result.Payload.QueryPerformanceStatuses, result.Payload.PlanCacheRuntime, content), 1,
            QueryPerformanceV3OutputValidation.MaximumResponseBytes);
        new CollectorOutputValidator(SqlServerQueryPerformanceV3Collector.OutputContract).Validate(collector.Manifest, request, result);
    }

    [Fact]
    public async Task V3RejectsCounterOnlyCaptureFromAnUnqualifiedAdapter()
    {
        var request = Request();
        var collector = new SqlServerQueryPerformanceV3Collector(SqlServerQueryPerformanceV3AssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), null,
            (_, _) => Task.FromResult(Cache(request)), new Port(request));
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
        Assert.Null(result.Payload.PlanCacheRuntime); Assert.Null(result.Payload.PlanCacheContent);
    }
}
