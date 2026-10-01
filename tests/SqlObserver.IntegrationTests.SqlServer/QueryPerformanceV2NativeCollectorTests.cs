using System.Text.Json;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class QueryPerformanceV2NativeCollectorTests
{
    [Fact]
    public void ExpandedOpaqueOutputDoesNotInventSourceResponseBytes()
    {
        var accounting = new CollectorRunAccounting(3, 1, 768, 3072);
        Assert.Equal(768, accounting.ResponseBytes);
        Assert.Equal(3072, accounting.OutputBytes);
    }

    [Fact]
    public void SerializedQueryStoreBoundaryPreservesWaitEvidence()
    {
        CollectorExecutionRequest request = PlanCacheRuntimeTargetSourceTests.Request();
        QueryPerformanceObservation item = Observation(request);
        var bounded = SqlServerQueryPerformanceCollector.BoundSerializedPayload([item], []);
        Assert.Same(item, Assert.Single(bounded.Payload.QueryPerformance.Items));
        Assert.Same(item.WaitSnapshot, bounded.Payload.QueryPerformance.Items[0].WaitSnapshot);
        Assert.Equal(0, bounded.OverflowBytes);
    }

    [Fact]
    public void EmbeddedAssetsKeepTheImmutableV2IdentityAndNestedPins()
    {
        var catalog = SqlServerQueryPerformanceV2AssetCatalog.LoadEmbedded();
        Assert.Equal(2, catalog.Asset.Manifest.ManifestVersion.Value);
        Assert.Equal(2, catalog.Asset.Manifest.OutputSchemaVersion.Value);
        Assert.Equal(TimeSpan.FromSeconds(120), catalog.Asset.Manifest.Limits.CommandTimeout);
        Assert.Equal(22_000, catalog.Asset.Manifest.Limits.MaxRows);
        using var manifest = JsonDocument.Parse(catalog.Asset.ManifestJson);
        Assert.False(manifest.RootElement.GetProperty("defaultEnabled").GetBoolean());
        byte[] Read(string name)
        {
            using var stream = typeof(SqlServerQueryPerformanceCollector).Assembly.GetManifestResourceStream(
                "SqlObserver.Infrastructure.SqlServer.QueryPerformanceV2Assets." + name)!;
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
        byte[] index = Read("m7-query-performance-v2.assets.sha256"), json = Read("queries.performance.v2.json"), schema = Read("queries.performance.v2.schema.json");
        SqlServerQueryPerformanceV2AssetCatalog.Validate(index, json, schema, catalog.QueryStore.BundleChecksum);
        byte[] modified = (byte[])json.Clone(); modified[20] ^= 1;
        Assert.Throws<InvalidDataException>(() => SqlServerQueryPerformanceV2AssetCatalog.Validate(index, modified, schema, catalog.QueryStore.BundleChecksum));
        Assert.Throws<InvalidDataException>(() => SqlServerQueryPerformanceV2AssetCatalog.Validate(index, json, schema, new string('0', 64)));
        index[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => SqlServerQueryPerformanceV2AssetCatalog.Validate(index, json, schema, catalog.QueryStore.BundleChecksum));
    }

    [Fact]
    public async Task CombinedResultPreservesBothSourcesAndChargesEachFrameOnce()
    {
        CollectorExecutionRequest request = Request(); var cache = Cache(request);
        var port = new Port(request);
        CollectorExecutionRequest? cacheRequest = null;
        var collector = Collector(port, (execution, token) => { cacheRequest = execution; return Task.FromResult(cache); });
        CollectorExecutionResult result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.CollectorManifestVersion); Assert.Equal(2, result.OutputSchemaVersion);
        Assert.Same(cache.Output, result.Payload.PlanCacheRuntime);
        Assert.Same(port.Items[0].WaitSnapshot, Assert.Single(result.Payload.QueryPerformance.Items).WaitSnapshot);
        Assert.Equal(4, result.Accounting.SourceRowsRead); // QS row + cache candidate + two identity frames.
        Assert.Equal(2, result.Accounting.OutputItemsProduced);
        Assert.Equal(280 + cache.ResponseBytes, result.Accounting.ResponseBytes);
        Assert.True(result.Accounting.OutputBytes > result.Accounting.ResponseBytes);
        Assert.NotNull(cacheRequest); Assert.InRange(port.Timeout.Ticks, 1, cacheRequest.Timeout.Value.Ticks);
        new CollectorOutputValidator(SqlServerQueryPerformanceV2Collector.OutputContract).Validate(collector.Manifest, request, result);
    }

    [Fact]
    public async Task MissingServerPermissionKeepsQueryStoreWithoutRunningLegacyCache()
    {
        var request = PlanCacheRuntimeTargetSourceTests.Request(); var port = new Port(request); int calls = 0;
        var collector = Collector(port, (_, _) => { calls++; throw new InvalidOperationException(); });
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Null(result.Payload.PlanCacheRuntime); Assert.Equal(0, calls);
        Assert.All(result.Payload.QueryPerformanceStatuses, status => Assert.False(status.FallbackAttempted));
    }

    [Fact]
    public async Task DisabledQueryStoreStillProducesTypedCacheEvidenceWithoutInventingIntervals()
    {
        var request = Request(); var port = new Port(request) { Disabled = true };
        var result = await Collector(port, (_, _) => Task.FromResult(Cache(request))).CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Payload.QueryPerformance.Items);
        Assert.Single(result.Payload.PlanCacheRuntime!.Items);
        Assert.Equal(QueryPerformanceReadStatus.QueryStoreDisabled, Assert.Single(result.Payload.QueryPerformanceStatuses).Status);
        QueryPerformanceV2OutputValidation.Validate(result.Payload, request.TargetId, request.TargetRevision, result.Outcome, result.Accounting, result.Loss);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteCacheCoverageRemainsVisibleWithSuccessfulQueryStore(bool capped)
    {
        var request = Request(); var port = new Port(request);
        var collector = Collector(port, (_, _) => Task.FromResult(Cache(request, capped: capped, unqualified: !capped)));
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome); Assert.Equal(1, result.Loss.MinimumLostItems);
        Assert.False(result.Loss.CountIsExact);
        Assert.Equal(capped ? CollectorLossKind.SourceRowLimit : CollectorLossKind.VisibilityIncomplete, result.Loss.Kind);
        new CollectorOutputValidator(SqlServerQueryPerformanceV2Collector.OutputContract).Validate(collector.Manifest, request, result);
    }

    [Fact]
    public async Task GlobalQueryStoreInventoryFailureCannotPublishEarlierCache()
    {
        var request = Request(); var port = new Port(request) { InventoryFailure = true };
        var result = await Collector(port, (_, _) => Task.FromResult(Cache(request))).CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.PermanentFailure, result.Outcome);
        Assert.Null(result.Payload.PlanCacheRuntime); Assert.Empty(result.Payload.QueryPerformanceStatuses);
        Assert.Equal(0, result.Accounting.OutputItemsProduced); Assert.Equal(0, result.Accounting.OutputBytes);
    }

    [Fact]
    public async Task PhasesConsumeOneDeadlineAndTimeoutDiscardsEarlierCache()
    {
        var request = Request(TimeSpan.FromMilliseconds(400)); var port = new Port(request) { Delay = TimeSpan.FromMilliseconds(300) };
        var collector = Collector(port, async (_, token) => { await Task.Delay(150, token); return Cache(request); });
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.TimedOut, result.Outcome);
        Assert.InRange(port.Timeout.TotalMilliseconds, 100, 300);
        Assert.Null(result.Payload.PlanCacheRuntime); Assert.Equal(0, result.Accounting.OutputItemsProduced);
    }

    [Fact]
    public async Task CallerCancellationWinsOverProviderFailure()
    {
        var request = Request(); var port = new Port(request); using var cancelled = new CancellationTokenSource();
        var collector = Collector(port, (_, _) => { cancelled.Cancel(); throw new InvalidDataException(); });
        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => collector.CollectAsync(request, cancelled.Token).AsTask());
        Assert.Equal(cancelled.Token, failure.CancellationToken);
    }

    [Fact]
    public async Task CombinedSerializedBudgetRetainsBoundedQueryStoreAndReportsWithheldRows()
    {
        var request = Request(); var port = new Port(request, 20_000, maximumCounters: true); var cache = Cache(request, count: 2_000);
        var collector = Collector(port, (_, _) => Task.FromResult(cache));
        var result = await collector.CollectAsync(request, default);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, result.Loss.Kind);
        Assert.InRange(result.Payload.QueryPerformance.Items.Count, 1, 19_999);
        Assert.Equal(2_000, result.Payload.PlanCacheRuntime!.Items.Count);
        Assert.Equal(22_002, result.Accounting.SourceRowsRead);
        Assert.InRange(QueryPerformanceV2PersistenceBudget.Measure(result.Payload.QueryPerformance.Items,
            result.Payload.QueryPerformanceStatuses, cache.Output), 1, 8 * 1024 * 1024);
        Assert.InRange(result.Accounting.ResponseBytes, 1, 8 * 1024 * 1024);
        Assert.InRange(result.Accounting.OutputBytes, 1, 8 * 1024 * 1024);
        Assert.InRange(result.Loss.MinimumLostBytes, 1, 8 * 1024 * 1024);
        Assert.Contains(result.Payload.QueryPerformanceStatuses, status => status.Truncated && !status.LossCountIsExact);
        new CollectorOutputValidator(SqlServerQueryPerformanceV2Collector.OutputContract).Validate(collector.Manifest, request, result);
    }

    [Fact]
    public void OutputByteLimitAlsoBoundsTheRetainedPrefixAndItsStatuses()
    {
        var request = Request(); var port = new Port(request, 10);
        var status = new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows,
            "query_store_read", false, false, 10, 2584, QueryStoreState.ReadWrite);
        var bounded = SqlServerQueryPerformanceCollector.BoundSerializedPayload(port.Items, [status],
            8 * 1024 * 1024, static _ => 100, 512, default);
        Assert.Single(bounded.Payload.QueryPerformance.Items);
        Assert.Same(port.Items[0].WaitSnapshot, bounded.Payload.QueryPerformance.Items[0].WaitSnapshot);
        Assert.True(bounded.OverflowBytes > 0);
        Assert.True(Assert.Single(bounded.Payload.QueryPerformanceStatuses).Truncated);
        Assert.InRange(bounded.Payload.EstimatedSizeBytes, 1, 512);
    }

    private static SqlServerQueryPerformanceV2Collector Collector(Port port,
        Func<CollectorExecutionRequest, CancellationToken, Task<PlanCacheRuntimeTargetRead>> cacheRead) =>
        new(SqlServerQueryPerformanceV2AssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), null, cacheRead, port);

    internal static CollectorExecutionRequest Request(TimeSpan? timeout = null)
    {
        var original = PlanCacheRuntimeTargetSourceTests.Request(); var old = original.CapabilityProfile;
        var profile = new CapabilityProfile(original.TargetId, original.TargetRevision, old.CollectorId, old.CollectorManifestVersion,
            old.OutputSchemaVersion, old.ServerIdentity, old.Outcome, old.Reason, old.AuthenticationScheme,
            true, false, [], [new PermissionEvidence(new("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)],
            TimeSpan.FromSeconds(1), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        return new(original.RunId, original.TargetId, original.TargetRevision, original.ConnectionPolicy, profile,
            original.Attempt, new(timeout ?? TimeSpan.FromSeconds(10)));
    }

    private static PlanCacheRuntimeTargetRead Cache(CollectorExecutionRequest request, int count = 1, bool capped = false, bool unqualified = false)
    {
        var at = DateTimeOffset.UtcNow; var epoch = new string('c', 64); var incarnation = new string('d', 64);
        var before = new PlanCacheDatabaseIdentityEvidence(request.TargetId, request.TargetRevision, 6, epoch, incarnation,
            at.AddMilliseconds(-2), at.AddMilliseconds(-1));
        var after = new PlanCacheDatabaseIdentityEvidence(request.TargetId, request.TargetRevision, 6, epoch, incarnation,
            at.AddMilliseconds(2), at.AddMilliseconds(3));
        var query = new QueryOpaqueIdentity(6, new string('e', 64)); var plan = new PlanOpaqueIdentity(query, new string('f', 64));
        var items = Enumerable.Range(0, count).Select(index => new PlanCacheRuntimeStatementObservation(new(
            new(request.TargetId, request.TargetRevision, query, plan, incarnation, epoch, index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)),
            new(1001, 2002, 3, 4, 5, 6), at), before, after)).ToArray();
        var output = new PlanCacheRuntimeOutputBatch(request.TargetId, request.TargetRevision, epoch, at, at, capped ? 1 : 2000,
            capped ? 2 : count, 0, 0, 0, capped ? 2 : count, unqualified ? 1 : 0, unqualified ? [] : items);
        return new(output, 2, 512);
    }

    private sealed class Port : ISqlServerQueryPerformanceExecutionPort
    {
        private readonly SqlServerDatabaseIdentity[] databases;
        internal QueryPerformanceObservation[] Items { get; }
        internal bool Disabled { get; init; }
        internal bool InventoryFailure { get; init; }
        internal TimeSpan Delay { get; init; }
        internal TimeSpan Timeout { get; private set; }
        internal Port(CollectorExecutionRequest request, int count = 1, bool maximumCounters = false)
        {
            var at = DateTimeOffset.UtcNow;
            Items = Enumerable.Range(0, count).Select(index =>
            {
                var query = new QueryOpaqueIdentity(5 + index / 1000, index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture));
                return new QueryPerformanceObservation(request.TargetId, request.TargetRevision, query, new(query, new string('b', 64)),
                    QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval,
                    maximumCounters ? new(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue) : new(1, 2, 3, 4, 5, 6), at.AddMinutes(-1), at, at, QueryCoverage.Complete, true, false,
                    waitSnapshot: index == 0 ? new QueryStoreWaitSnapshot([new(1, 100)]) : null);
            }).ToArray();
            databases = Items.Select(static item => item.Query.DatabaseId).Distinct().Select(static id => new SqlServerDatabaseIdentity(id, "db" + id)).ToArray();
        }
        public ValueTask<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken token)
        {
            if (InventoryFailure) throw new InvalidOperationException();
            Timeout = request.Timeout.Value; return ValueTask.FromResult<IReadOnlyList<SqlServerDatabaseIdentity>>(databases);
        }
        public async ValueTask<QueryPerformanceExecutionBatch> ReadDatabasesAsync(IReadOnlyList<SqlServerDatabaseIdentity> inventory,
            CollectorExecutionRequest request, SharedResponseBudget responseBudget, CancellationToken token)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token);
            return new(databases.Select(database =>
            {
                var items = Items.Where(item => item.Query.DatabaseId == database.DatabaseId).ToArray();
                return Disabled ? new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreDisabled,
                    [], "query_store_disabled", false, false, 0, 0, sourceState: QueryStoreState.Disabled) :
                    new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreRows, items, "query_store_read", false,
                        false, items.Length, items.Sum(static item => item.EstimatedSizeBytes), QueryStoreState.ReadWrite);
            }).ToArray());
        }
    }

    internal static QueryPerformanceObservation Observation(CollectorExecutionRequest request)
    {
        var query = new QueryOpaqueIdentity(5, new string('a', 64));
        var now = DateTimeOffset.UtcNow;
        return new(request.TargetId, request.TargetRevision, query, new(query, new string('b', 64)),
            QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval,
            new(1, 2, 3, 4, 5, 6), now.AddMinutes(-1), now, now, QueryCoverage.Complete, true, false,
            waitSnapshot: new QueryStoreWaitSnapshot([new(1, 100)]));
    }
}
