using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Collector.Abstractions;

namespace SqlObserver.UnitTests;

public sealed class M7QueryPerformanceContractTests
{
    [Fact] public void AggregateStatusPreservesMixedAndRejectsUnknown() { Assert.Equal("mixed", QueryPerformanceAggregateState.Require("mixed")); Assert.Throws<InvalidDataException>(() => QueryPerformanceAggregateState.Require("not-a-state")); }
    [Fact] public void BoundsAreExplicit() { Assert.Equal(2000, QueryPerformanceBounds.MaximumCandidatesPerDatabase); Assert.Equal(20000, QueryPerformanceBounds.MaximumObservationsPerDatabase); Assert.Equal(50, QueryPerformanceBounds.MaximumPlansPerQuery); Assert.Equal(2001, QueryPerformanceBounds.ProbeRows); }
    [Fact] public void QueryStoreEmptyIsNotLoss() { var result = new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "inventory"), QueryPerformanceReadStatus.QueryStoreEmpty, [], "query_store_empty", false, false, 0, 0); Assert.False(result.FallbackAttempted); Assert.Empty(result.Observations); }
    [Fact] public void PlanIdentityMustBelongToObservationQuery() { var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(5, new string('a', 64)); var other = new QueryOpaqueIdentity(5, new string('b', 64)); var plan = new PlanOpaqueIdentity(other, new string('c', 64)); Assert.Throws<ArgumentException>(() => { _ = new QueryPerformanceObservation(target, revision, query, plan, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1,1,1,1,1,1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false); }); }
    [Fact] public void CacheResetIsExplicit() { var b = new QueryPerformanceCacheBaseline(); var id = Guid.NewGuid(); var digest = new string('a',64); var first = b.Observe(id,5,digest,10,100,DateTimeOffset.UtcNow); var reset = b.Observe(id,5,digest,2,10,DateTimeOffset.UtcNow.AddMinutes(1)); Assert.True(first.ResetDetected); Assert.True(reset.ResetDetected); Assert.Equal(0,reset.DeltaExecutions); }
    [Fact] public void CacheBaselineThenMonotonicDeltasAreStable() { var b = new QueryPerformanceCacheBaseline(); var id = Guid.NewGuid(); var digest = new string('b',64); var t=DateTimeOffset.UtcNow; var first=b.Observe(id,5,digest,10,100,t); var second=b.Observe(id,5,digest,14,180,t.AddMinutes(1)); var third=b.Observe(id,5,digest,20,240,t.AddMinutes(2)); Assert.True(first.ResetDetected); Assert.False(second.ResetDetected); Assert.Equal(4,second.DeltaExecutions); Assert.Equal(80,second.DeltaCpuMilliseconds); Assert.Equal(6,third.DeltaExecutions); Assert.Equal(60,third.DeltaCpuMilliseconds); }
    [Fact] public void CacheBaselineIsPlanBoundForMultiPlanQueries() { var b = new QueryPerformanceCacheBaseline(); var id = Guid.NewGuid(); var query = new string('c',64); var planA = new string('a',64); var planB = new string('b',64); var t=DateTimeOffset.UtcNow; Assert.True(b.Observe(id,5,query,100,100,t,planA).ResetDetected); Assert.True(b.Observe(id,5,query,5,5,t,planB).ResetDetected); Assert.False(b.Observe(id,5,query,120,120,t.AddMinutes(1),planA).ResetDetected); Assert.True(b.Observe(id,5,query,3,3,t.AddMinutes(1),planB).ResetDetected); }
    [Fact]
    public async Task PlanCacheSingleFlightInvokesReaderOnceUnderConcurrency()
    {
        int calls = 0;
        var gate = new QueryPerformanceSingleFlight<int>(_ => { Interlocked.Increment(ref calls); return Task.FromResult(42); });
        Task<int>[] reads = Enumerable.Range(0, 32).Select(_ => gate.GetAsync(CancellationToken.None)).ToArray();
        Assert.All(await Task.WhenAll(reads), value => Assert.Equal(42, value));
        Assert.Equal(1, calls);
    }
    [Fact]
    public void SourceStatusMatrixIsBidirectional()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(5, new string('a', 64));
        var queryStore = new QueryPerformanceObservation(target, revision, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 1, 1, 1, 1, 1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
        Assert.Throws<ArgumentException>(() => new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "db"), QueryPerformanceReadStatus.QueryStoreRows, [], "query_store_read", false, false, 0, 0, QueryStoreState.ReadWrite));
        Assert.Throws<ArgumentException>(() => new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "db"), QueryPerformanceReadStatus.QueryStoreEmpty, [queryStore], "query_store_empty", false, false, 1, 256, QueryStoreState.ReadWrite));
        var valid = new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "db"), QueryPerformanceReadStatus.QueryStoreRows, [queryStore], "query_store_read", false, false, 1, 256, QueryStoreState.ReadWrite);
        Assert.Single(valid.Observations);
        Assert.Throws<ArgumentException>(() => new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "db"), QueryPerformanceReadStatus.PlanCacheRows, [], "plan_cache_fallback", true, false, 0, 0));
    }
    [Fact]
    public void PlanCacheSampleRetainsTargetAndDatabaseAccounting()
    {
        var perDatabase = new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>
        {
            [5] = new(1999, 1998, 1, 512_000, CollectorLossKind.OutputValidationFailure, true),
            [6] = new(1, 1, 0, 256, CollectorLossKind.None, false),
        };
        var sample = new QueryPerformancePlanCacheSample([], 2001, 512_257, CollectorLossKind.SourceRowLimit, true, perDatabase, invalidSourceRowsRead: 1, invalidResponseBytes: 1);
        Assert.Equal(2001, sample.SourceRowsRead); Assert.Equal(1998, sample.ByDatabase[5].EmittedRows); Assert.Equal(1, sample.ByDatabase[5].RejectedRows); Assert.Equal(1, sample.InvalidSourceRowsRead); Assert.Equal(CollectorLossKind.SourceRowLimit, sample.LossKind);
    }
    [Fact]
    public void PlanCacheSampleRejectsUnpartitionedRowsAndBytes()
    {
        var accounting = new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting> { [5] = new(1, 1, 0, 256, CollectorLossKind.None, false) };
        Assert.Throws<ArgumentException>(() => new QueryPerformancePlanCacheSample([], 2, 256, CollectorLossKind.None, false, accounting));
        Assert.Throws<ArgumentException>(() => new QueryPerformancePlanCacheSample([], 1, 257, CollectorLossKind.None, false, accounting));
        var globalProbe = new QueryPerformancePlanCacheSample([], 1, 1, CollectorLossKind.SourceRowLimit, true, globalOnlySourceRowsRead: 1, globalOnlyResponseBytes: 1);
        Assert.Equal(1, globalProbe.GlobalOnlySourceRowsRead);
    }
    [Fact]
    public void OutputCappedFallbackIsChargedOnceButQueryStoreCapIsNotPlanCache()
    {
        var q = new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(5, "db"), QueryPerformanceReadStatus.OutputCapped, [], "output_capped", true, true, 2001, 100, QueryStoreState.ReadFailure, CollectorLossKind.SourceRowLimit);
        var qs = new QueryPerformanceReadResult(new SqlServerDatabaseIdentity(6, "db2"), QueryPerformanceReadStatus.OutputCapped, [], "output_capped", false, true, 0, 0, QueryStoreState.ReadFailure, CollectorLossKind.ResponseByteLimit);
        Assert.True(q.IsPlanCacheDerived()); Assert.False(qs.IsPlanCacheDerived());
    }
    [Fact]
    public void AggregateMetadataIncludesStatusOnlyFallbackAndUnavailableEvidence()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(5, new string('a', 64));
        var observation = new QueryPerformanceObservation(target, revision, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadOnly, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 1, 1, 1, 1, 1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
        var statuses = new[]
        {
            new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 1, 256, QueryStoreState.ReadOnly),
            new QueryPerformanceDatabaseStatus(6, QueryPerformanceReadStatus.PlanCachePermissionDenied, "plan_cache_permission_denied", true, false, 0, 0, QueryStoreState.PermissionDenied),
        };
        QueryPerformanceAggregateMetadata aggregate = QueryPerformanceAggregateMetadata.Create([observation], statuses);
        Assert.Equal(QueryPerformanceSource.Mixed, aggregate.Source);
        Assert.Equal("mixed", aggregate.SourceState);
    }
    [Fact]
    public void TargetFailureEvidenceIsBoundedAndNeverQueryStoreUnsupported()
    {
        var timeout = new QueryPerformanceTargetStatus("deadline_exceeded", "deadline_exceeded");
        Assert.Equal("deadline_exceeded", timeout.Status);
        Assert.Throws<ArgumentException>(() => new QueryPerformanceTargetStatus("inventory_failure", "deadline_exceeded"));
        var payload = new CollectorPayload(queryPerformanceTargetStatus: timeout);
        Assert.Equal("deadline_exceeded", payload.QueryPerformanceTargetStatus!.Reason);
    }
    [Fact]
    public void UnsupportedTargetReasonMatrixAcceptsAllEligibilityReasons()
    {
        foreach (var reason in new[] { "target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_missing", "capability_profile_missing", "capability_profile_stale" })
            Assert.Equal(reason, new QueryPerformanceTargetStatus("unsupported", reason).Reason);
        Assert.Throws<ArgumentException>(() => new QueryPerformanceTargetStatus("connection_failure", "capability_missing"));
    }
    [Fact]
    public void OverlapDeduplicationRetainsExactPerDatabaseLoss()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(5, new string('d', 64));
        QueryPerformanceObservation Make(DateTimeOffset observed) => new(target, revision, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), observed, QueryCoverage.Complete, true, false);
        QueryPerformanceDeduplicationResult result = QueryPerformanceBounds.DedupeOverlapWithAccounting([Make(DateTimeOffset.UnixEpoch.AddMinutes(1)), Make(DateTimeOffset.UnixEpoch.AddMinutes(2))]);
        Assert.Single(result.Observations); Assert.Equal(1, result.TotalDuplicateCount); Assert.Equal(QueryPerformanceObservation.FixedEstimatedBytes, result.TotalDuplicateBytes); Assert.Equal(1, result.DuplicateCountsByDatabase[5]); Assert.True(result.CountIsExact);
    }
    [Fact]
    public void CanonicalPersistenceSerializerBoundsWorstCaseRows()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(5, new string('e', 64));
        var item = new QueryPerformanceObservation(target, revision, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
        var all = Enumerable.Repeat(item, QueryPerformanceObservationBatch.MaximumItems).ToArray();
        byte[] oversized = QueryPerformancePersistencePayload.Serialize(all, [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2000, 256, QueryStoreState.ReadWrite)], null);
        Assert.DoesNotContain("targetStatus", System.Text.Encoding.UTF8.GetString(QueryPerformancePersistencePayload.Serialize([item], [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 1, 256, QueryStoreState.ReadWrite)], null)), StringComparison.Ordinal);
        Assert.True(oversized.Length > QueryPerformancePersistencePayload.MaximumSerializedBytes);
        int low = 1, high = all.Length;
        while (low < high) { int mid = low + (high - low + 1) / 2; int size = QueryPerformancePersistencePayload.Serialize(all.Take(mid).ToArray(), [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2000, 256, QueryStoreState.ReadWrite)], null).Length; if (size <= QueryPerformancePersistencePayload.MaximumSerializedBytes) low = mid; else high = mid - 1; }
        int count = low;
        Assert.InRange(count, 1, QueryPerformanceObservationBatch.MaximumItems); Assert.True(QueryPerformancePersistencePayload.Serialize(all.Take(count).ToArray(), [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2000, 256, QueryStoreState.ReadWrite)], null).Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes);
    }
    [Fact]
    public void CanonicalNormalSuccessShapeOmitsNullableTargetStatus()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var observation = new QueryPerformanceObservation(target, revision, new QueryOpaqueIdentity(5, new string('1', 64)), null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadOnly, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
        byte[] bytes = QueryPerformancePersistencePayload.Serialize([observation], [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 1, 256, QueryStoreState.ReadOnly)], null);
        using var document = System.Text.Json.JsonDocument.Parse(bytes);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.GetProperty("observations").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.GetProperty("databaseStatuses").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("targetStatus", out _));
        Assert.InRange(bytes.Length, 1, QueryPerformancePersistencePayload.MaximumSerializedBytes);
    }
    [Fact]
    public void PersistenceSerializerUsesCanonicalJsonbTextShape()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var observation = new QueryPerformanceObservation(target, revision, new QueryOpaqueIdentity(5, new string('2', 64)), null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
        var bytes = QueryPerformancePersistencePayload.Serialize([observation], [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 1, 256, QueryStoreState.ReadWrite)], null);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("\": ", text, StringComparison.Ordinal);
        Assert.Contains(", ", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("\"observations\"", StringComparison.Ordinal) < text.IndexOf("\"databaseStatuses\"", StringComparison.Ordinal));
        Assert.Equal(text, System.Text.Encoding.UTF8.GetString(QueryPerformancePersistencePayload.SerializeCanonicalJsonb(System.Text.Json.JsonDocument.Parse(bytes).RootElement)));
    }
    [Fact]
    public void CanonicalSerializerMatchesPostgreSqlJsonbGoldenForUnicodeNumbersAndNulls()
    {
        // Golden derived from PostgreSQL: SELECT '{"é":"é","z":null,"n":1.0}'::jsonb::text;
        const string expected = "{\"n\": 1.0, \"z\": null, \"é\": \"é\"}";
        using var source = System.Text.Json.JsonDocument.Parse("{\"é\":\"é\",\"z\":null,\"n\":1.0}");
        var actual = System.Text.Encoding.UTF8.GetString(QueryPerformancePersistencePayload.SerializeCanonicalJsonb(source.RootElement));
        Assert.Equal(expected, actual);
    }
    [Fact]
    public void CombinedLossDetailIsExplicitlyInexact()
    {
        var status = new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, true, 2001, 8388608, QueryStoreState.ReadWrite, CollectorLossKind.DuplicateOverlap, 3, false, 768);
        Assert.Equal(3, status.MinimumLostItems); Assert.Equal(768, status.MinimumLostBytes); Assert.False(status.LossCountIsExact);
    }
    [Fact]
    public async Task QueryPerformanceApiServiceRejectsNullTopMetricValue()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var query = new QueryOpaqueIdentity(5, new string('f', 64));
        var item = new TopQueryDto(target, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryPerformanceMetric.CpuMilliseconds, null, QueryMetricSemantics.QueryStoreInterval, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false, false);
        var service = new QueryPerformanceApiQueryService(new NullMetricRepository(item));
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-7000"), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForAllTargets())]);
        var request = new TopQueryRequest(target, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), QueryPerformanceMetric.CpuMilliseconds, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await service.GetTopAsync(authorization, request, CancellationToken.None));
    }

    [Fact]
    public async Task QueryPerformanceApiServiceRejectsNullTopCursorBeforeRepositoryAndAcceptsValidCursor()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var query = new QueryOpaqueIdentity(5, new string('f', 64));
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-7001"), AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForAllTargets())]);
        var from = DateTimeOffset.UnixEpoch; var to = from.AddHours(1);
        var invalidRepository = new NullMetricRepository(new TopQueryDto(target, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryPerformanceMetric.CpuMilliseconds, 1, QueryMetricSemantics.QueryStoreInterval, from, from.AddMinutes(1), QueryCoverage.Complete, true, false, false));
        var invalidService = new QueryPerformanceApiQueryService(invalidRepository);
        var nullCursor = new QueryPerformanceCursorEnvelope(target, 5, from, to, QueryPerformanceMetric.CpuMilliseconds, from, from.AddMinutes(1), query.QueryFingerprint, null, Guid.NewGuid(), null, new string('a', 32));
        var invalidRequest = new TopQueryRequest(target, from, to, QueryPerformanceMetric.CpuMilliseconds, 1, nullCursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await invalidService.GetTopAsync(authorization, invalidRequest, CancellationToken.None));
        Assert.Equal(0, invalidRepository.TopCalls);

        var validRepository = new NullMetricRepository(new TopQueryDto(target, query, null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryPerformanceMetric.CpuMilliseconds, 1, QueryMetricSemantics.QueryStoreInterval, from, from.AddMinutes(1), QueryCoverage.Complete, true, false, false));
        var validService = new QueryPerformanceApiQueryService(validRepository);
        var validCursor = new QueryPerformanceCursorEnvelope(target, 5, from, to, QueryPerformanceMetric.CpuMilliseconds, from, from.AddMinutes(1), query.QueryFingerprint, 1, Guid.NewGuid(), null, new string('a', 32));
        var page = await validService.GetTopAsync(authorization, invalidRequest with { Cursor = validCursor }, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal(1, validRepository.TopCalls);
    }

    private sealed class NullMetricRepository(TopQueryDto item) : IQueryPerformanceApiRepositoryPort
    {
        public int TopCalls { get; private set; }
        public ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(QueryPerformanceStatusRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<QueryPerformanceStatusDto?>(null);
        public ValueTask<TopQueryPage> GetTopAsync(TopQueryRequest request, CancellationToken cancellationToken) { TopCalls++; return ValueTask.FromResult(new TopQueryPage([item], false, DateTimeOffset.UnixEpoch)); }
        public ValueTask<QueryHistoryPage> GetHistoryAsync(QueryHistoryRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new QueryHistoryPage([], false, DateTimeOffset.UnixEpoch));
        public ValueTask<QueryPlanMetadataDto?> GetPlanAsync(QueryPlanMetadataRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<QueryPlanMetadataDto?>(null);
    }
}
