using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    private static readonly RepositoryCallTimeout RollupTimeout = new(TimeSpan.FromSeconds(5));
    private const string BatchMetric = "engine.batch_requests_per_second";

    [Fact]
    public async Task SqlWorkerUpgradeKeepsLegacyClaimsOnVersionOneAndSnapshotsNewCadence()
    {
        await using RepositoryTestDatabase database = await Database(292);
        Guid target = await Target(database);
        Guid oldJob = await RollupJob(database, target, version: 1);
        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, MigrationTimeout), default);
        Assert.False(upgrade.HasFailures); Assert.Equal(293, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using (var privileges = database.DataSource.CreateCommand("""
            SELECT bool_and(pg_get_userbyid(p.proowner)='sqlobserver_migrator'
                AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
                AND has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')=
                    (p.proname IN ('claim_m10_derivation_jobs_versioned','schedule_sql_metric_rollup_jobs','commit_sql_metric_rollup'))
                AND NOT EXISTS (SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a
                    WHERE a.grantee=0 AND a.privilege_type='EXECUTE')),count(*)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE (n.nspname='control' AND p.proname IN ('claim_analytics_derivation_catalogs','claim_m10_derivation_jobs_versioned','schedule_sql_metric_rollup_jobs'))
                OR (n.nspname='analytics' AND p.proname IN ('commit_metric_rollups_catalog_v1','commit_sql_metric_rollup'));
            """))
        {
            await using NpgsqlDataReader rows = await privileges.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); Assert.True(rows.GetBoolean(0)); Assert.Equal(5L, rows.GetInt64(1));
        }
        Guid sqlJob = await RollupJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await RollupLease(collector);
        await using var legacy = collector.CreateCommand("""
            SELECT job_id FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@fence,2);
            """);
        legacy.Parameters.AddWithValue("owner", lease.Owner.Value); legacy.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal(oldJob, await legacy.ExecuteScalarAsync());
        var repository = RollupRepository(collector);
        AnalyticsDerivationJob claimed = Assert.Single(await repository.ClaimAsync(lease, 2, default));
        Assert.Equal(sqlJob, claimed.JobId); Assert.Equal(2, claimed.SourceCatalogVersion);
        Assert.Equal(150, claimed.SourceCadenceSeconds);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => repository.StoreRollupsAsync(
            RollupRequest(claimed, lease), [SqlMetricRollupV2.Compute([], BatchMetric, RollupInterval.FiveMinutes,
                Day, Day.AddMinutes(5), 150).Statistics], default).AsTask())).SqlState);
    }

    [Fact]
    public async Task TypedSourceAndFencedWriterPersistWeightedMeanAndReplayAfterLeaseReplacement()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database); await WeightedSamples(database, target);
        Guid jobId = await RollupJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = RollupRepository(collector);
        WorkerLeaseIdentity lease = await RollupLease(collector);
        AnalyticsDerivationJob job = Assert.Single(await repository.ClaimAsync(lease, 1, default)); Assert.Equal(jobId, job.JobId);
        IReadOnlyList<SqlMetricPoint> inputs = await repository.ReadSqlRollupInputsAsync(job, default);
        Assert.Equal(2, inputs.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReadRollupInputsAsync(job, RollupInterval.FiveMinutes, default).AsTask());
        SqlMetricRollupResult result = SqlMetricRollupV2.Compute(inputs, job.MetricKey!, job.RollupInterval!.Value,
            job.FromUtc, job.SourceCutoffUtc, job.SourceCadenceSeconds!.Value, job.Generation);
        AnalyticsJobRequest request = RollupRequest(job, lease);
        await repository.StoreSqlRollupAsync(request, result, default);
        await repository.StoreSqlRollupAsync(request, result, default);
        await AssertStoredWeighted(database, target);
        Assert.Equal(1L, await CountRollupReplays(database, jobId));
        SqlMetricRollupResult divergent = result with { WeightSum = 240, WeightedValueSum = 7800 };
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            repository.StoreSqlRollupAsync(request, divergent, default).AsTask())).SqlState);
        Assert.Equal(1L, await CountRollupReplays(database, jobId));

        await using (var expire = database.DataSource.CreateCommand("""
            UPDATE control.worker_lease SET acquired_at=statement_timestamp()-interval '3 minutes',
                renewed_at=statement_timestamp()-interval '2 minutes',expires_at=statement_timestamp()-interval '1 minute'
            WHERE work_key='analytics/derivation';
            """)) await expire.ExecuteNonQueryAsync();
        WorkerLeaseIdentity replacement = await RollupLease(collector);
        Assert.True(replacement.FencingToken.Value > lease.FencingToken.Value);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            repository.StoreSqlRollupAsync(request, result, default).AsTask())).SqlState);
        AnalyticsDerivationJob reclaimed = Assert.Single(await repository.ClaimAsync(replacement, 1, default));
        Assert.Equal(jobId, reclaimed.JobId); Assert.Equal(job.SourceCadenceSeconds, reclaimed.SourceCadenceSeconds);
        Assert.Equal(job.SourceCutoffUtc, reclaimed.SourceCutoffUtc);
        await repository.StoreSqlRollupAsync(RollupRequest(reclaimed, replacement), result, default);
        await repository.CompleteAsync(reclaimed, replacement, AnalyticsDerivationCompletion.Succeeded, null, default);
        await AssertStoredWeighted(database, target);
        Assert.Equal(2L, await CountRollupReplays(database, jobId));
        Assert.Equal("succeeded", await RollupJobState(database, jobId));
    }

    [Fact]
    public async Task SchedulerUsesClosedBucketsAndPersistsCadenceOncePerDeterministicJob()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database);
        await using (var cadence = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET collection_interval=interval '45 seconds' WHERE instance_id=@target AND collector_id='engine.core';"))
        { cadence.Parameters.AddWithValue("target", target); await cadence.ExecuteNonQueryAsync(); }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await RollupLease(collector);
        var repository = RollupRepository(collector);
        Assert.InRange(await repository.ScheduleAsync(lease, default), 1, 100000);
        await using (var facts = database.DataSource.CreateCommand("""
            SELECT count(*),bool_and(from_utc<to_utc AND to_utc<=source_cutoff_utc AND source_cadence_seconds=45
              AND dimensions_hash=sha256(convert_to('{}','UTF8'))),count(DISTINCT metric_key),count(DISTINCT rollup_interval)
            FROM control.analytics_job WHERE instance_id=@target AND source_catalog_version=2;
            """))
        {
            facts.Parameters.AddWithValue("target", target); await using NpgsqlDataReader rows = await facts.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); Assert.Equal(21L, rows.GetInt64(0)); Assert.True(rows.GetBoolean(1));
            Assert.Equal(7L, rows.GetInt64(2)); Assert.Equal(3L, rows.GetInt64(3));
        }
        await using (var cadence = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET collection_interval=interval '30 seconds' WHERE instance_id=@target AND collector_id='engine.core';"))
        { cadence.Parameters.AddWithValue("target", target); await cadence.ExecuteNonQueryAsync(); }
        await using var repeat = collector.CreateCommand("SELECT control.schedule_sql_metric_rollup_jobs(@owner,@fence);");
        repeat.Parameters.AddWithValue("owner", lease.Owner.Value); repeat.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal(0, await repeat.ExecuteScalarAsync());
        await using var unchanged = database.DataSource.CreateCommand("SELECT bool_and(source_cadence_seconds=45) FROM control.analytics_job WHERE instance_id=@target AND source_catalog_version=2;");
        unchanged.Parameters.AddWithValue("target", target); Assert.Equal(true, await unchanged.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProductionBackgroundWorkerWritesAndCompletesTheSqlJob()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database); await WeightedSamples(database, target);
        Guid jobId = await RollupJob(database, target);
        Guid gaugeJob = await RollupJob(database, target, metric: "engine.user_connections");
        Guid cpuJob = await RollupJob(database, target, metric: "engine.sql_scheduler_cpu_percent");
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = RollupRepository(collector);
        using var worker = new AnalyticsDerivationWorker(repository, new PostgreSqlWorkerLeasePort(collector),
            new WorkerExecutionId(Guid.NewGuid()), repository, NullLogger<AnalyticsDerivationWorker>.Instance, TimeSpan.FromSeconds(10));
        await worker.StartAsync(default);
        try
        {
            var elapsed = Stopwatch.StartNew();
            while ((await RollupJobState(database, jobId) != "succeeded" ||
                await RollupJobState(database, gaugeJob) != "succeeded" || await RollupJobState(database, cpuJob) != "succeeded") && elapsed.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(50);
            Assert.Equal("succeeded", await RollupJobState(database, jobId));
            Assert.Equal("succeeded", await RollupJobState(database, gaugeJob));
            Assert.Equal("succeeded", await RollupJobState(database, cpuJob));
            await AssertStoredWeighted(database, target);
        }
        finally { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await worker.StopAsync(stop.Token); }
    }

    [Fact]
    public async Task BoundedSqlSchedulingAdvancesBeyondTheFirstFleetBatch()
    {
        await using RepositoryTestDatabase database = await Database();
        for (int index = 0; index < 129; index++) await Target(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await RollupLease(collector);
        await using var schedule = collector.CreateCommand("SELECT control.schedule_sql_metric_rollup_jobs(@owner,@fence);");
        schedule.Parameters.AddWithValue("owner", lease.Owner.Value); schedule.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal(2688, await schedule.ExecuteScalarAsync());
        Assert.InRange(Assert.IsType<int>(await schedule.ExecuteScalarAsync()), 21, 2688);
        await using var coverage = database.DataSource.CreateCommand("SELECT count(DISTINCT instance_id),count(*) FROM control.analytics_job WHERE source_catalog_version=2;");
        await using NpgsqlDataReader rows = await coverage.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
        Assert.Equal(129L, rows.GetInt64(0)); Assert.InRange(rows.GetInt64(1), 2709, 5376);
    }

    [Fact]
    public async Task SqlWorkerPreservesAnExplicitUnavailableBucketAndRejectsForeignRevision()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database); await RollupJob(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = RollupRepository(collector); WorkerLeaseIdentity lease = await RollupLease(collector);
        AnalyticsDerivationJob job = Assert.Single(await repository.ClaimAsync(lease, 1, default));
        Assert.Empty(await repository.ReadSqlRollupInputsAsync(job, default));
        SqlMetricRollupResult missing = SqlMetricRollupV2.Compute([], BatchMetric, RollupInterval.FiveMinutes, Day, Day.AddMinutes(5), 150);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => repository.StoreSqlRollupAsync(
            RollupRequest(job, lease) with { TargetRevision = new(2) }, missing, default).AsTask())).SqlState);
        await repository.StoreSqlRollupAsync(RollupRequest(job, lease), missing, default);
        await using var row = database.DataSource.CreateCommand("SELECT sample_count,expected_count,value,visibility_state,weight_sum,catalog_version FROM analytics.metric_rollup_v2 WHERE instance_id=@target AND metric_key=@metric;");
        row.Parameters.AddWithValue("target", target); row.Parameters.AddWithValue("metric", BatchMetric);
        await using NpgsqlDataReader rows = await row.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
        Assert.Equal(0, rows.GetInt32(0)); Assert.Equal(2, rows.GetInt32(1)); Assert.True(rows.IsDBNull(2));
        Assert.Equal("unavailable", rows.GetString(3)); Assert.Equal(0, rows.GetDouble(4)); Assert.Equal(2, rows.GetInt32(5));
    }

    private static PostgreSqlAnalyticsRepositoryPort RollupRepository(NpgsqlDataSource source) => new(source, new IdentityFingerprintKey(new byte[32]));
    private static AnalyticsJobRequest RollupRequest(AnalyticsDerivationJob job, WorkerLeaseIdentity lease)
        => new(job.JobId, job.TargetId, lease.Key.Value, lease, RollupTimeout, job.TargetRevision);

    private static async Task<WorkerLeaseIdentity> RollupLease(NpgsqlDataSource collector)
    {
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey("analytics/derivation"), new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromSeconds(90)), RollupTimeout), default);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status); return acquired.Lease!.Identity;
    }

    private static async Task<Guid> RollupJob(RepositoryTestDatabase database, Guid target, int version = 2, string metric = BatchMetric)
    {
        Guid id = Guid.NewGuid();
        string sql = version == 1
            ? "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval,requested_at) VALUES(@id,'rollup',@target,1,@from,@to,'host.cpu.percent',@to,1,'queued','analytics/derivation','5m',@from);"
            : "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval,requested_at,dimensions_hash,source_catalog_version,source_cadence_seconds) VALUES(@id,'rollup',@target,1,@from,@to,@metric,@to,1,'queued','analytics/derivation','5m',@from,sha256(convert_to('{}','UTF8')),2,150);";
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from", Day); command.Parameters.AddWithValue("to", Day.AddMinutes(5));
        command.Parameters.AddWithValue("metric", metric); await command.ExecuteNonQueryAsync(); return id;
    }

    private static async Task WeightedSamples(RepositoryTestDatabase database, Guid target)
    {
        await Seed(database, target, Sample(Day.AddSeconds(-30), 100, 100000));
        await Seed(database, target, Sample(Day, 3100, 160000));
        await Seed(database, target, Sample(Day.AddSeconds(90), 4000, 448000));
    }

    private static async Task AssertStoredWeighted(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT value,weight_sum,weighted_value_sum,catalog_version,catalog_checksum,algorithm_version,sample_count,expected_count,minimum_value,maximum_value
            FROM analytics.metric_rollup_v2 WHERE instance_id=@target AND metric_key=@metric AND bucket_start=@from AND rollup_interval='5m';
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("metric", BatchMetric); command.Parameters.AddWithValue("from", Day);
        await using NpgsqlDataReader rows = await command.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
        Assert.Equal(32.5, rows.GetDouble(0)); Assert.Equal(120, rows.GetDouble(1)); Assert.Equal(3900, rows.GetDouble(2));
        Assert.Equal(2, rows.GetInt32(3)); Assert.Equal(MetricCatalogV2.Checksum, rows.GetString(4));
        Assert.Equal("sql-rollup-v2", rows.GetString(5)); Assert.Equal(2, rows.GetInt32(6)); Assert.Equal(2, rows.GetInt32(7));
        Assert.Equal(10, rows.GetDouble(8)); Assert.Equal(100, rows.GetDouble(9)); Assert.False(await rows.ReadAsync());
    }

    private static async Task<string> RollupJobState(RepositoryTestDatabase database, Guid job)
    {
        await using var command = database.DataSource.CreateCommand("SELECT status FROM control.analytics_job WHERE job_id=@job;");
        command.Parameters.AddWithValue("job", job); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountRollupReplays(RepositoryTestDatabase database, Guid job)
    {
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM control.analytics_replay WHERE job_id=@job;");
        command.Parameters.AddWithValue("job", job); return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }
}
