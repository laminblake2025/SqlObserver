using Npgsql;
using NpgsqlTypes;
using SqlObserver.Domain.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Auditing;
using SqlObserver.Infrastructure.PostgreSql;
using System.Text.Json;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>
/// PostgreSQL 18.4 runtime coverage for the M10 analytics surface.  These
/// tests deliberately use the migrated database and the collector/server
/// roles; they are not source-text contract checks.
/// </summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M10AnalyticsPostgreSqlIntegrationTests
{
    private readonly PostgreSql18Fixture fixture;

    public M10AnalyticsPostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task RetentionPreviewBlocksOnlyJobsOverlappingItsPartitionWindow()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "retention-window", 1);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var clock = new NpgsqlCommand("SELECT (current_date-1)::timestamptz;", connection);
        await using NpgsqlDataReader clockReader = await clock.ExecuteReaderAsync();
        Assert.True(await clockReader.ReadAsync());
        DateTimeOffset start = clockReader.GetFieldValue<DateTimeOffset>(0);
        await clockReader.CloseAsync();
        DateTimeOffset previewAt = start.AddDays(20);
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true,retain_for=interval '1 day',minimum_partitions_to_keep=1 WHERE data_class='m10_rollups';");
        await ExecuteAsync(database, "INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest) VALUES(@id,clock_timestamp(),'test','restore-tested',@expires,sha256(convert_to('test','UTF8')));",
            ("id", Guid.NewGuid()), ("expires", previewAt.AddDays(1)));

        Guid disjoint = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,from_utc,to_utc,status) VALUES(@job,'baseline',@target,1,'analytics/derivation',@from,@to,'queued');",
            ("job", disjoint), ("target", target), ("from", start.AddDays(10)), ("to", start.AddDays(11)));
        Assert.Equal((true, "eligible"), await PreviewAsync());

        Guid overlapping = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,from_utc,to_utc,status) VALUES(@job,'baseline',@target,1,'analytics/derivation',@from,@to,'queued');",
            ("job", overlapping), ("target", target), ("from", start.AddHours(1)), ("to", start.AddHours(2)));
        Assert.Equal((false, "dependency_pending"), await PreviewAsync());
        await ExecuteAsync(database, "UPDATE control.analytics_job SET status='succeeded' WHERE job_id=@job;", ("job", overlapping));
        Assert.Equal((true, "eligible"), await PreviewAsync());

        Guid unknown = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,status) VALUES(@job,'baseline',@target,1,'analytics/derivation','queued');",
            ("job", unknown), ("target", target));
        Assert.Equal((false, "dependency_pending"), await PreviewAsync());

        async Task<(bool Eligible, string Reason)> PreviewAsync()
        {
            await using var preview = new NpgsqlCommand("SELECT eligible,reason FROM system.preview_m10_retention(@now,NULL) WHERE data_class='m10_rollups' AND range_start=@start;", connection);
            preview.Parameters.AddWithValue("now", previewAt);
            preview.Parameters.AddWithValue("start", start);
            await using NpgsqlDataReader reader = await preview.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetBoolean(0), reader.GetString(1));
        }
    }

    [Fact]
    public async Task RetentionDetachAndDropRespectPartitionJobWindow()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), execution = Guid.NewGuid();
        await InsertTargetAsync(database, target, "retention-lifecycle", 1);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("""
            SET TIME ZONE 'UTC';
            SET ROLE sqlobserver_migrator;
            DO $$ DECLARE d date:=current_date-3; n text:=format('metric_rollup_v2_p%s',to_char(d,'YYYYMMDD')); BEGIN
              EXECUTE format('CREATE TABLE analytics.%I PARTITION OF analytics.metric_rollup_v2 FOR VALUES FROM (%L) TO (%L)',
                n,d::timestamptz,(d+1)::timestamptz);
              INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
              VALUES('analytics','metric_rollup_v2','analytics',n,'day',d::timestamptz,(d+1)::timestamptz);
            END $$;
            RESET ROLE;
            """, admin))
            await setup.ExecuteNonQueryAsync();
        await using var bounds = new NpgsqlCommand("SELECT range_start,partition_name::text FROM system.partition_registry WHERE parent_schema='analytics' AND parent_table='metric_rollup_v2' AND range_start=(current_date-3)::timestamptz;", admin);
        await using NpgsqlDataReader boundsReader = await bounds.ExecuteReaderAsync();
        Assert.True(await boundsReader.ReadAsync());
        DateTimeOffset start = boundsReader.GetFieldValue<DateTimeOffset>(0);
        string partition = boundsReader.GetString(1);
        await boundsReader.CloseAsync();
        await ExecuteAsync(database, "UPDATE system.retention_policy SET enabled=true,retain_for=interval '1 day',minimum_partitions_to_keep=1 WHERE data_class='m10_rollups';");
        await ExecuteAsync(database, "INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest) VALUES(@id,clock_timestamp(),'test','restore-tested',clock_timestamp()+interval '3 days',sha256(convert_to('test','UTF8')));", ("id", Guid.NewGuid()));
        await using (var privilege = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_collector','system.drop_m10_partition(uuid)','EXECUTE');", admin))
            Assert.False((bool)(await privilege.ExecuteScalarAsync())!);

        Guid disjoint = Guid.NewGuid(), overlapping = Guid.NewGuid();
        await AddJobAsync(disjoint, start.AddDays(10), start.AddDays(11));
        await AddJobAsync(overlapping, start.AddHours(1), start.AddHours(2));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(() => RetentionAsync("detach", Guid.NewGuid()));
        Assert.Equal("55000", blocked.SqlState);
        await ExecuteAsync(database, "UPDATE control.analytics_job SET status='succeeded' WHERE job_id=@job;", ("job", overlapping));
        Assert.True(await RetentionAsync("detach", Guid.NewGuid()));
        await ExecuteAsync(database, "UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 minute' WHERE execution_id=@execution;", ("execution", execution));

        Guid secondOverlap = Guid.NewGuid();
        await AddJobAsync(secondOverlap, start.AddHours(3), start.AddHours(4));
        Assert.False(await RetentionAsync("drop", Guid.NewGuid()));
        await ExecuteAsync(database, "UPDATE control.analytics_job SET status='succeeded' WHERE job_id=@job;", ("job", secondOverlap));

        await ExecuteAsync(database, $"CREATE VIEW analytics.retention_drop_dependency AS SELECT count(*) FROM analytics.\"{partition}\";");
        Assert.False(await RetentionAsync("drop", Guid.NewGuid()));
        await using (var failed = new NpgsqlCommand("SELECT state,attempt FROM system.retention_execution WHERE execution_id=@execution;", admin))
        {
            failed.Parameters.AddWithValue("execution", execution);
            await using NpgsqlDataReader reader = await failed.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("retry", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        await ExecuteAsync(database, "DROP VIEW analytics.retention_drop_dependency;");
        Assert.False(await RetentionAsync("drop", Guid.NewGuid()));
        await using (var retry = new NpgsqlCommand("SELECT error_code,attempt FROM system.retention_drop_retry JOIN system.retention_execution USING(execution_id) WHERE execution_id=@execution ORDER BY retry_id DESC LIMIT 1;", admin))
        {
            retry.Parameters.AddWithValue("execution", execution);
            await using NpgsqlDataReader reader = await retry.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("2BP01", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        await ExecuteAsync(database, "INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at) VALUES(@execution,'TEST','elapsed backoff',clock_timestamp()-interval '1 minute');", ("execution", execution));
        Assert.True(await RetentionAsync("drop", Guid.NewGuid()));
        await using var outcome = new NpgsqlCommand("SELECT lifecycle_state FROM system.partition_registry WHERE parent_schema='analytics' AND parent_table='metric_rollup_v2' AND partition_name=@partition;", admin);
        outcome.Parameters.AddWithValue("partition", partition);
        Assert.Equal("dropped", await outcome.ExecuteScalarAsync());
        await using var recovery = new NpgsqlCommand("SELECT state,attempt FROM system.retention_execution WHERE execution_id=@execution;", admin);
        recovery.Parameters.AddWithValue("execution", execution);
        await using NpgsqlDataReader recoveryReader = await recovery.ExecuteReaderAsync();
        Assert.True(await recoveryReader.ReadAsync());
        Assert.Equal("dropped", recoveryReader.GetString(0));
        Assert.Equal(1, recoveryReader.GetInt32(1));

        async Task AddJobAsync(Guid id, DateTimeOffset from, DateTimeOffset to) =>
            await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,from_utc,to_utc,status) VALUES(@job,'baseline',@target,1,'analytics/derivation',@from,@to,'queued');",
                ("job", id), ("target", target), ("from", from), ("to", to));

        async Task<bool> RetentionAsync(string operation, Guid operationId)
        {
            await using NpgsqlTransaction transaction = await serverConnection.BeginTransactionAsync();
            try
            {
                await using var context = new NpgsqlCommand("""
                    SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                           set_config('sqlobserver.authorization_scope','global',true),
                           set_config('sqlobserver.actor_sid','S-1-5-21-1-2-3-1001',true),
                           set_config('sqlobserver.retention_operation_id',@operation,true),
                           set_config('sqlobserver.retention_request_digest',repeat('a',64),true),
                           set_config('sqlobserver.retention_correlation_id',@correlation,true),
                           set_config('sqlobserver.retention_change_reason','retention window regression',true);
                    """, serverConnection, transaction);
                context.Parameters.AddWithValue("operation", operationId.ToString());
                context.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString());
                await context.ExecuteNonQueryAsync();
                string sql = operation == "detach"
                    ? "SELECT system.detach_m10_partition('analytics','metric_rollup_v2',@partition,@execution);"
                    : "SELECT system.drop_m10_partition(@execution);";
                await using var command = new NpgsqlCommand(sql, serverConnection, transaction);
                command.Parameters.AddWithValue("execution", execution);
                if (operation == "detach") command.Parameters.AddWithValue("partition", partition);
                bool result = (bool)(await command.ExecuteScalarAsync())!;
                await transaction.CommitAsync();
                return result;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
    }

    [Fact]
    public async Task PartitionMaintenanceExtendsOnlyActiveM10Streams()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using var maintain = collector.CreateCommand("SELECT control.ensure_m10_partition_set(current_date+1);");
        Assert.Equal(33, (int)(await maintain.ExecuteScalarAsync())!);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var inspect = new NpgsqlCommand("""
            SELECT parent_schema::text, parent_table::text
            FROM system.partition_registry
            WHERE partition_granularity='day'
              AND range_start=(current_date+8)::timestamptz
            ORDER BY parent_schema,parent_table;
            """, connection);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        var parents = new List<string>();
        while (await reader.ReadAsync())
            parents.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        Assert.Equal(3, parents.Count);
        Assert.Equal("analytics.metric_rollup_v2", parents[0]);
        Assert.Equal("telemetry.host_metric_snapshot_v2", parents[1]);
        Assert.Equal("telemetry.replication_snapshot_v2", parents[2]);
    }

    [Fact]
    public async Task BackfillInventoryFiltersBeforePagingAndBindsCursorsToItsSurface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        await InsertTargetAsync(database, target, "backfill-inventory", 1);
        await InsertTargetAsync(database, other, "other-inventory", 1);
        DateTimeOffset end = DateTimeOffset.UtcNow.AddMinutes(-1);
        await ExecuteAsync(database, """
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,requested_at,from_utc,to_utc)
            VALUES ('10000000-0000-0000-0000-000000000001','rollup',@target,1,'analytics/rollup',@at,@at-interval '1 hour',@at),
                   ('20000000-0000-0000-0000-000000000001','backfill',@target,1,'analytics/backfill',@at,@at-interval '1 hour',@at),
                   ('20000000-0000-0000-0000-000000000002','backfill',@target,1,'analytics/backfill',@at,@at-interval '1 hour',@at),
                   ('20000000-0000-0000-0000-000000000003','backfill',@other,1,'analytics/backfill',@at,@at-interval '1 hour',@at);
            """, ("target", target), ("other", other), ("at", end));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var adapter = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        AnalyticsSurfacePage first = await adapter.ReadSurfaceAsync(target, "backfill", end.AddHours(-1), end, 1, null, default);
        Assert.Equal("backfill", first.Surface);
        Assert.Equal("backfill", Assert.Single(first.Items).GetProperty("jobKind").GetString());
        AnalyticsSurfacePage second = await adapter.ReadSurfaceAsync(target, "backfill", end.AddHours(-1), end, 1, first.NextCursor, default);
        Assert.NotEqual(first.Items[0].GetProperty("jobId").GetGuid(), Assert.Single(second.Items).GetProperty("jobId").GetGuid());
        Assert.Empty((await adapter.ReadSurfaceAsync(target, "backfill", end.AddHours(-1), end, 1, second.NextCursor, default)).Items);
        await Assert.ThrowsAsync<ArgumentException>(async () => await adapter.ReadSurfaceAsync(target, "jobs", end.AddHours(-1), end, 1, first.NextCursor, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await adapter.ReadSurfaceAsync(other, "backfill", end.AddHours(-1), end, 1, first.NextCursor, default));
        await using var direct = server.CreateCommand("SELECT count(*) FROM control.analytics_job");
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task DerivationReadsCloseTheirReadersBeforeCompletingTransactions()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertTargetAsync(database, target.Value, "reader-lifecycle", 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var adapter = new PostgreSqlAnalyticsRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]));
        DateTimeOffset end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var job = new AnalyticsDerivationJob(Guid.NewGuid(), target, new ObservationTargetRevision(1), "rollup",
            end.AddHours(-1), end, end, MetricKey: "host.cpu.percent", RollupInterval: SqlObserver.Domain.Analytics.RollupInterval.Hour);

        Assert.Empty(await adapter.ReadRollupInputsAsync(job, SqlObserver.Domain.Analytics.RollupInterval.Hour, CancellationToken.None));
        Assert.Empty(await adapter.ReadEvidenceInputsAsync(job with { JobKind = "evidence", MetricKey = null, RollupInterval = null }, CancellationToken.None));
        Assert.Empty(await adapter.ReadIncidentInputsAsync(job with { JobKind = "correlation", MetricKey = null, RollupInterval = null }, CancellationToken.None));
    }

    [Fact]
    public async Task ServerReadsRetentionPolicyWithoutDirectPolicyTableAccess()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var adapter = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        foreach (string dataClass in new[] { "m10_host_metrics", "m10_replication", "m10_rollups", "m10_evidence" })
        {
            RetentionPolicyReadResult result = await adapter.GetPolicyAsync(dataClass, CancellationToken.None);
            Assert.Equal(dataClass, result.Policy.DataClass);
            Assert.False(result.Policy.Enabled);
        }
        await using var direct = server.CreateCommand("SELECT count(*) FROM system.retention_policy");
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using var collectorRead = collector.CreateCommand("SELECT * FROM system.get_m10_retention_policy('m10_host_metrics')");
        denied = await Assert.ThrowsAsync<PostgresException>(() => collectorRead.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task BackfillReceiptCommitsAndReplaysWithoutAnOpenReader()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await InsertTargetAsync(database, target.Value, "backfill-reader-lifecycle", 1);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var adapter = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        DateTimeOffset end = new(2026, 9, 15, 1, 0, 0, TimeSpan.Zero);
        var request = new BackfillMutationRequest(target.Value, end.AddHours(-1), end, "host.cpu.percent", 1,
            Guid.NewGuid(), new byte[32], "S-1-5-21-1-2-3-1001", Guid.NewGuid(), "Backfill transaction regression");
        AnalyticsMutationReceipt receipt = await adapter.StartBackfillAsync(request, CancellationToken.None);
        Assert.Equal("queued", receipt.State);
        Assert.Equal(1, receipt.Revision);
        AnalyticsMutationReceipt replay = await adapter.StartBackfillAsync(request, CancellationToken.None);
        Assert.Equal("replayed", replay.State);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM control.analytics_job WHERE instance_id=@target AND job_kind='backfill'", connection);
        count.Parameters.AddWithValue("target", target.Value);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ReplicationBindingResolverUsesCanonicalTextTargetScope()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-replication-binding", 1);
        await ExecuteAsync(
            database,
            "INSERT INTO control.replication_distribution_binding(instance_id,target_revision,database_name,tcp_port) VALUES(@target,1,'distribution',1433);",
            ("target", target));

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var resolver = new PostgreSqlReplicationDistributionBindingResolver(collector);
        ReplicationDistributionBinding? binding = await resolver.ResolveAsync(
            new MonitoredInstanceId(target),
            new ObservationTargetRevision(1),
            CancellationToken.None);

        Assert.NotNull(binding);
        Assert.Equal("distribution", binding.DatabaseName);
        Assert.Equal(1433, binding.TcpPort);
    }

    [Fact]
    public async Task M10MigrationCreatesCatalogRevisionFencesAndPartitionedSurface()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string sql = """
            SELECT
              (SELECT count(*) FROM analytics.metric_catalog),
              to_regclass('control.host_binding') IS NOT NULL,
              to_regclass('control.host_profile') IS NOT NULL,
              to_regclass('control.replication_profile') IS NOT NULL,
              to_regclass('control.analytics_job') IS NOT NULL,
              to_regclass('telemetry.host_metric_snapshot_v2') IS NOT NULL,
              to_regclass('telemetry.replication_snapshot_v2') IS NOT NULL,
              to_regclass('analytics.metric_rollup_v2') IS NOT NULL,
              to_regprocedure('control.resolve_m10_target_revision(uuid,bigint)') IS NOT NULL,
              to_regprocedure('analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)') IS NOT NULL,
              to_regprocedure('analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea)') IS NOT NULL,
              (SELECT count(*) FROM system.partition_registry WHERE parent_schema='analytics' AND parent_table='metric_rollup_v2' AND partition_granularity='day') >= 9;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(10L, reader.GetInt64(0));
        for (int i = 1; i < 12; i++) Assert.True(reader.GetBoolean(i), $"M10 shape column {i} was false.");
        await reader.CloseAsync();

        await using var catalog = new NpgsqlCommand("SELECT metric_key,(definition->'dimensions')::text FROM analytics.metric_catalog ORDER BY metric_key;", connection);
        await using NpgsqlDataReader catalogReader = await catalog.ExecuteReaderAsync();
        var catalogDimensions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await catalogReader.ReadAsync())
            catalogDimensions[catalogReader.GetString(0)] = catalogReader.GetString(1);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host.cpu.percent"] = "[]", ["host.memory.available_bytes"] = "[]", ["host.memory.committed_bytes"] = "[]",
                ["host.volume.free_bytes"] = "[\"volume\"]", ["host.volume.queue_length"] = "[\"volume\"]",
                ["host.volume.read_latency_ms"] = "[\"volume\"]", ["host.volume.total_bytes"] = "[\"volume\"]",
                ["host.volume.write_latency_ms"] = "[\"volume\"]", ["replication.latency_seconds"] = "[]", ["replication.pending_commands"] = "[]",
            }, catalogDimensions);
    }

    [Fact]
    public async Task M10FreshForecastSchemaStoresExplicitDimensionIdentity()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-forecast-schema", 1);
        Guid forecast = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        await ExecuteAsync(database, "INSERT INTO analytics.metric_forecast(forecast_id,instance_id,target_revision,metric_key,horizon_start,horizon_end,model,predicted_value,source_generation,visibility_state,dimension_hash) VALUES(@forecast,@target,1,'host.volume.free_bytes',@start,@end,'test',1,1,'complete',sha256(convert_to('{}','UTF8')));", ("forecast", forecast), ("target", target), ("start", start), ("end", start.AddHours(1)));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT dimensions::text,octet_length(dimension_hash),EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='analytics' AND table_name='metric_forecast' AND column_name='dimensions') FROM analytics.metric_forecast WHERE forecast_id=@forecast;", connection);
        command.Parameters.AddWithValue("forecast", forecast);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("{}", reader.GetString(0));
        Assert.Equal(32, reader.GetInt32(1));
        Assert.True(reader.GetBoolean(2));
    }

    [Fact]
    public async Task M10CanonicalBackfillClaimIsCollectorOnly()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        string[] signatures =
        [
            "control.claim_m10_analytics_jobs(text,uuid,bigint,integer)",
            "reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer)",
            "control.ensure_m10_backfill_partition(uuid,bigint,timestamptz,uuid,bigint)",
            "control.advance_m10_backfill_cursor(uuid,timestamptz,text,uuid,bigint)",
            "control.complete_m10_analytics_job(uuid,text,text,uuid,bigint)",
            "control.replay_m10_analytics_job(uuid,uuid,uuid,bigint,uuid,bigint,bytea,bytea,jsonb)",
        ];
        foreach (string signature in signatures)
        {
            await using var privileges = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_collector',@signature,'EXECUTE'),has_function_privilege('sqlobserver_server',@signature,'EXECUTE');", admin);
            privileges.Parameters.AddWithValue("signature", signature);
            await using NpgsqlDataReader reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0), $"Collector lacks EXECUTE on {signature}.");
            Assert.False(reader.GetBoolean(1), $"Server unexpectedly has EXECUTE on {signature}.");
        }

        Guid target = Guid.NewGuid();
        Guid job = Guid.NewGuid();
        DateTimeOffset from = DateTimeOffset.UtcNow.UtcDateTime.AddDays(-1).Date;
        await InsertTargetAsync(database, target, "m10-backfill-role", 1);
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,status,work_key) VALUES(@job,'backfill',@target,1,@from,@to,'queued','analytics/backfill');", ("job", job), ("target", target), ("from", from), ("to", from.AddHours(1)));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "analytics/backfill", Guid.NewGuid());
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await using (var claim = new NpgsqlCommand("SELECT job_id FROM control.claim_m10_analytics_jobs('analytics/backfill',@owner,@token,1);", collectorConnection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            object? claimed = await claim.ExecuteScalarAsync();
            Assert.Equal(job, claimed);
        }
        var backfills = new PostgreSqlAnalyticsBackfillStore(collector);
        var claimedJob = new SqlObserver.Analytics.AnalyticsBackfillJob(job, new MonitoredInstanceId(target), new ObservationTargetRevision(1), from, from.AddHours(1), null);
        var lease = new SqlObserver.Domain.Coordination.WorkerLeaseIdentity(new("analytics/backfill"), new(owner), new(token));
        await backfills.SaveCursorAsync(claimedJob, lease, from, null, CancellationToken.None);
        string cursorJson = JsonSerializer.Serialize(new { v = "m10.backfill.v2", sourceKind = "host", observedAtUtc = from.AddMinutes(5), sourceId = Guid.NewGuid(), metricKey = "host.cpu.percent", dimensionHash = CanonicalDimensions.Sha256(null), ordinal = 2, targetId = target, targetRevision = 1, dayStartUtc = from, catalogVersion = 1, jobId = job });
        string cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cursorJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await backfills.SaveCursorAsync(claimedJob, lease, from, cursor, CancellationToken.None);
        await using (var persisted = new NpgsqlCommand("SELECT cursor_ordinal,cursor_target_revision,cursor_metric_key FROM control.analytics_job WHERE job_id=@job", admin))
        {
            persisted.Parameters.AddWithValue("job", job);
            await using NpgsqlDataReader reader = await persisted.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("host.cpu.percent", reader.GetString(2));
        }
        var staleLease = new SqlObserver.Domain.Coordination.WorkerLeaseIdentity(lease.Key, lease.Owner, new(token + 1));
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() => backfills.SaveCursorAsync(claimedJob, staleLease, from, null, CancellationToken.None).AsTask());
        Assert.Equal("55000", stale.SqlState);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await using var denied = new NpgsqlCommand("SELECT job_id FROM control.claim_m10_analytics_jobs('analytics/backfill',@owner,@token,1);", serverConnection);
        denied.Parameters.AddWithValue("owner", owner);
        denied.Parameters.AddWithValue("token", token);
        PostgresException serverDenied = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync());
        Assert.Equal("42501", serverDenied.SqlState);
    }

    [Fact]
    public async Task M10RolesDenyBaseTablesAndRevisionResolverRejectsUnscopedOrStaleReads()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid otherTarget = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-target", 1);
        await InsertTargetAsync(database, otherTarget, "m10-other", 1);

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        foreach (string protectedTable in new[] { "control.host_profile", "telemetry.host_metric_snapshot_v2", "analytics.metric_rollup_v2", "control.analytics_job" })
        {
            await using var direct = new NpgsqlCommand($"SELECT count(*) FROM {protectedTable};", collectorConnection);
            PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
            Assert.Equal("42501", denied.SqlState);
        }

        await SetScopeAsync(collectorConnection, target);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var unscoped = new NpgsqlCommand(
            "SELECT count(*) FROM reporting.list_metric_series(@target,1,@from,@to,'host.cpu.percent',10,@snapshot);", serverConnection))
        {
            unscoped.Parameters.AddWithValue("target", otherTarget);
            unscoped.Parameters.AddWithValue("from", now.AddDays(-1));
            unscoped.Parameters.AddWithValue("to", now.AddDays(1));
            unscoped.Parameters.AddWithValue("snapshot", now.AddMinutes(1));
            Assert.Equal(0L, Convert.ToInt64(await unscoped.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target;", ("target", target));
        await using var stale = new NpgsqlCommand(
            "SELECT control.resolve_m10_target_revision(@target,1);", serverConnection);
        stale.Parameters.AddWithValue("target", target);
        stale.Parameters.AddWithValue("from", now.AddDays(-1));
        stale.Parameters.AddWithValue("to", now.AddDays(1));
        stale.Parameters.AddWithValue("snapshot", now.AddMinutes(1));
        PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => stale.ExecuteScalarAsync());
        Assert.Equal("40001", conflict.SqlState);
    }

    [Fact]
    public async Task M10DerivationSchedulerCreatesLiveRollupAndDerivationJobsForCanonicalClaimLane()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-rollup", 1);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        (long token, _) = await AcquireLeaseAsync(collector, "analytics/derivation", owner);

        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        int scheduled = await ScalarIntAsync(connection, "SELECT control.schedule_m10_derivation_jobs(@owner,@token);", ("owner", owner), ("token", token));
        Assert.Equal(30, scheduled);
        int repeat = await ScalarIntAsync(connection, "SELECT control.schedule_m10_derivation_jobs(@owner,@token);", ("owner", owner), ("token", token));
        Assert.Equal(0, repeat);

        await using NpgsqlConnection inspection = await database.DataSource.OpenConnectionAsync();
        await using (var counts = new NpgsqlCommand("SELECT count(*) FILTER (WHERE job_kind='rollup'),count(*) FILTER (WHERE job_kind<>'rollup') FROM control.analytics_job WHERE instance_id=@target;", inspection))
        {
            counts.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader countReader = await counts.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(30L, countReader.GetInt64(0));
            Assert.Equal(3L, countReader.GetInt64(1));
        }

        PostgresException invalidLimit = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarIntAsync(connection, "SELECT count(*)::integer FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,3);", ("owner", owner), ("token", token)));
        Assert.Equal("22023", invalidLimit.SqlState);

        Guid jobId;
        string jobKind;
        await using (var claim = new NpgsqlCommand("SELECT job_id,job_kind FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,2) ORDER BY job_id LIMIT 1;", connection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            await using NpgsqlDataReader claimReader = await claim.ExecuteReaderAsync();
            Assert.True(await claimReader.ReadAsync());
            jobId = claimReader.GetGuid(0);
            jobKind = claimReader.GetString(1);
        }
        Assert.True(jobKind is "baseline" or "evidence" or "correlation" or "rollup");
        Assert.True(await ScalarBoolAsync(connection, "SELECT control.complete_m10_derivation_job(@job,'succeeded',NULL,@owner,@token);", ("job", jobId), ("owner", owner), ("token", token)));
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarBoolAsync(connection, "SELECT control.complete_m10_derivation_job(@job,'succeeded',NULL,@owner,@token);", ("job", jobId), ("owner", owner), ("token", token + 1)));
        Assert.Equal("55000", stale.SqlState);

        await using var status = new NpgsqlCommand("SELECT status,attempt FROM control.analytics_job WHERE job_id=@job;", inspection);
        status.Parameters.AddWithValue("job", jobId);
        await using NpgsqlDataReader reader = await status.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("succeeded", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task M10DerivationSchedulerUsesLatestCompletedUtcBucketsAcrossFiveMinuteAndHourBoundaries()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();

        DateTimeOffset first = new(2026, 8, 26, 10, 4, 59, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, first, new BucketExpectation("5m", "2026-08-26T09:55:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset fiveMinuteBoundary = new(2026, 8, 26, 10, 5, 0, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, fiveMinuteBoundary, new BucketExpectation("5m", "2026-08-26T10:00:00Z", "2026-08-26T10:05:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset beforeHour = new(2026, 8, 26, 10, 59, 59, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, beforeHour, new BucketExpectation("5m", "2026-08-26T10:50:00Z", "2026-08-26T10:55:00Z"), new BucketExpectation("hour", "2026-08-26T09:00:00Z", "2026-08-26T10:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));

        DateTimeOffset hourBoundary = new(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        await AssertDueBucketsAsync(connection, hourBoundary, new BucketExpectation("5m", "2026-08-26T10:55:00Z", "2026-08-26T11:00:00Z"), new BucketExpectation("hour", "2026-08-26T10:00:00Z", "2026-08-26T11:00:00Z"), new BucketExpectation("day", "2026-08-25T00:00:00Z", "2026-08-26T00:00:00Z"));
    }

    [Fact]
    public async Task M10RollupCommitReplaysAndDimensionScopedReadReturnsOnlyRequestedIdentity()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target, "m10-dimensions", 1);
        DateTime utcNow = DateTime.UtcNow;
        DateTime bucket = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, (utcNow.Minute / 5) * 5, 0, DateTimeKind.Utc).AddMinutes(-5);
        DateTimeOffset from = new(bucket, TimeSpan.Zero);
        Guid queuedJob = Guid.NewGuid();
        await ExecuteAsync(database, "INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval) VALUES(@job,'rollup',@target,1,@from,@to,'host.cpu.percent',@to,1,'queued','analytics/derivation','5m');", ("job", queuedJob), ("target", target), ("from", from), ("to", from.AddMinutes(5)));
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        (long token, _) = await AcquireLeaseAsync(collector, "analytics/derivation", owner);
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        Guid jobId;
        string claimedInterval;
        await using (var claim = new NpgsqlCommand("SELECT job_id,from_utc,rollup_interval FROM control.claim_m10_derivation_jobs('analytics/derivation',@owner,@token,1);", connection))
        {
            claim.Parameters.AddWithValue("owner", owner);
            claim.Parameters.AddWithValue("token", token);
            await using NpgsqlDataReader reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            jobId = reader.GetGuid(0);
            Assert.Equal(queuedJob, jobId);
            from = reader.GetFieldValue<DateTimeOffset>(1);
            claimedInterval = reader.GetString(2);
        }
        Assert.Equal("5m", claimedInterval);
        await SetScopeAsync(connection, target);
        byte[] digest = Enumerable.Repeat((byte)7, 32).ToArray();
        string dimensionHash = CanonicalDimensions.Sha256(new Dictionary<string, string> { ["cpu"] = "all" });
        string rows = JsonSerializer.Serialize(new[]
        {
            new { bucketStartUtc = from.ToUniversalTime().ToString("O"), interval = "5m", metricKey = "host.cpu.percent", aggregation = "avg", sampleCount = 2, value = 12.5, visibilityState = "complete", dimensions = new { cpu = "all" }, dimensionHash, generation = 1, sourceCutoffUtc = from.ToUniversalTime().ToString("O") },
            new { bucketStartUtc = from.ToUniversalTime().ToString("O"), interval = "5m", metricKey = "host.cpu.percent", aggregation = "avg", sampleCount = 1, value = 99.0, visibilityState = "complete", dimensions = new { cpu = "0" }, dimensionHash = new string('b', 64), generation = 1, sourceCutoffUtc = from.ToUniversalTime().ToString("O") },
        });
        Guid operation = Guid.NewGuid();
        string commitSql = "SELECT result_status,inserted_count FROM analytics.commit_metric_rollups(@op,@job,@target,1,'analytics/derivation',@owner,@token,@request,@rows::jsonb,@result);";
        await using (var commit = new NpgsqlCommand(commitSql, connection))
        {
            commit.Parameters.AddWithValue("op", operation);
            commit.Parameters.AddWithValue("job", jobId);
            commit.Parameters.AddWithValue("target", target);
            commit.Parameters.AddWithValue("owner", owner);
            commit.Parameters.AddWithValue("token", token);
            commit.Parameters.AddWithValue("request", digest);
            commit.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, rows);
            commit.Parameters.AddWithValue("result", digest);
            await using NpgsqlDataReader reader = await commit.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("committed", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
        }

        await using (var replay = new NpgsqlCommand(commitSql, connection))
        {
            replay.Parameters.AddWithValue("op", operation);
            replay.Parameters.AddWithValue("job", jobId);
            replay.Parameters.AddWithValue("target", target);
            replay.Parameters.AddWithValue("owner", owner);
            replay.Parameters.AddWithValue("token", token);
            replay.Parameters.AddWithValue("request", digest);
            replay.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, rows);
            replay.Parameters.AddWithValue("result", digest);
            await using NpgsqlDataReader reader = await replay.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("replayed", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
        }

        string changedRows = rows.Replace("12.5", "12.6", StringComparison.Ordinal);
        await using (var divergent = new NpgsqlCommand(commitSql, connection))
        {
            divergent.Parameters.AddWithValue("op", operation);
            divergent.Parameters.AddWithValue("job", jobId);
            divergent.Parameters.AddWithValue("target", target);
            divergent.Parameters.AddWithValue("owner", owner);
            divergent.Parameters.AddWithValue("token", token);
            divergent.Parameters.AddWithValue("request", digest);
            divergent.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, changedRows);
            divergent.Parameters.AddWithValue("result", digest);
            PostgresException divergence = await Assert.ThrowsAsync<PostgresException>(() => divergent.ExecuteReaderAsync());
            Assert.Equal("40001", divergence.SqlState);
        }

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        DateTimeOffset snapshot = DateTimeOffset.UtcNow.AddMinutes(1);
        byte[] requestedDimension = Convert.FromHexString(dimensionHash);
        await using var read = new NpgsqlCommand("SELECT count(*),min(value),max(value) FROM analytics.list_metric_rollups_scoped(@target,1,'host.cpu.percent',@from,@to,100,@snapshot,'5m',NULL,NULL,NULL,NULL,@dimension);", serverConnection);
        read.Parameters.AddWithValue("target", target);
        read.Parameters.AddWithValue("from", from.AddMinutes(-1));
        read.Parameters.AddWithValue("to", from.AddMinutes(6));
        read.Parameters.AddWithValue("snapshot", snapshot);
        read.Parameters.AddWithValue("dimension", requestedDimension);
        await using NpgsqlDataReader scopedReader = await read.ExecuteReaderAsync();
        Assert.True(await scopedReader.ReadAsync());
        Assert.Equal(1L, scopedReader.GetInt64(0));
        Assert.Equal(12.5, scopedReader.GetDouble(1));
        Assert.Equal(12.5, scopedReader.GetDouble(2));
        await scopedReader.DisposeAsync();

        var repository = new PostgreSqlAnalyticsRepositoryPort(server, new IdentityFingerprintKey(new byte[32]));
        AnalyticsRollupPage page = await repository.ReadRollupPageAsync(
            new AnalyticsQueryRequest(new MonitoredInstanceId(target), "host.cpu.percent", from.AddMinutes(-1), from.AddMinutes(6), 100,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), new ObservationTargetRevision(1), snapshot, dimensionHash),
            RollupInterval.FiveMinutes, null, CancellationToken.None);
        RollupResult hydrated = Assert.Single(page.Items);
        Assert.Equal(12.5, hydrated.Mean);
        Assert.Equal(1, hydrated.Generation);
        Assert.Null(hydrated.Last);
        Assert.Null(hydrated.CounterDelta);
        Assert.Null(hydrated.RatePerSecond);
    }

    [Fact]
    public async Task M10HostCommitRejectsRowAndPayloadByteBounds()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('c', 64);
        await InsertTargetAsync(database, target, "m10-host-bounds", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-host", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        object[] tooManyRows = Enumerable.Range(0, 257)
            .Select(i => (object)new { observedAtUtc = observed.AddSeconds(-i).ToString("O"), metricKey = "host.volume.free_bytes", value = (double)i, dimensions = new Dictionary<string, string> { ["volume"] = $"v{i}" } })
            .ToArray();
        byte[] digest = Enumerable.Repeat((byte)8, 32).ToArray();
        string rowBoundPayload = HostPayload(target, host, fingerprint, tooManyRows);
        PostgresException rowBound = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-host", owner, token, digest, rowBoundPayload, digest));
        Assert.Equal("22023", rowBound.SqlState);

        string byteBoundPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.volume.free_bytes", value = 1.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } }], new string('x', 300_000));
        PostgresException byteBound = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-host", owner, token, digest, byteBoundPayload, digest));
        Assert.Equal("22023", byteBound.SqlState);
    }

    [Fact]
    public async Task M10HostAndReplicationReplayRejectChangedPayloadWithReusedDigest()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('d', 64);
        await InsertTargetAsync(database, target, "m10-replay-digest", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-host", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        byte[] digest = Enumerable.Repeat((byte)9, 32).ToArray();
        string hostPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.cpu.percent", value = 10.0, dimensions = new { } }]);
        Guid hostRun = Guid.NewGuid();
        (string hostStatus, int hostCount) = await CommitHostAsync(connection, hostRun, target, "collector/m10-host", owner, token, digest, hostPayload, digest);
        Assert.Equal("committed", hostStatus);
        Assert.Equal(1, hostCount);
        string changedHostPayload = HostPayload(target, host, fingerprint, [new { observedAtUtc = observed.ToString("O"), metricKey = "host.cpu.percent", value = 11.0, dimensions = new { } }]);
        PostgresException hostDivergence = await Assert.ThrowsAsync<PostgresException>(() => CommitHostAsync(connection, hostRun, target, "collector/m10-host", owner, token, digest, changedHostPayload, digest));
        Assert.Equal("40001", hostDivergence.SqlState);

        string replicationPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, targetId = target.ToString(), targetRevision = 1,
            items = new[] { new { observedAtUtc = observed.ToString("O"), topologyFingerprint = new string('e', 64), role = "primary", synchronizationState = "synchronized", pendingCommands = 3, latencySeconds = 1.5, visibilityScope = 1, stateAvailable = true } },
        });
        Guid replicationRun = Guid.NewGuid();
        (long replicationToken, Guid replicationOwner) = await AcquireLeaseAsync(collector, "collector/m10-replication", Guid.NewGuid());
        (string replicationStatus, int replicationCount) = await CommitReplicationAsync(connection, replicationRun, target, "collector/m10-replication", replicationOwner, replicationToken, digest, replicationPayload, digest);
        Assert.Equal("committed", replicationStatus);
        Assert.Equal(1, replicationCount);
        string changedReplicationPayload = replicationPayload.Replace("synchronized", "unknown", StringComparison.Ordinal);
        PostgresException replicationDivergence = await Assert.ThrowsAsync<PostgresException>(() => CommitReplicationAsync(connection, replicationRun, target, "collector/m10-replication", replicationOwner, replicationToken, digest, changedReplicationPayload, digest));
        Assert.Equal("40001", replicationDivergence.SqlState);

        // Command counts must keep their unit through the public surface.
        // They are not Always On send/redo queue byte measurements.
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var surfaces = new PostgreSqlAnalyticsRepositoryPort(server,
            new SqlObserver.Domain.Security.IdentityFingerprintKey(new byte[32]));
        AnalyticsSurfacePage page = await surfaces.ReadSurfaceAsync(target, "replication/status",
            observed.AddMinutes(-1), observed.AddMinutes(1), 10, null, CancellationToken.None);
        JsonElement item = Assert.Single(page.Items);
        Assert.Equal(3, item.GetProperty("pendingCommands").GetInt64());
        Assert.Equal(1.5, item.GetProperty("latencySeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("sendQueueBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("redoQueueBytes").ValueKind);
        var targetId = new MonitoredInstanceId(target);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-711"),
            AuthorizationPrincipalState.Active, [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([targetId]));
        foreach ((string metric, double expected) in new[] { ("host.cpu.percent", 10.0), ("replication.pending_commands", 3.0), ("replication.latency_seconds", 1.5) })
        {
            var series = await surfaces.ReadMetricSeriesAsync(new MetricSeriesQuery(authorization, targetId,
                metric, observed.AddMinutes(-1), observed.AddMinutes(1), 100,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), CancellationToken.None);
            Assert.Equal(expected, Assert.Single(series.Items).Value);
        }

        // Existing host and replication history must not prevent rediscovery.
        await ExecuteAsync(database, "UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;", ("target", target));
        page = await surfaces.ReadSurfaceAsync(target, "replication/status",
            observed.AddMinutes(-1), observed.AddMinutes(1), 10, null, CancellationToken.None);
        Assert.Empty(page.Items);
        await using var historical = new NpgsqlCommand("SELECT target_revision FROM telemetry.replication_snapshot_v2 WHERE instance_id=@target", await database.DataSource.OpenConnectionAsync());
        historical.Parameters.AddWithValue("target", target);
        Assert.Equal(1L, await historical.ExecuteScalarAsync());
        await historical.Connection!.DisposeAsync();
    }

    [Fact]
    public async Task M10ForecastCapacityForFreeBytesUsesTotalBytesInTheSameDimension()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        Guid host = Guid.NewGuid();
        string fingerprint = new string('f', 64);
        await InsertTargetAsync(database, target, "m10-capacity", 1);
        await SeedHostAsync(database, target, host, fingerprint);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        (long token, Guid owner) = await AcquireLeaseAsync(collector, "collector/m10-capacity", Guid.NewGuid());
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        string payload = HostPayload(target, host, fingerprint,
        [
            new { observedAtUtc = observed.ToString("O"), metricKey = "host.volume.free_bytes", value = 100.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } },
            new { observedAtUtc = observed.AddSeconds(1).ToString("O"), metricKey = "host.volume.total_bytes", value = 1000.0, dimensions = new Dictionary<string, string> { ["volume"] = "C" } },
        ]);
        byte[] digest = Enumerable.Repeat((byte)10, 32).ToArray();
        (string status, int count) = await CommitHostAsync(connection, Guid.NewGuid(), target, "collector/m10-capacity", owner, token, digest, payload, digest);
        Assert.Equal("committed", status);
        Assert.Equal(2, count);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        await SetScopeAsync(serverConnection, target);
        await using var capacity = new NpgsqlCommand("SELECT reporting.get_m10_forecast_capacity_scoped(@target,1,'host.volume.free_bytes',@dimensions,@snapshot);", serverConnection);
        capacity.Parameters.AddWithValue("target", target);
        capacity.Parameters.AddWithValue("dimensions", NpgsqlDbType.Jsonb, "{\"volume\": \"C\"}");
        capacity.Parameters.AddWithValue("snapshot", observed.AddMinutes(1));
        Assert.Equal(1000.0, Convert.ToDouble(await capacity.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertTargetAsync(RepositoryTestDatabase database, Guid target, string key, long revision) =>
        await ExecuteAsync(database, "INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at) VALUES(@target,@key,'M10 target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',@revision,statement_timestamp(),statement_timestamp(),statement_timestamp());", ("target", target), ("key", key), ("revision", revision));

    private static async Task<(long Token, Guid Owner)> AcquireLeaseAsync(NpgsqlDataSource dataSource, string key, Guid owner)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');", connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("owner", owner);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        return (reader.GetInt64(1), owner);
    }

    private static Task<int> SetScopeAsync(NpgsqlConnection connection, Guid target) =>
        new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection) { Parameters = { new NpgsqlParameter("scope", target.ToString()) } }.ExecuteNonQueryAsync();

    private static async Task<int> ScalarIntAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDueBucketsAsync(NpgsqlConnection connection, DateTimeOffset now, params BucketExpectation[] expected)
    {
        await using var command = new NpgsqlCommand("SELECT interval_name,bucket_start,bucket_start+width FROM control.m10_rollup_due_buckets(@now) ORDER BY interval_name;", connection);
        command.Parameters.AddWithValue("now", now);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        var actual = new Dictionary<string, (DateTimeOffset From, DateTimeOffset To)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            actual[reader.GetString(0)] = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
        }
        Assert.Equal(expected.Length, actual.Count);
        foreach (BucketExpectation bucket in expected)
        {
            Assert.True(actual.TryGetValue(bucket.Interval, out (DateTimeOffset From, DateTimeOffset To) range));
            Assert.Equal(DateTimeOffset.Parse(bucket.FromUtc, System.Globalization.CultureInfo.InvariantCulture), range.From);
            Assert.Equal(DateTimeOffset.Parse(bucket.ToUtc, System.Globalization.CultureInfo.InvariantCulture), range.To);
        }
    }

    private readonly record struct BucketExpectation(string Interval, string FromUtc, string ToUtc);

    private static async Task SeedHostAsync(RepositoryTestDatabase database, Guid target, Guid host, string fingerprint)
    {
        byte[] identity = Convert.FromHexString(fingerprint);
        await ExecuteAsync(database, "INSERT INTO control.host_binding(instance_id,target_revision,host_id,binding_revision,host_name,identity_fingerprint,binding_state) VALUES(@target,1,@host,1,'sql01',@fingerprint,'active'); INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,os_family,os_version,cpu_count,memory_bytes,capability_state,profile) VALUES(@target,1,@host,1,1,'Windows','2026',4,4294967296,'available','{\"osFamily\":\"Windows\",\"osVersion\":\"2026\",\"cpuCount\":4,\"memoryBytes\":4294967296,\"capabilityState\":\"available\"}'::jsonb);", ("target", target), ("host", host), ("fingerprint", identity));
    }

    private static string HostPayload(Guid target, Guid host, string fingerprint, IEnumerable<object> items, string? padding = null)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = 1, ["targetId"] = target.ToString(), ["targetRevision"] = 1,
            ["hostId"] = host.ToString(), ["hostFingerprint"] = fingerprint,
            ["bindingRevision"] = 1, ["profileRevision"] = 1, ["items"] = items.ToArray(),
        };
        if (padding is not null) envelope["padding"] = padding;
        return JsonSerializer.Serialize(envelope);
    }

    private static async Task<(string Status, int Count)> CommitHostAsync(NpgsqlConnection connection, Guid run, Guid target, string workKey, Guid owner, long token, byte[] requestDigest, string payload, byte[] completionDigest)
    {
        await using var command = new NpgsqlCommand("SELECT result_status,inserted_count FROM telemetry.commit_m10_host_metrics(@run,@target,1,@work_key,@owner,@token,@request,@payload::jsonb,@completion);", connection);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("work_key", workKey); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("token", token); command.Parameters.AddWithValue("request", requestDigest); command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("completion", completionDigest);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static async Task<(string Status, int Count)> CommitReplicationAsync(NpgsqlConnection connection, Guid run, Guid target, string workKey, Guid owner, long token, byte[] requestDigest, string payload, byte[] completionDigest)
    {
        await using var command = new NpgsqlCommand("SELECT result_status,inserted_count FROM telemetry.commit_m10_replication(@run,@target,1,@work_key,@owner,@token,@request,@payload::jsonb,@completion);", connection);
        command.Parameters.AddWithValue("run", run); command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("work_key", workKey); command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("token", token); command.Parameters.AddWithValue("request", requestDigest); command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("completion", completionDigest);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetInt32(1));
    }
}
