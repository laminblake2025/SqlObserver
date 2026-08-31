using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Collector.Abstractions;
using System.Globalization;
using System.Data;
using System.Diagnostics;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class M7QueryPerformanceIntegrationTests
{
    [Fact] public async Task TargetResponseBudgetIsSharedAcrossConcurrentDatabaseReaders() { var budget = new SharedResponseBudget(100); var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => budget.TryAccept(30)))); Assert.Equal(3, results.Count(static accepted => accepted)); Assert.Equal(90, budget.ResponseBytes); Assert.True(budget.ByteLimitReached); Assert.False(budget.TryAccept(1)); }
    [Fact] public void MigrationAllowsMixedOnlyForAggregateRunMetadata() { string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0011_query_performance.sql")); string migration = File.ReadAllText(path); string observation = migration.Split("CREATE TABLE events.query_performance_observation", StringSplitOptions.None)[1].Split("CREATE TABLE events.query_performance_cache_baseline", StringSplitOptions.None)[0]; string status = migration.Split("CREATE TABLE events.query_performance_database_status", StringSplitOptions.None)[1].Split("CREATE INDEX", StringSplitOptions.None)[0]; string run = migration.Split("CREATE TABLE events.query_performance_run", StringSplitOptions.None)[1].Split("CREATE TABLE events.query_performance_query", StringSplitOptions.None)[0]; string statusGuard = migration.Split("IF jsonb_typeof(status_item)", StringSplitOptions.None)[1].Split("THEN RAISE EXCEPTION", StringSplitOptions.None)[0]; const string databaseStates = "('read_write','read_only','disabled','unsupported','permission_denied','read_failure','timed_out')"; Assert.DoesNotContain("'mixed'", observation, StringComparison.Ordinal); Assert.DoesNotContain("'mixed'", status, StringComparison.Ordinal); Assert.Contains($"source_state IN ('read_write','read_only','disabled','unsupported','permission_denied','read_failure','timed_out','mixed')", run, StringComparison.Ordinal); Assert.Contains($"status_item->>'sourceState' IS NULL OR status_item->>'sourceState' NOT IN {databaseStates}", statusGuard, StringComparison.Ordinal); Assert.DoesNotContain("status_item->>'sourceState' NOT IN ('read_write','read_only','disabled','unsupported','permission_denied','read_failure','timed_out','mixed')", statusGuard, StringComparison.Ordinal); Assert.DoesNotContain("33554432", migration, StringComparison.Ordinal); Assert.Contains("p_response_bytes NOT BETWEEN 0 AND 8388608", migration, StringComparison.Ordinal); Assert.Contains("p_minimum_lost_bytes NOT BETWEEN 0 AND 8388608", migration, StringComparison.Ordinal); }
    [Fact] public void PlanlessCacheBaselineUsesExactly32ByteSentinel() { string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0011_query_performance.sql")); string migration = File.ReadAllText(path); Assert.Contains("DEFAULT decode(repeat('00',32),'hex')", migration, StringComparison.Ordinal); Assert.Contains("plan_key := coalesce(plan_id,decode(repeat('00',32),'hex'))", migration, StringComparison.Ordinal); Assert.DoesNotContain("repeat('00',64)", migration, StringComparison.Ordinal); }
    [Fact] public void PlanMetadataProjectionUsesLatestConcreteObservation() { string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0011_query_performance.sql")); string migration = File.ReadAllText(path); string function = migration.Split("CREATE OR REPLACE FUNCTION control.get_query_plan_metadata", StringSplitOptions.None)[1].Split("CREATE OR REPLACE FUNCTION", StringSplitOptions.None)[0]; Assert.Contains("o.source,o.observed_at", function, StringComparison.Ordinal); Assert.Contains("ORDER BY o.observed_at DESC,o.interval_end DESC,o.collection_run_id DESC,o.observation_key DESC", function, StringComparison.Ordinal); Assert.Contains("o.plan_fingerprint=p.plan_fingerprint", function, StringComparison.Ordinal); string status = migration.Split("CREATE OR REPLACE FUNCTION control.get_query_performance_status", StringSplitOptions.None)[1].Split("CREATE OR REPLACE FUNCTION", StringSplitOptions.None)[0]; Assert.Contains("r.window_end > p_from_utc AND r.window_start < p_to_utc", status, StringComparison.Ordinal); Assert.Contains("ELSE INSERT INTO events.query_performance_run", migration, StringComparison.Ordinal); }
    [Fact] public void MigrationPersistsTargetFailureEvidenceWithoutFabricatedQueryStoreState() { string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0011_query_performance.sql")); string migration = File.ReadAllText(path); Assert.Contains("'unavailable'", migration, StringComparison.Ordinal); Assert.Contains("target_status text", migration, StringComparison.Ordinal); Assert.Contains("target_reason text", migration, StringComparison.Ordinal); Assert.Contains("p_payload->'targetStatus'->>'status'", migration, StringComparison.Ordinal); Assert.Contains("r.target_status,r.target_reason", migration, StringComparison.Ordinal); }
    [Fact] public void UnsupportedReasonMatrixIsAlignedAcrossCanonicalPreflightAndRunConstraint() { string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/0011_query_performance.sql")); string migration = File.ReadAllText(path); foreach (var reason in new[] { "target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_missing", "capability_profile_missing", "capability_profile_stale" }) { Assert.Contains($"'unsupported','{reason}'", migration, StringComparison.Ordinal); } Assert.Contains("p_outcome='unsupported'", migration, StringComparison.Ordinal); Assert.Contains("p_reason_code NOT IN ('target_unsupported','target_version_unsupported','target_platform_unsupported','target_edition_unsupported','capability_missing','capability_profile_missing','capability_profile_stale')", migration, StringComparison.Ordinal); }
    [Fact] public void QueryStoreAssetsAreReadOnlyAndUseExecutableMaxPlusOneProbe() { var catalog=SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(); Assert.Equal("queries.performance",catalog.Asset.Manifest.Id.Value); Assert.Equal(2_000,catalog.Asset.Manifest.Limits.MaxRows); Assert.Equal("plan-cache",catalog.FallbackMode); Assert.Equal(3,catalog.FallbackPermissionsByMajor.Count); for(var major=15;major<=17;major++){var sql=catalog.Asset.GetQuery(major);Assert.Contains("query_store",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("plan_cache",sql,StringComparison.OrdinalIgnoreCase);Assert.Contains("TOP (@probe_rows)",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("TOP (@maximum_rows)",sql,StringComparison.OrdinalIgnoreCase);Assert.Contains("DATEADD(minute,-5",sql,StringComparison.OrdinalIgnoreCase);Assert.Contains("varbinary(max)",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("dm_exec_sql_text",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("query_plan",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("INSERT ",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("UPDATE ",sql,StringComparison.OrdinalIgnoreCase);} }
    [Fact] public void SourceSelectionKeepsEmptyAndCancellationDistinct() { Assert.Equal(QueryPerformanceSource.QueryStore,QueryPerformanceSourceSelector.Choose(QueryStoreState.ReadOnly,true).Source); Assert.Equal(QueryPerformanceSource.PlanCache,QueryPerformanceSourceSelector.Choose(QueryStoreState.Disabled,true).Source); Assert.Throws<OperationCanceledException>(()=>QueryPerformanceSourceSelector.Choose(QueryStoreState.Disabled,true,cancelled:true)); }
    [Fact] public void DatetimeOffsetRowsAreNormalizedToUtc() { var value=QueryPerformanceRowParser.ReadUtc(new DateTimeOffset(2026,8,24,12,0,0,TimeSpan.FromHours(-4))); Assert.Equal(TimeSpan.Zero,value.Offset); Assert.Equal(16,value.Hour); }
    [Fact] public async Task FallbackIsExactlyOneAttemptAndEmptyQueryStoreNeverFallsBack() { var db=new SqlServerDatabaseIdentity(5,"inventory"); var reader=new FakeReader(QueryPerformanceReadStatus.QueryStoreEmpty,QueryPerformanceReadStatus.PlanCacheRows); var result=await QueryPerformanceFallbackCoordinator.ReadAsync(db,reader,true,CancellationToken.None); Assert.Equal(QueryPerformanceReadStatus.QueryStoreEmpty,result.Status); Assert.False(result.FallbackAttempted); Assert.Equal(0,reader.PlanCacheCalls); }
    [Fact] public async Task FallbackPreservesQueryStoreTriggerState() { var db=new SqlServerDatabaseIdentity(5,"inventory"); var reader=new FakeReader(QueryPerformanceReadStatus.QueryStorePermissionDenied,QueryPerformanceReadStatus.PlanCacheEmpty); var result=await QueryPerformanceFallbackCoordinator.ReadAsync(db,reader,true,CancellationToken.None); Assert.Equal(QueryPerformanceReadStatus.PlanCacheEmpty,result.Status); Assert.True(result.FallbackAttempted); Assert.Equal(QueryStoreState.PermissionDenied,result.SourceState); }
    [Fact]
    public void ProductionPayloadBoundFeedsBackEveryTrimmedDatabaseStatus()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var items = Enumerable.Range(0, QueryPerformanceObservationBatch.MaximumItems).Select(index =>
            new QueryPerformanceObservation(target, revision,
                new QueryOpaqueIdentity(5, index.ToString("x64", CultureInfo.InvariantCulture)), null,
                QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite,
                QueryMetricSemantics.QueryStoreInterval,
                new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1),
                DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false)).ToArray();
        var status = new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2000, 256, QueryStoreState.ReadWrite);
        (var payload, int overflow) = SqlServerQueryPerformanceCollector.BoundSerializedPayload(items, [status]);
        Assert.True(overflow > 0); Assert.True(QueryPerformancePersistencePayload.Serialize(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, null).Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes); Assert.True(payload.QueryPerformanceStatuses[0].Truncated); Assert.Equal(CollectorLossKind.ResponseByteLimit, payload.QueryPerformanceStatuses[0].LossKind); Assert.False(payload.QueryPerformanceStatuses[0].LossCountIsExact);
    }
    [Fact]
    public void ProductionPayloadBoundUsesBoundedSearchForTwentyThousandRows()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var items = Enumerable.Range(0, QueryPerformanceBounds.MaximumObservationsPerDatabase).Select(index => new QueryPerformanceObservation(target, revision, new QueryOpaqueIdentity(5, index.ToString("x64", CultureInfo.InvariantCulture)), null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false)).ToArray();
        var status = new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2001, 256, QueryStoreState.ReadWrite);
        Stopwatch stopwatch = Stopwatch.StartNew();
        (var payload, _) = SqlServerQueryPerformanceCollector.BoundSerializedPayload(items, [status]);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Bounded serializer took {stopwatch.Elapsed}.");
        Assert.True(QueryPerformancePersistencePayload.Serialize(payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, null).Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes);
    }
    [Fact]
    public void ProductionPayloadBoundHonorsCancellationDuringSearch()
    {
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
        var items = Enumerable.Range(0, 2_000).Select(index => new QueryPerformanceObservation(target, revision, new QueryOpaqueIdentity(5, index.ToString("x64", CultureInfo.InvariantCulture)), null, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false)).ToArray();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SqlServerQueryPerformanceCollector.BoundSerializedPayload(items, [new QueryPerformanceDatabaseStatus(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 2001, 256, QueryStoreState.ReadWrite)], cancellation.Token));
    }
    [Fact]
    public async Task ProductionCollectAsyncUsesInjectedExecutionPortForQueryStoreSuccess()
    {
        var db = new SqlServerDatabaseIdentity(5, "inventory");
        var read = Read(db, QueryPerformanceReadStatus.QueryStoreRows, [Observation(5, 1, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval)], "query_store_read", false, QueryStoreState.ReadWrite);
        var port = new InjectedExecutionPort([db], [read]);
        var collector = new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), port);
        CollectorExecutionResult result = await collector.CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Single(result.Payload.QueryPerformance.Items);
        Assert.Equal(QueryPerformanceObservation.FixedEstimatedBytes, result.Accounting.ResponseBytes);
        Assert.Equal(1, port.InventoryCalls);
        Assert.Equal(1, port.ReadCalls);
    }
    [Fact]
    public async Task ProductionCollectAsyncMarksSerializedCapPartialAndUpdatesStatuses()
    {
        var databases = Enumerable.Range(5, 10).Select(id => new SqlServerDatabaseIdentity(id, $"db{id}" )).ToArray();
        var reads = databases.Select(db => Read(db, QueryPerformanceReadStatus.QueryStoreRows, Enumerable.Range(0, 2_000).Select(index => Observation(db.DatabaseId, index, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval)).ToArray(), "query_store_read", false, QueryStoreState.ReadWrite)).ToArray();
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort(databases, reads)).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, result.Loss.Kind);
        Assert.Contains(result.Payload.QueryPerformanceStatuses, status => status.Truncated && status.LossKind == CollectorLossKind.ResponseByteLimit);
        Assert.True(QueryPerformancePersistencePayload.Serialize(result.Payload.QueryPerformance.Items, result.Payload.QueryPerformanceStatuses, null).Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes);
    }
    [Fact]
    public async Task ProductionCollectAsyncComposesDuplicateAndRowCapAsInexact()
    {
        SqlServerDatabaseIdentity[] databases = [new SqlServerDatabaseIdentity(5, "db5")];
        var reads = databases.Select(db =>
        {
            var rows = Enumerable.Range(0, 2_001).Select(index => Observation(db.DatabaseId, index, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval)).ToList();
            rows.Add(Observation(db.DatabaseId, 0, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, DateTimeOffset.UnixEpoch.AddMinutes(2)));
            return Read(db, QueryPerformanceReadStatus.QueryStoreRows, rows.ToArray(), "query_store_read", false, QueryStoreState.ReadWrite, 2_001);
        }).ToArray();
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort(databases, reads)).CollectAsync(Request(), CancellationToken.None);
        Assert.True(result.Outcome == CollectorRunOutcome.Partial, $"Outcome={result.Outcome}, target={result.Payload.QueryPerformanceTargetStatus?.Status}/{result.Payload.QueryPerformanceTargetStatus?.Reason}, loss={result.Loss.Kind}/{result.Loss.MinimumLostItems}");
        Assert.Equal(CollectorLossKind.DuplicateOverlap, result.Loss.Kind);
        Assert.False(result.Loss.CountIsExact);
        Assert.Equal(CollectorLossKind.SourceRowLimit, Assert.Single(result.Payload.QueryPerformanceStatuses).LossKind);
    }

    [Fact]
    public async Task ProductionCollectAsyncDuplicateOnlyReportsOverlapNotRowLimit()
    {
        var db = new SqlServerDatabaseIdentity(5, "known");
        var first = Observation(5, 1, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval);
        var duplicate = Observation(5, 1, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval, DateTimeOffset.UnixEpoch.AddMinutes(2));
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([db], [Read(db, QueryPerformanceReadStatus.QueryStoreRows, [first, duplicate], "query_store_read", false, QueryStoreState.ReadWrite)])).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorLossKind.DuplicateOverlap, result.Loss.Kind);
        Assert.Equal(1, result.Loss.MinimumLostItems);
        Assert.Equal(QueryPerformanceObservation.FixedEstimatedBytes, result.Loss.MinimumLostBytes);
        Assert.True(result.Loss.CountIsExact);
        Assert.Equal(CollectorLossKind.DuplicateOverlap, Assert.Single(result.Payload.QueryPerformanceStatuses).LossKind);
    }
    [Fact]
    public async Task ProductionCollectAsyncRejectsUnknownPlanCacheDatabaseWithTargetLoss()
    {
        var known = new SqlServerDatabaseIdentity(5, "known");
        var unknown = new SqlServerDatabaseIdentity(6, "unknown");
        var read = Read(unknown, QueryPerformanceReadStatus.PlanCacheRows, [Observation(6, 1, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative)], "plan_cache_fallback", true, QueryStoreState.Disabled);
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([known], [read])).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
        Assert.Empty(result.Payload.QueryPerformance.Items);
        Assert.DoesNotContain(result.Payload.QueryPerformanceStatuses, status => status.DatabaseId == 6);
    }
    [Fact]
    public async Task ProductionCollectAsyncUsesInjectedPlanCacheFallback()
    {
        var db = new SqlServerDatabaseIdentity(5, "known");
        var read = Read(db, QueryPerformanceReadStatus.PlanCacheRows, [Observation(5, 2, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative)], "plan_cache_fallback", true, QueryStoreState.Disabled);
        var sample = new QueryPerformancePlanCacheSample([read.Observations[0]], 1, QueryPerformanceObservation.FixedEstimatedBytes, CollectorLossKind.None, false,
            new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>
            {
                [5] = new QueryPerformancePlanCacheDatabaseAccounting(1, 1, 0, QueryPerformanceObservation.FixedEstimatedBytes, CollectorLossKind.None, false),
            });
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([db], [read], sample)).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.True(result.Payload.QueryPerformanceStatuses[0].FallbackAttempted);
        Assert.Equal(1, result.Accounting.SourceRowsRead);
        Assert.Equal(QueryPerformanceObservation.FixedEstimatedBytes, result.Accounting.ResponseBytes);
    }

    [Fact]
    public async Task ProductionCollectAsyncChargesCappedInvalidMultiDatabaseSampleOnce()
    {
        var db5 = new SqlServerDatabaseIdentity(5, "known5");
        var db6 = new SqlServerDatabaseIdentity(6, "known6");
        QueryPerformanceObservation item5 = Observation(5, 10, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative);
        QueryPerformanceObservation item6 = Observation(6, 11, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative);
        QueryPerformanceObservation unknown = Observation(99, 12, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative);
        var sample = new QueryPerformancePlanCacheSample(
            [item5, item6, unknown],
            sourceRowsRead: 2_001,
            responseBytes: 513,
            lossKind: CollectorLossKind.OutputValidationFailure,
            truncated: true,
            byDatabase: new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>
            {
                [5] = new(999, 1, 0, 256, CollectorLossKind.None, false),
                [6] = new(1_001, 1, 1, 256, CollectorLossKind.OutputValidationFailure, true),
            },
            invalidSourceRowsRead: 1,
            invalidResponseBytes: 1);
        var reads = new[]
        {
            Read(db5, QueryPerformanceReadStatus.PlanCacheRows, [item5], "plan_cache_fallback", true, QueryStoreState.Disabled, 999),
            Read(db6, QueryPerformanceReadStatus.PlanCacheRows, [item6], "plan_cache_fallback", true, QueryStoreState.Disabled, 1_001),
        };
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([db5, db6], reads, sample)).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Payload.QueryPerformance.Items.Count);
        Assert.Equal(2_001, result.Accounting.SourceRowsRead);
        Assert.Equal(513, result.Accounting.ResponseBytes);
        Assert.False(result.Payload.QueryPerformanceStatuses.Single(status => status.DatabaseId == 5).Truncated);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Payload.QueryPerformanceStatuses.Single(status => status.DatabaseId == 6).LossKind);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
        Assert.True(result.Loss.MinimumLostItems >= 2);
    }

    [Fact]
    public async Task ProductionCollectAsyncChargesQueryStoreAndPlanCacheAgainstOneSharedBudget()
    {
        var queryStoreDb = new SqlServerDatabaseIdentity(5, "query_store");
        var planCacheDb = new SqlServerDatabaseIdentity(6, "plan_cache");
        QueryPerformanceObservation queryStoreItem = Observation(5, 20, QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval);
        QueryPerformanceObservation planCacheItem = Observation(6, 21, QueryPerformanceSource.PlanCache, QueryStoreState.Disabled, QueryMetricSemantics.PlanCacheCumulative);
        var sample = new QueryPerformancePlanCacheSample([planCacheItem], 1, 1_000, CollectorLossKind.None, false,
            new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>
            {
                [6] = new(1, 1, 0, 1_000, CollectorLossKind.None, false),
            });
        var reads = new[]
        {
            Read(queryStoreDb, QueryPerformanceReadStatus.QueryStoreRows, [queryStoreItem], "query_store_read", false, QueryStoreState.ReadWrite, 1, 8_388_000),
            Read(planCacheDb, QueryPerformanceReadStatus.PlanCacheRows, [planCacheItem], "plan_cache_fallback", true, QueryStoreState.Disabled, 1, 1_000),
        };
        CollectorExecutionResult result = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([queryStoreDb, planCacheDb], reads, sample)).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, result.Loss.Kind);
        Assert.Equal(8_388_000, result.Accounting.ResponseBytes);
        Assert.Equal(2, result.Accounting.SourceRowsRead);
        Assert.Single(result.Payload.QueryPerformance.Items);
        Assert.Equal(QueryPerformanceSource.QueryStore, result.Payload.QueryPerformance.Items[0].Source);
        var cappedPlanStatus = Assert.Single(result.Payload.QueryPerformanceStatuses, status => status.DatabaseId == 6);
        Assert.Equal(QueryPerformanceReadStatus.OutputCapped, cappedPlanStatus.Status);
        Assert.True(cappedPlanStatus.Truncated);
    }

    [Fact]
    public void ProductionPlanCachePartitionHelperDoesNotCopyGlobalLossToUnaffectedDatabases()
    {
        var sample = new QueryPerformancePlanCacheSample([], 1, 1, CollectorLossKind.ResponseByteLimit, true,
            new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>
            {
                [6] = new(1, 0, 0, 1, CollectorLossKind.ResponseByteLimit, true),
            });
        QueryPerformanceReadResult unaffected = SqlServerQueryPerformanceCollector.BuildPlanCacheReadResult(new SqlServerDatabaseIdentity(5, "unaffected"), sample);
        QueryPerformanceReadResult affected = SqlServerQueryPerformanceCollector.BuildPlanCacheReadResult(new SqlServerDatabaseIdentity(6, "affected"), sample);
        Assert.Equal(QueryPerformanceReadStatus.PlanCacheEmpty, unaffected.Status);
        Assert.Equal(CollectorLossKind.None, unaffected.LossKind);
        Assert.Equal(QueryPerformanceReadStatus.OutputCapped, affected.Status);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, affected.LossKind);
    }

    [Fact]
    public async Task ProductionPlanCacheParserAttributesProbeAndSharedBudgetRejectionsPerDatabase()
    {
        var collector = new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded());
        using DataTable probeRows = CreatePlanCacheRows(2_001);
        QueryPerformancePlanCacheParseResult probe = await collector.ReadPlanCachePayloadAsync(Request(), probeRows.CreateDataReader(), new SharedResponseBudget(QueryPerformanceBounds.ResponseBytes), CancellationToken.None);
        QueryPerformancePlanCacheDatabaseAccounting probeAccounting = Assert.Single(probe.PerDatabase).Value;
        Assert.Equal(2_001, probe.SourceRowsRead);
        Assert.Equal(2_000, probeAccounting.EmittedRows);
        Assert.Equal(1, probeAccounting.RejectedRows);
        Assert.Equal(CollectorLossKind.SourceRowLimit, probeAccounting.LossKind);
        Assert.True(probeAccounting.Truncated);
        Assert.False(probeAccounting.LossCountIsExact);
        Assert.Equal(1, probeAccounting.MinimumLostItems);
        Assert.Equal(1, probeAccounting.MinimumLostBytes);

        using DataTable sharedCapRows = CreatePlanCacheRows(2);
        QueryPerformancePlanCacheParseResult sharedCap = await collector.ReadPlanCachePayloadAsync(Request(), sharedCapRows.CreateDataReader(), new SharedResponseBudget(QueryPerformanceObservation.FixedEstimatedBytes), CancellationToken.None);
        QueryPerformancePlanCacheDatabaseAccounting sharedAccounting = Assert.Single(sharedCap.PerDatabase).Value;
        Assert.Equal(2, sharedAccounting.SourceRowsRead);
        Assert.Equal(1, sharedAccounting.EmittedRows);
        Assert.Equal(1, sharedAccounting.RejectedRows);
        Assert.Equal(CollectorLossKind.ResponseByteLimit, sharedAccounting.LossKind);
        Assert.Equal(QueryPerformanceObservation.FixedEstimatedBytes, sharedAccounting.MinimumLostBytes);
    }

    private static DataTable CreatePlanCacheRows(int count)
    {
        var table = new DataTable();
        table.Columns.Add("database_id", typeof(int)); table.Columns.Add("query_fingerprint", typeof(string)); table.Columns.Add("plan_fingerprint", typeof(string)); table.Columns.Add("source", typeof(string)); table.Columns.Add("source_state", typeof(string));
        for (int index = 0; index < 6; index++) table.Columns.Add("metric" + index.ToString(CultureInfo.InvariantCulture), typeof(long));
        table.Columns.Add("interval_start", typeof(DateTimeOffset)); table.Columns.Add("interval_end", typeof(DateTimeOffset)); table.Columns.Add("observed_at", typeof(DateTimeOffset)); table.Columns.Add("fresh", typeof(bool)); table.Columns.Add("truncated", typeof(bool));
        DateTimeOffset start = DateTimeOffset.UnixEpoch; DateTimeOffset end = start.AddMinutes(1);
        for (int index = 0; index < count; index++) table.Rows.Add(5, index.ToString("x64", CultureInfo.InvariantCulture), (index + 100_000).ToString("x64", CultureInfo.InvariantCulture), "plan_cache", "disabled", 1L, 2L, 3L, 4L, 5L, 6L, start, end, end, true, false);
        return table;
    }
    [Fact]
    public async Task ProductionCollectAsyncPreservesInjectedInventoryAndDeadlineFailures()
    {
        var inventoryFailure = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort(new InvalidOperationException())).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.PermanentFailure, inventoryFailure.Outcome);
        Assert.Equal("inventory_failure", inventoryFailure.Payload.QueryPerformanceTargetStatus!.Status);
        var deadlineFailure = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort(new TimeoutException())).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.TimedOut, deadlineFailure.Outcome);
        Assert.Equal("deadline_exceeded", deadlineFailure.Payload.QueryPerformanceTargetStatus!.Status);
        var connectionFailure = await new SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(), new InjectedExecutionPort([new SqlServerDatabaseIdentity(5, "known")], new InvalidOperationException())).CollectAsync(Request(), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.PermanentFailure, connectionFailure.Outcome);
        Assert.Equal("permanent_target_failure", connectionFailure.Payload.QueryPerformanceTargetStatus!.Reason);
    }
    private static CollectorExecutionRequest Request()
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var revision = new ObservationTargetRevision(1);
        var profile = new CapabilityProfile(target, revision, new CollectorId("capability.connection"), 1, 1, new SqlServerIdentity(new SqlServerVersion(16, 0, 1, 0), new SqlServerEditionName("Express"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false, [], [], TimeSpan.FromSeconds(1), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));
        return new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, revision, new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("test"), new SqlServerInstanceName("SQLEXPRESS")), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))), profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromMinutes(2)));
    }
    private static QueryPerformanceObservation Observation(int databaseId, int index, QueryPerformanceSource source, QueryStoreState state, QueryMetricSemantics semantics, DateTimeOffset? observedAt = null)
    {
        var target = new MonitoredInstanceId(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")); var revision = new ObservationTargetRevision(1); var query = new QueryOpaqueIdentity(databaseId, index.ToString("x64", CultureInfo.InvariantCulture));
        PlanOpaqueIdentity? plan = source == QueryPerformanceSource.PlanCache ? new PlanOpaqueIdentity(query, (index + 100_000).ToString("x64", CultureInfo.InvariantCulture)) : null;
        return new QueryPerformanceObservation(target, revision, query, plan, source, state, semantics, new QueryPerformanceMetricSet(1, 2, 3, 4, 5, 6), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), observedAt ?? DateTimeOffset.UnixEpoch.AddMinutes(1), QueryCoverage.Complete, true, false);
    }
    private static QueryPerformanceReadResult Read(SqlServerDatabaseIdentity database, QueryPerformanceReadStatus status, QueryPerformanceObservation[] observations, string reason, bool fallback, QueryStoreState state, int? sourceRowsRead = null, int? responseBytes = null) => new(database, status, observations, reason, fallback, false, sourceRowsRead ?? observations.Length, responseBytes ?? observations.Length * QueryPerformanceObservation.FixedEstimatedBytes, state);
    private sealed class InjectedExecutionPort : ISqlServerQueryPerformanceExecutionPort
    {
        private readonly IReadOnlyList<SqlServerDatabaseIdentity>? inventory; private readonly IReadOnlyList<QueryPerformanceReadResult>? reads; private readonly QueryPerformancePlanCacheSample? planCacheSample; private readonly Exception? inventoryFailure; private readonly Exception? readFailure;
        public int InventoryCalls { get; private set; } public int ReadCalls { get; private set; }
        public InjectedExecutionPort(IReadOnlyList<SqlServerDatabaseIdentity> inventory, IReadOnlyList<QueryPerformanceReadResult> reads, QueryPerformancePlanCacheSample? planCacheSample = null) { this.inventory = inventory; this.reads = reads; this.planCacheSample = planCacheSample; }
        public InjectedExecutionPort(Exception failure) => inventoryFailure = failure;
        public InjectedExecutionPort(IReadOnlyList<SqlServerDatabaseIdentity> inventory, Exception failure) { this.inventory = inventory; readFailure = failure; }
        public ValueTask<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken cancellationToken) { InventoryCalls++; if (inventoryFailure is not null) throw inventoryFailure; return ValueTask.FromResult(inventory!); }
        public ValueTask<QueryPerformanceExecutionBatch> ReadDatabasesAsync(IReadOnlyList<SqlServerDatabaseIdentity> databases, CollectorExecutionRequest request, SharedResponseBudget responseBudget, CancellationToken cancellationToken)
        {
            ReadCalls++;
            if (readFailure is not null) throw readFailure;
            return ValueTask.FromResult(new QueryPerformanceExecutionBatch(reads!, planCacheSample));
        }
    }
    private sealed class FakeReader(QueryPerformanceReadStatus queryStore,QueryPerformanceReadStatus planCache) : IQueryPerformanceSourceReader
    { public int PlanCacheCalls { get; private set; } public ValueTask<QueryPerformanceReadResult> ReadQueryStoreAsync(SqlServerDatabaseIdentity db,CancellationToken token)=>ValueTask.FromResult(new QueryPerformanceReadResult(db,queryStore,[],queryStore==QueryPerformanceReadStatus.QueryStoreEmpty?"query_store_empty":queryStore==QueryPerformanceReadStatus.QueryStorePermissionDenied?"query_store_permission_denied":"query_store_disabled",false,false,0,0,queryStore==QueryPerformanceReadStatus.QueryStorePermissionDenied?QueryStoreState.PermissionDenied:queryStore==QueryPerformanceReadStatus.QueryStoreEmpty?QueryStoreState.ReadWrite:QueryStoreState.Disabled)); public ValueTask<QueryPerformanceReadResult> ReadPlanCacheAsync(SqlServerDatabaseIdentity db,CancellationToken token){PlanCacheCalls++;return ValueTask.FromResult(new QueryPerformanceReadResult(db,planCache,[],"plan_cache_empty",true,false,0,0));} }
}
