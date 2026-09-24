using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ScheduledForecastHorizonPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private const string Metric = "host.volume.free_bytes";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task SchedulerPersistsThirtyDayHorizonAndForecastRemainsReadableAfterCollection()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await SeedHistoryAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]));
        WorkerLeaseIdentity lease = await AcquireLeaseAsync(collector);

        await repository.ScheduleAsync(lease, CancellationToken.None);
        await using (var otherHorizons = database.DataSource.CreateCommand("SELECT count(*) FROM control.analytics_job WHERE job_kind<>'forecast' AND horizon_seconds IS NOT NULL;"))
            Assert.Equal(0L, await otherHorizons.ExecuteScalarAsync());
        AnalyticsDerivationJob job = await ClaimForecastAsync(repository, lease);

        // The original scheduler leaves this NULL, making the worker forecast
        // end at today's midnight (source cutoff + its one-day fallback).
        Assert.Equal(TimeSpan.FromDays(30), job.ForecastHorizon);
        Assert.Equal(Metric, job.MetricKey);
        Assert.Equal(TimeSpan.FromDays(28), job.ToUtc - job.FromUtc);
        Assert.Equal(job.ToUtc, job.SourceCutoffUtc);
        Assert.Equal(CanonicalDimensions.Sha256(new Dictionary<string, string> { ["volume"] = "C" }), job.DimensionsSha256);

        AnalyticsForecastInput input = await repository.ReadForecastInputsAsync(job, CancellationToken.None);
        Assert.Equal(28, input.DailyRollups.Count);
        Assert.Equal(10_000d, input.Capacity);
        ForecastResult forecast = ForecastV1.Compute(Metric, input.DailyRollups, job.SourceCutoffUtc,
            job.ForecastHorizon.GetValueOrDefault(), input.Capacity, job.DimensionsSha256) with { SourceGeneration = job.Generation };
        Assert.True(forecast.Available);
        Assert.True(forecast.HorizonEndUtc > DateTimeOffset.UtcNow.AddDays(28));
        await repository.StoreForecastAsync(new AnalyticsJobRequest(job.JobId, job.TargetId, lease.Key.Value,
            lease, Timeout, job.TargetRevision), forecast, CancellationToken.None);
        await repository.CompleteAsync(job, lease, AnalyticsDerivationCompletion.Succeeded, null, CancellationToken.None);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var reader = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        DateTimeOffset snapshot = DateTimeOffset.UtcNow;
        var query = new AnalyticsQueryRequest(
            new MonitoredInstanceId(target), Metric, job.FromUtc, snapshot, 10, Timeout,
            job.TargetRevision, snapshot, job.DimensionsSha256, input.Dimensions);
        ForecastResult actual = Assert.Single(await reader.ReadForecastsAsync(query, TimeSpan.FromDays(30), CancellationToken.None));
        Assert.Equal(forecast.HorizonStartUtc, actual.HorizonStartUtc);
        Assert.Equal(forecast.HorizonEndUtc, actual.HorizonEndUtc);
        Assert.Equal(forecast.Estimate, actual.Estimate);
        Assert.Equal(forecast.DimensionsSha256, actual.DimensionsSha256);
        var otherVolume = new Dictionary<string, string> { ["volume"] = "D" };
        Assert.Empty(await reader.ReadForecastsAsync(query with
        {
            Dimensions = otherVolume,
            DimensionsSha256 = CanonicalDimensions.Sha256(otherVolume),
        }, TimeSpan.FromDays(30), CancellationToken.None));
        Guid otherTarget = Guid.NewGuid();
        await using (var insertTarget = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
                connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,
                created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Unrelated target','sql02',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,
                statement_timestamp(),statement_timestamp(),statement_timestamp());
            """))
        {
            insertTarget.Parameters.AddWithValue("target", otherTarget);
            insertTarget.Parameters.AddWithValue("key", "forecast-other-" + otherTarget.ToString("N"));
            await insertTarget.ExecuteNonQueryAsync();
        }
        Assert.Empty(await reader.ReadForecastsAsync(query with { TargetId = new MonitoredInstanceId(otherTarget) },
            TimeSpan.FromDays(30), CancellationToken.None));
        Assert.Empty(await reader.ReadForecastsAsync(query with
        {
            Dimensions = new Dictionary<string, string>(),
            DimensionsSha256 = CanonicalDimensions.Sha256(null),
        }, TimeSpan.FromDays(30), CancellationToken.None));

        await repository.ScheduleAsync(lease, CancellationToken.None);
        await using var duplicate = database.DataSource.CreateCommand("SELECT count(*) FROM control.analytics_job WHERE job_kind='forecast' AND instance_id=@target;");
        duplicate.Parameters.AddWithValue("target", target);
        Assert.Equal(1L, await duplicate.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ForwardUpgradePreservesQueuedJobsAndFunctionSecurityContracts()
    {
        PostgreSqlMigrationResource repair = Assert.Single(PostgreSqlMigrationCatalog.LoadEmbedded().Migrations,
            migration => migration.FileName == "0082_scheduled_forecast_horizon.sql");
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(repair.Descriptor.Number.Value - 1);
        Guid target = Guid.NewGuid();
        await SeedHistoryAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var repository = new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]));
        WorkerLeaseIdentity lease = await AcquireLeaseAsync(collector);
        await repository.ScheduleAsync(lease, CancellationToken.None);
        const string jobsSql = "SELECT jsonb_agg(to_jsonb(j) ORDER BY j.job_id)::text FROM control.analytics_job j;";
        const string securitySql = """
            SELECT jsonb_agg(jsonb_build_object('identity',p.oid::regprocedure::text,
                'owner',p.proowner,'acl',p.proacl,'securityDefiner',p.prosecdef,'config',p.proconfig,
                'result',pg_get_function_result(p.oid)) ORDER BY p.oid::regprocedure::text)::text
            FROM pg_proc p WHERE p.oid IN (
                'control.schedule_m10_derivation_jobs(uuid,bigint)'::regprocedure,
                'reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz)'::regprocedure);
            """;
        string jobsBefore = await ScalarTextAsync(database, jobsSql);
        const string forecastsSql = "SELECT jsonb_agg(to_jsonb(j) ORDER BY j.job_id)::text FROM control.analytics_job j WHERE j.job_kind='forecast';";
        string forecastsBefore = await ScalarTextAsync(database, forecastsSql);
        string securityBefore = await ScalarTextAsync(database, securitySql);
        await using (var legacy = database.DataSource.CreateCommand("SELECT count(*) FROM control.analytics_job WHERE job_kind='forecast' AND horizon_seconds IS NULL;"))
            Assert.Equal(1L, await legacy.ExecuteScalarAsync());

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);

        Assert.False(upgrade.HasFailures);
        Assert.Equal(repair.Descriptor.Number.Value, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(jobsBefore, await ScalarTextAsync(database, jobsSql));
        Assert.Equal(securityBefore, await ScalarTextAsync(database, securitySql));
        await repository.ScheduleAsync(lease, CancellationToken.None);
        Assert.Equal(forecastsBefore, await ScalarTextAsync(database, forecastsSql));
    }

    private static async Task<string> ScalarTextAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<AnalyticsDerivationJob> ClaimForecastAsync(PostgreSqlAnalyticsRepositoryPort repository, WorkerLeaseIdentity lease)
    {
        // Keep ordinary scheduling/claim ordering. Complete unrelated jobs in
        // this isolated fixture so the real bounded claim reaches its forecast.
        for (int attempt = 0; attempt < 64; attempt++)
        {
            IReadOnlyList<AnalyticsDerivationJob> jobs = await repository.ClaimAsync(lease, 2, CancellationToken.None);
            Assert.NotEmpty(jobs);
            AnalyticsDerivationJob? forecast = null;
            foreach (AnalyticsDerivationJob job in jobs)
            {
                if (job.JobKind == "forecast") forecast = job;
                else await repository.CompleteAsync(job, lease, AnalyticsDerivationCompletion.Succeeded, null, CancellationToken.None);
            }
            if (forecast is not null) return forecast;
        }
        throw new InvalidOperationException("Scheduled forecast was not reached within the claim bound.");
    }

    private static async Task<WorkerLeaseIdentity> AcquireLeaseAsync(NpgsqlDataSource collector)
    {
        Guid owner = Guid.NewGuid();
        await using var command = collector.CreateCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease('analytics/derivation',@owner,interval '5 minutes');");
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return new WorkerLeaseIdentity(new("analytics/derivation"), new(owner), new(reader.GetInt64(1)));
    }

    private static async Task SeedHistoryAsync(RepositoryTestDatabase database, Guid target)
    {
        // Fresh installs provision only recent partitions. This isolated test
        // needs a full 28-day history, so its administrator adds a default
        // partition for older synthetic rows without changing production DDL.
        const string sql = """
            CREATE TABLE analytics.forecast_test_history PARTITION OF analytics.metric_rollup_v2 DEFAULT;
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
                connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,
                created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Forecast horizon regression','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,
                statement_timestamp(),statement_timestamp(),statement_timestamp());
            INSERT INTO control.host_binding(instance_id,target_revision,host_id,binding_revision,host_name,identity_fingerprint,binding_state)
            VALUES(@target,1,@host,1,'sql01',sha256(convert_to('forecast-fixture','UTF8')),'active');
            INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,
                os_family,os_version,cpu_count,memory_bytes,capability_state,profile)
            VALUES(@target,1,@host,1,1,'Windows','2026',4,4294967296,'available',
                '{"osFamily":"Windows","osVersion":"2026","cpuCount":4,"memoryBytes":4294967296,"capabilityState":"available"}'::jsonb);
            INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,
                host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
            VALUES(date_trunc('day',statement_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'-interval '1 day',
                @run,@target,1,@host,1,1,'host.volume.total_bytes',10000,'{"volume":"C"}'::jsonb,statement_timestamp());
            INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,
                metric_key,aggregation,dimension_hash,generation,sample_count,value,visibility_state,computed_at,
                dimensions,expected_count,reset_count,gap_count,truncated,source_cutoff_utc,catalog_version,algorithm_version)
            SELECT day_end-i*interval '1 day',@target,1,'day','host.volume.free_bytes','avg',
                sha256(convert_to('{"volume":"C"}','UTF8')),1,1,5000+i*100,'complete',day_end-interval '1 day',
                '{"volume":"C"}'::jsonb,1,0,0,false,day_end-interval '1 day',1,'rollup-v1'
            FROM generate_series(2,29) AS series(i)
            CROSS JOIN (SELECT date_trunc('day',statement_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS day_end) AS clock;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("key", "forecast-horizon-" + target.ToString("N"));
        command.Parameters.AddWithValue("host", Guid.NewGuid());
        command.Parameters.AddWithValue("run", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync(int maximumMigrations = MigrationBatchResult.MaximumResults)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(maximumMigrations, Timeout), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
}
