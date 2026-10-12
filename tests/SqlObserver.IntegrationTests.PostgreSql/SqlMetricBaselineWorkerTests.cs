using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    private static readonly DateTimeOffset BaselineEnd = new(DateTime.UtcNow.Date, TimeSpan.Zero);
    [Fact]
    public async Task SqlBaselineTypedReaderWorkerPersistsAllHoursAndReplayIsFenced()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database); await SeedBaselineHours(database, target);
        Guid jobId = await SqlBaselineJob(database, target);
        await using (var disable = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@target AND collector_id='engine.core';"))
        { disable.Parameters.AddWithValue("target", target); await disable.ExecuteNonQueryAsync(); }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = RollupRepository(collector);
        using var worker = new AnalyticsDerivationWorker(repository, new PostgreSqlWorkerLeasePort(collector),
            new WorkerExecutionId(Guid.NewGuid()), repository, NullLogger<AnalyticsDerivationWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            var timer = Stopwatch.StartNew();
            while (await RollupJobState(database, jobId) != "succeeded" && timer.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            Assert.Equal("succeeded", await RollupJobState(database, jobId));
        }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(stop.Token); }
        await using (var rows = database.DataSource.CreateCommand("""
            SELECT count(*),count(DISTINCT hour_of_week),bool_and((result->>'median')::double precision=32.5
                AND (result->>'completeDays')::integer=28 AND (result->>'sampleCount')::integer=4
                AND catalog_version=2 AND algorithm_version='sql-baseline-v2' AND source_algorithm_version='sql-rollup-v2')
            FROM analytics.sql_metric_baseline WHERE instance_id=@target;
            """))
        {
            rows.Parameters.AddWithValue("target", target); await using NpgsqlDataReader reader = await rows.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(168L, reader.GetInt64(0)); Assert.Equal(168L, reader.GetInt64(1)); Assert.True(reader.GetBoolean(2));
        }
        await using var registry = database.DataSource.CreateCommand("""
            SELECT count(*) FROM system.partition_registry r JOIN system.retention_policy p
             ON p.parent_schema=r.parent_schema AND p.parent_table=r.parent_table
            WHERE p.data_class='sql_metric_baselines' AND p.enabled AND p.retain_for=interval '90 days';
            """);
        Assert.Equal(1L, await registry.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqlBaselineReplayRefusesChangedContentAndExpiredLeaseAndKeepsUnavailableHours()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database);
        Guid jobId = await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = RollupRepository(collector); var lease = await RollupLease(collector);
        AnalyticsDerivationJob job = Assert.Single(await repository.ClaimAsync(lease, 1, default)); Assert.Equal(jobId, job.JobId);
        IReadOnlyList<SqlMetricRollupResult> inputs = await repository.ReadSqlBaselineInputsAsync(job, default); Assert.Empty(inputs);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadBaselineInputsAsync(job, default).AsTask());
        IReadOnlyList<SqlMetricBaselineResult> results = SqlMetricBaselineV2.Compute(inputs, BatchMetric, job.ToUtc, job.SourceCutoffUtc);
        AnalyticsJobRequest request = RollupRequest(job, lease);
        await repository.StoreSqlBaselinesAsync(request, results, default); await repository.StoreSqlBaselinesAsync(request, results, default);
        await using (var rows = database.DataSource.CreateCommand("SELECT count(*),bool_and(result->>'median' IS NULL AND result->>'visibilityState'='unavailable') FROM analytics.sql_metric_baseline WHERE instance_id=@target;"))
        {
            rows.Parameters.AddWithValue("target", target); await using NpgsqlDataReader reader = await rows.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(168L, reader.GetInt64(0)); Assert.True(reader.GetBoolean(1));
        }
        SqlMetricBaselineResult[] divergent = [.. results];
        divergent[0] = divergent[0] with { Statistics = divergent[0].Statistics with
        { CompleteDays = 1, Coverage = 1 / 28d } };
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => repository.StoreSqlBaselinesAsync(request, divergent, default).AsTask())).SqlState);
        SqlMetricBaselineResult[] duplicate = [.. results]; duplicate[0] = duplicate[1];
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => repository.StoreSqlBaselinesAsync(request, duplicate, default).AsTask())).SqlState);
        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
                renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='analytics/derivation';
            """)) await expire.ExecuteNonQueryAsync();
        var replacement = await RollupLease(collector);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => repository.StoreSqlBaselinesAsync(request, results, default).AsTask())).SqlState);
        AnalyticsDerivationJob reclaimed = Assert.Single(await repository.ClaimAsync(replacement, 1, default));
        await repository.StoreSqlBaselinesAsync(RollupRequest(reclaimed, replacement), results, default);
        await repository.CompleteAsync(reclaimed, replacement, AnalyticsDerivationCompletion.Succeeded, null, default);
        await using var receipts = database.DataSource.CreateCommand("SELECT count(*) FROM control.analytics_replay WHERE job_id=@job;");
        receipts.Parameters.AddWithValue("job", jobId); Assert.Equal(2L, await receipts.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SqlBaselineSourceFiltersGenerationCatalogAndSnapshotAndRefusesWrongScope()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database);
        await SeedBaselineHours(database, target);
        await using (var extras = database.DataSource.CreateCommand("""
            INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,
              dimension_hash,generation,sample_count,value,visibility_state,computed_at,dimensions,expected_count,reset_count,gap_count,
              truncated,source_cutoff_utc,catalog_version,algorithm_version,last_value,counter_delta,rate_per_second,
              catalog_checksum,weight_sum,weighted_value_sum,minimum_value,maximum_value,sample_value_sum)
            SELECT r.bucket_start,r.instance_id,r.target_revision,r.rollup_interval,r.metric_key,r.aggregation,
              r.dimension_hash,2,r.sample_count,r.value,r.visibility_state,@future,r.dimensions,r.expected_count,r.reset_count,r.gap_count,
              r.truncated,r.source_cutoff_utc,r.catalog_version,r.algorithm_version,r.last_value,r.counter_delta,r.rate_per_second,
              r.catalog_checksum,r.weight_sum,r.weighted_value_sum,r.minimum_value,r.maximum_value,r.sample_value_sum
            FROM analytics.metric_rollup_v2 r WHERE r.instance_id=@target;
            INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,
              dimension_hash,generation,sample_count,value,visibility_state,computed_at,expected_count,source_cutoff_utc,catalog_version,algorithm_version)
            SELECT r.bucket_start,r.instance_id,r.target_revision,r.rollup_interval,r.metric_key,r.aggregation,
              r.dimension_hash,4,r.sample_count,r.value,r.visibility_state,r.computed_at,r.expected_count,r.source_cutoff_utc,1,'rollup-v1'
            FROM analytics.metric_rollup_v2 r WHERE r.instance_id=@target AND r.generation=1;
            """))
        { extras.Parameters.AddWithValue("target", target); extras.Parameters.AddWithValue("future", BaselineEnd.AddDays(1)); await extras.ExecuteNonQueryAsync(); }
        Guid jobId = await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var repository = RollupRepository(collector);
        var lease = await RollupLease(collector); AnalyticsDerivationJob job = Assert.Single(await repository.ClaimAsync(lease, 1, default));
        Assert.Equal(jobId, job.JobId); Assert.Equal(672, (await repository.ReadSqlBaselineInputsAsync(job, default)).Count);
        Assert.Empty(await repository.ReadSqlBaselineInputsAsync(job with { Generation = 2 }, default));
        Assert.Equal(672, (await repository.ReadSqlBaselineInputsAsync(job with { Generation = 2, SourceCutoffUtc = BaselineEnd.AddDays(2) }, default)).Count);
        Assert.Empty(await repository.ReadSqlBaselineInputsAsync(job with { Generation = 3 }, default));
        Assert.Empty(await repository.ReadSqlBaselineInputsAsync(job with { Generation = 4 }, default));
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => repository.ReadSqlBaselineInputsAsync(job with { TargetRevision = new(2) }, default).AsTask())).SqlState);
        await using var unscoped = collector.CreateCommand("SELECT count(*) FROM reporting.read_sql_baseline_inputs(@target,1,@metric,@from,@to,@to,1);");
        unscoped.Parameters.AddWithValue("target", target); unscoped.Parameters.AddWithValue("metric", BatchMetric);
        unscoped.Parameters.AddWithValue("from", BaselineEnd.AddDays(-28)); unscoped.Parameters.AddWithValue("to", BaselineEnd);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => unscoped.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task SqlBaselinePartialDayCoveragePersistsWithoutFillingMissingHours()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database);
        await SeedBaselineHours(database, target, days: 1); await SqlBaselineJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var repository = RollupRepository(collector);
        var lease = await RollupLease(collector); AnalyticsDerivationJob job = Assert.Single(await repository.ClaimAsync(lease, 1, default));
        IReadOnlyList<SqlMetricBaselineResult> results = SqlMetricBaselineV2.Compute(await repository.ReadSqlBaselineInputsAsync(job, default),
            BatchMetric, job.ToUtc, job.SourceCutoffUtc);
        await repository.StoreSqlBaselinesAsync(RollupRequest(job, lease), results, default);
        await using var counts = database.DataSource.CreateCommand("""
            SELECT count(*) FILTER(WHERE result->>'visibilityState'='partial'),
                count(*) FILTER(WHERE result->>'visibilityState'='unavailable'),
                bool_and((result->>'completeDays')::integer=1 AND (result->>'coverage')::double precision=1::double precision/28)
            FROM analytics.sql_metric_baseline WHERE instance_id=@target;
            """);
        counts.Parameters.AddWithValue("target", target); await using NpgsqlDataReader reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(24L, reader.GetInt64(0)); Assert.Equal(144L, reader.GetInt64(1)); Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task SqlBaselineUpgradeKeepsV1GrantsAndSchedulerWaitsForCompletedLastHour()
    {
        await using RepositoryTestDatabase database = await Database(293); Guid target = await Target(database);
        var upgrade = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(1, MigrationTimeout), default);
        Assert.False(upgrade.HasFailures); Assert.Equal(294, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var lease = await RollupLease(collector);
        await using var schedule = collector.CreateCommand("SELECT control.schedule_sql_metric_baseline_jobs(@owner,@fence);");
        schedule.Parameters.AddWithValue("owner", lease.Owner.Value); schedule.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal(0, await schedule.ExecuteScalarAsync());
        await SeedBaselineHours(database, target);
        Assert.Equal(1, await schedule.ExecuteScalarAsync()); Assert.Equal(0, await schedule.ExecuteScalarAsync());
        AnalyticsDerivationJob job = Assert.Single(await RollupRepository(collector).ClaimAsync(lease, 1, default));
        Assert.Equal("baseline", job.JobKind); Assert.Equal(2, job.SourceCatalogVersion); Assert.Null(job.SourceCadenceSeconds);
        Assert.Equal(BaselineEnd.AddDays(-28), job.FromUtc); Assert.Equal(BaselineEnd, job.ToUtc);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => RollupRepository(collector)
            .StoreBaselineAsync(RollupRequest(job, lease), [], default).AsTask())).SqlState);
        await using var privileges = database.DataSource.CreateCommand("""
            SELECT c.relrowsecurity AND c.relforcerowsecurity AND NOT has_table_privilege('sqlobserver_collector',c.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_server',c.oid,'SELECT'),
              NOT has_function_privilege('sqlobserver_collector','analytics.commit_metric_baselines_catalog_v1(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)','EXECUTE'),
              NOT has_function_privilege('sqlobserver_server','analytics.commit_sql_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)','EXECUTE')
            FROM pg_class c WHERE c.oid='analytics.sql_metric_baseline'::regclass;
            """);
        await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1)); Assert.True(reader.GetBoolean(2));
    }

    private static async Task<Guid> SqlBaselineJob(RepositoryTestDatabase database, Guid target)
    {
        Guid id = Guid.NewGuid(); await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,
                generation,status,work_key,requested_at,dimensions_hash,source_catalog_version)
            VALUES(@id,'baseline',@target,1,@from,@to,@metric,@to,1,'queued','analytics/derivation',@from,sha256(convert_to('{}','UTF8')),2);
            """);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from", BaselineEnd.AddDays(-28)); command.Parameters.AddWithValue("to", BaselineEnd);
        command.Parameters.AddWithValue("metric", BatchMetric); await command.ExecuteNonQueryAsync(); return id;
    }

    private static async Task SeedBaselineHours(RepositoryTestDatabase database, Guid target, int days = 28)
    {
        await using var command = database.DataSource.CreateCommand("""
            DO $$ DECLARE d date; BEGIN
             FOR d IN SELECT generate_series((date_trunc('day',clock_timestamp())-interval '29 days')::date,
               (date_trunc('day',clock_timestamp())-interval '1 day')::date,interval '1 day')::date LOOP
              EXECUTE format('CREATE TABLE IF NOT EXISTS analytics.%I PARTITION OF analytics.metric_rollup_v2 FOR VALUES FROM (%L) TO (%L)',
                'metric_rollup_v2_p'||to_char(d,'YYYYMMDD'),d::timestamptz,(d+1)::timestamptz);
             END LOOP;
            END $$;
            INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation,
                sample_count,expected_count,value,visibility_state,computed_at,source_cutoff_utc,catalog_version,algorithm_version,catalog_checksum,
                last_value,weight_sum,weighted_value_sum,minimum_value,maximum_value,sample_value_sum)
            SELECT h,@target,1,'hour',@metric,'avg',sha256(convert_to('{}','UTF8')),1,2,2,32.5,'complete',@to-interval '1 second',h+interval '1 hour',
                2,'sql-rollup-v2',@checksum,10,120,3900,10,100,110 FROM generate_series(@from,@to-interval '1 hour',interval '1 hour') h;
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("metric", BatchMetric);
        command.Parameters.AddWithValue("checksum", MetricCatalogV2.Checksum);
        command.Parameters.AddWithValue("from", BaselineEnd.AddDays(-days)); command.Parameters.AddWithValue("to", BaselineEnd);
        await command.ExecuteNonQueryAsync();
    }
}
