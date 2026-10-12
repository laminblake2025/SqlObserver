using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.UnitTests;

public sealed class QueryPerformanceV2OutputTests
{
    [Fact]
    public async Task ProtectedContentReplacementPreservesCacheEvidenceAndAccounting()
    {
        CollectorPayload original = Payload(Cache());
        QueryPerformanceObservation item = Assert.Single(original.QueryPerformance.Items);
        ProtectedSensitivePayload Protect(SensitivePayloadKind kind) => new(kind,
            new(new byte[32]), "AES-256-GCM", "test", new byte[12], new byte[16], new byte[64]);
        var protectedItem = new QueryPerformanceObservation(item.TargetId, item.TargetRevision, item.Query, item.Plan,
            item.Source, item.SourceState, item.Semantics, item.Metrics, item.IntervalStartUtc, item.IntervalEndUtc,
            item.ObservedAtUtc, item.Coverage, item.Fresh, item.Truncated,
            protectedContent: Protect(SensitivePayloadKind.QueryText),
            protectedPlanContent: Protect(SensitivePayloadKind.ExecutionPlan), waitSnapshot: new([new(1, 17)]));
        var payload = new CollectorPayload(queryPerformance: new([protectedItem]),
            queryPerformanceStatuses: original.QueryPerformanceStatuses, planCacheRuntime: original.PlanCacheRuntime);
        var port = new ContentPort();
        var lease = new WorkerLeaseIdentity(new("collector/run/queries.performance/test"), new(Guid.NewGuid()), new(1));
        CollectorPayload linked = await new QueryPerformanceProtectedContentCommitter(port).WriteAsync(payload,
            item.TargetId, lease, new(TimeSpan.FromSeconds(5)), default);
        Assert.Equal(QueryPerformanceV2PersistenceBudget.Measure(payload.QueryPerformance.Items,
                payload.QueryPerformanceStatuses, payload.PlanCacheRuntime),
            QueryPerformanceV2PersistenceBudget.Measure(linked.QueryPerformance.Items,
                linked.QueryPerformanceStatuses, linked.PlanCacheRuntime));
        Assert.Same(payload.PlanCacheRuntime, linked.PlanCacheRuntime);
        Assert.Equal(payload.ItemCount, linked.ItemCount); Assert.Equal(payload.EstimatedSizeBytes, linked.EstimatedSizeBytes);
        QueryPerformanceObservation output = Assert.Single(linked.QueryPerformance.Items);
        Assert.NotNull(output.ContentReference); Assert.NotNull(output.PlanContentReference);
        Assert.Null(output.ProtectedContent); Assert.Null(output.ProtectedPlanContent);
        Assert.Same(protectedItem.WaitSnapshot, output.WaitSnapshot); Assert.Equal(2, port.Writes);
        Validate(linked);
    }

    [Fact]
    public void CombinedPayloadCountsSelectedTuplesAndQueryStoreRowsExactlyOnce()
    {
        CollectorPayload payload = Payload(Cache());
        Assert.Equal(2, payload.ItemCount);
        Assert.Equal(256 + 1024 + 2048, payload.EstimatedSizeBytes);
        Validate(payload);
        Assert.Throws<ArgumentException>(() => Result(Manifest(), payload,
            new CollectorRunAccounting(2, 1, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultContractDeniesEvenAnEmptyCacheFrame(bool empty)
    {
        CollectorPayload payload = Payload(Cache(empty: empty));
        var validator = new CollectorOutputValidator(new(new(2), [], 0, 0, 0,
            maxQueryPerformanceObservations: 20_000));
        Assert.Throws<InvalidDataException>(() => validator.Validate(Manifest(), M4TestData.CreateExecutionRequest(),
            Result(Manifest(), payload)));
    }

    [Theory]
    [InlineData("queries.performance", 1)]
    [InlineData("queries.performance-groups", 2)]
    [InlineData("engine.core", 2)]
    public void ExplicitCardinalityCannotAuthorizeAnotherCollectorOrVersion(string id, int version)
    {
        CollectorManifest manifest = M4TestData.CreateManifest(id, version: version,
            outputKind: CollectorOutputKind.QueryPerformance, maxRows: 22_000);
        var validator = new CollectorOutputValidator(new(new(version), [], 0, 0, 0,
            maxQueryPerformanceObservations: 20_000, maxPlanCacheRuntimeObservations: 2_000));
        CollectorPayload payload = Payload(Cache(empty: true));
        Assert.Throws<InvalidDataException>(() => validator.Validate(manifest, M4TestData.CreateExecutionRequest(),
            Result(manifest, payload)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CacheFrameMustMatchTheExactExecutionScope(bool revision)
    {
        CollectorPayload payload = Payload(Cache(target: revision ? M4TestData.TargetId : new(Guid.NewGuid()),
            revision: revision ? new(M4TestData.TargetRevision.Value + 1) : M4TestData.TargetRevision));
        Assert.Throws<InvalidDataException>(() => Validate(payload));
    }

    [Fact]
    public void SourceRowsIncludeQueryStoreAndCacheCandidates()
    {
        CollectorPayload payload = Payload(Cache());
        Assert.Throws<InvalidDataException>(() => Validate(payload, sourceRows: 1));
        Validate(payload, sourceRows: 2);
    }

    [Fact]
    public void UnsupportedVersionIsRejectedEvenWithoutACacheFrame()
    {
        CollectorManifest manifest = M4TestData.CreateManifest("queries.performance", version: 4,
            outputKind: CollectorOutputKind.QueryPerformance);
        var validator = new CollectorOutputValidator(new(new(4), [], 0, 0, 0, maxQueryPerformanceObservations: 20_000));
        Assert.Throws<InvalidDataException>(() => validator.Validate(manifest, M4TestData.CreateExecutionRequest(),
            Result(manifest, Payload(null))));
    }

    [Fact]
    public void CappedSourceRequiresPartialInexactLossWithoutDiscardingItsSelectedTuple()
    {
        CollectorPayload payload = Payload(Cache(capped: true));
        Assert.Throws<InvalidDataException>(() => Validate(payload, sourceRows: 3));
        Assert.Throws<InvalidDataException>(() => Validate(payload, outcome: CollectorRunOutcome.Partial,
            loss: new(CollectorLossKind.SourceRowLimit, 1, true), sourceRows: 3));
        Validate(payload, outcome: CollectorRunOutcome.Partial,
            loss: new(CollectorLossKind.SourceRowLimit, 1, false), sourceRows: 3);
        Assert.Single(payload.PlanCacheRuntime!.Items);
    }

    [Fact]
    public void UnqualifiedIdentityRequiresVisibleCoverageLoss()
    {
        CollectorPayload payload = Payload(Cache(unqualified: true));
        Assert.Throws<InvalidDataException>(() => Validate(payload));
        Validate(payload, outcome: CollectorRunOutcome.Partial,
            loss: new(CollectorLossKind.VisibilityIncomplete, 1, true));
    }

    [Fact]
    public void FailedRunCannotCarryAnEmptyCacheReceipt()
    {
        var payload = new CollectorPayload(planCacheRuntime: Cache(empty: true));
        Assert.Throws<InvalidDataException>(() => Validate(payload, outcome: CollectorRunOutcome.TimedOut));
        Validate(CollectorPayload.Empty, outcome: CollectorRunOutcome.TimedOut, sourceRows: 0);
    }

    [Fact]
    public void RejectedOutputKeepsItsCountAndBytesAsLossEvidence()
    {
        var accounting = new CollectorRunAccounting(2, 2, 3328, 3328);
        var loss = new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 2, true, 3328);
        var result = Result(Manifest(), CollectorPayload.Empty, accounting, CollectorRunOutcome.OutputInvalid, loss);
        Validator().Validate(Manifest(), M4TestData.CreateExecutionRequest(), result);
        Assert.Throws<InvalidDataException>(() => Validator().Validate(Manifest(), M4TestData.CreateExecutionRequest(),
            Result(Manifest(), CollectorPayload.Empty, accounting, CollectorRunOutcome.OutputInvalid,
                new(CollectorLossKind.OutputValidationFailure, 1, true, 3328))));
    }

    [Fact]
    public void QueryStoreOnlyWorksAndLegacyCumulativeCacheIsRejectedByV2()
    {
        Validate(Payload(null), sourceRows: 1);
        CollectorPayload legacy = Payload(null, legacy: true);
        Assert.Throws<InvalidDataException>(() => Validate(legacy, sourceRows: 1));
        CollectorManifest v1 = M4TestData.CreateManifest("queries.performance", outputKind: CollectorOutputKind.QueryPerformance);
        new CollectorOutputValidator(new(new(1), [], 0, 0, 0, maxQueryPerformanceObservations: 20_000))
            .Validate(v1, M4TestData.CreateExecutionRequest(), Result(v1, legacy));
    }

    [Fact]
    public void MultipleIntervalsForOnePlanCannotChargeTheSameWaitSidecarTwice()
    {
        QueryPerformanceObservation original = Assert.Single(Payload(null).QueryPerformance.Items);
        var waits = new QueryStoreWaitSnapshot([new(1, 17)]);
        var earlier = new QueryPerformanceObservation(original.TargetId, original.TargetRevision, original.Query, original.Plan,
            original.Source, original.SourceState, original.Semantics, original.Metrics,
            original.IntervalStartUtc.AddMinutes(-5), original.IntervalStartUtc, original.ObservedAtUtc,
            original.Coverage, original.Fresh, original.Truncated, waitSnapshot: waits);
        var payload = new CollectorPayload(queryPerformance: new([earlier, original.WithWaitSnapshot(waits)]),
            queryPerformanceStatuses: [new(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false,
                2, 560, QueryStoreState.ReadWrite)]);
        Assert.Throws<InvalidDataException>(() => Validate(payload));
        var once = new CollectorPayload(queryPerformance: new([earlier, original]),
            queryPerformanceStatuses: payload.QueryPerformanceStatuses);
        Validate(once);
    }

    private static CollectorManifest Manifest() => M4TestData.CreateManifest("queries.performance", version: 2,
        outputKind: CollectorOutputKind.QueryPerformance, maxRows: 22_000, maxResponseBytes: 8 * 1024 * 1024);

    private static CollectorOutputValidator Validator() => new(new(new(2), [], 0, 0, 0,
        maxQueryPerformanceObservations: 20_000, maxPlanCacheRuntimeObservations: 2_000));

    private static void Validate(CollectorPayload payload, CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded,
        CollectorLossEvidence? loss = null, int sourceRows = 2) =>
        Validator().Validate(Manifest(), M4TestData.CreateExecutionRequest(), Result(Manifest(), payload,
            new(sourceRows, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), outcome, loss));

    private static CollectorExecutionResult Result(CollectorManifest manifest, CollectorPayload payload,
        CollectorRunAccounting? accounting = null, CollectorRunOutcome outcome = CollectorRunOutcome.Succeeded,
        CollectorLossEvidence? loss = null) => new(M4TestData.TargetId, M4TestData.TargetRevision, manifest.Id,
            manifest.ManifestVersion.Value, manifest.OutputSchemaVersion.Value, outcome,
            outcome switch
            {
                CollectorRunOutcome.Partial => loss?.Kind == CollectorLossKind.VisibilityIncomplete
                    ? CollectorRunReason.VisibilityIncomplete : CollectorRunReason.SourceRowLimit,
                CollectorRunOutcome.OutputInvalid => CollectorRunReason.OutputValidationFailed,
                CollectorRunOutcome.TimedOut => CollectorRunReason.DeadlineExceeded,
                _ => CollectorRunReason.Completed,
            }, payload, accounting ?? new(2, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            loss ?? CollectorLossEvidence.None);

    private static CollectorPayload Payload(PlanCacheRuntimeOutputBatch? cache, bool legacy = false)
    {
        var query = new QueryOpaqueIdentity(5, new string('1', 64));
        var item = new QueryPerformanceObservation(M4TestData.TargetId, M4TestData.TargetRevision,
            query, new(query, new string('2', 64)), legacy ? QueryPerformanceSource.PlanCache : QueryPerformanceSource.QueryStore,
            QueryStoreState.ReadWrite, legacy ? QueryMetricSemantics.PlanCacheCumulative : QueryMetricSemantics.QueryStoreInterval,
            new(1, 2, 3, 4, 5, 6), M4TestData.RepositoryTime.AddMinutes(-5), M4TestData.RepositoryTime,
            M4TestData.RepositoryTime, QueryCoverage.Complete, true, false);
        return new(queryPerformance: new([item]), queryPerformanceStatuses:
            [new(5, legacy ? QueryPerformanceReadStatus.PlanCacheRows : QueryPerformanceReadStatus.QueryStoreRows,
                legacy ? "plan_cache_fallback" : "query_store_read", legacy, false, 1, 256,
                QueryStoreState.ReadWrite)], planCacheRuntime: cache);
    }

    private static PlanCacheRuntimeOutputBatch Cache(bool empty = false, bool capped = false,
        bool unqualified = false, MonitoredInstanceId? target = null, ObservationTargetRevision? revision = null)
    {
        target ??= M4TestData.TargetId; revision ??= M4TestData.TargetRevision;
        DateTimeOffset at = M4TestData.RepositoryTime;
        var query = new QueryOpaqueIdentity(6, new string('a', 64));
        var before = new PlanCacheDatabaseIdentityEvidence(target, revision, 6, new string('c', 64), new string('d', 64),
            at.AddMilliseconds(-2), at.AddMilliseconds(-1));
        var after = new PlanCacheDatabaseIdentityEvidence(target, revision, 6, new string('c', 64), new string('d', 64),
            at.AddMilliseconds(2), at.AddMilliseconds(3));
        var item = new PlanCacheRuntimeStatementObservation(new(new(target, revision, query,
            new(query, new string('b', 64)), new string('d', 64), new string('c', 64), new string('e', 64)),
            new(1001, 2002, 1, 2, 3, 4), at), before, after);
        return new(target, revision, new string('c', 64), at, at, capped ? 1 : 2000,
            empty ? 0 : capped ? 2 : 1, 0, 0, 0, empty ? 0 : capped ? 2 : 1, unqualified ? 1 : 0,
            empty || unqualified ? [] : [item]);
    }

    private sealed class ContentPort : ISensitivePayloadPort
    {
        internal int Writes { get; private set; }
        public ValueTask<SensitivePayloadReference> GetOrAddAsync(SensitivePayloadGetOrAddRequest request,
            CancellationToken cancellationToken)
        {
            Writes++;
            return ValueTask.FromResult(new SensitivePayloadReference(new(Guid.NewGuid()), request.Payload.Kind, request.Payload.Fingerprint));
        }
    }
}
