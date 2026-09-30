using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupV2RetentionTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task UpgradeKeepsV1PolicyAndFencedMaintenanceCreatesNinePrivateRegisteredPartitions()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        Assert.False((await migrations.ApplyPendingAsync(new(271, timeout), CancellationToken.None)).HasFailures);
        await ExecuteAsync(database, "UPDATE system.retention_policy SET retain_for=interval '365 days',policy_revision=7,updated_by='operator-fixture' WHERE data_class='m9_backup_status';");
        Assert.False((await migrations.ApplyPendingAsync(new(MigrationBatchResult.MaximumResults, timeout), CancellationToken.None)).HasFailures);
        await using (var old = database.DataSource.CreateCommand("SELECT retain_for,policy_revision,updated_by FROM system.retention_policy WHERE data_class='m9_backup_status';"))
        {
            await using NpgsqlDataReader row = await old.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(TimeSpan.FromDays(365), row.GetFieldValue<TimeSpan>(0)); Assert.Equal(7L, row.GetInt64(1)); Assert.Equal("operator-fixture", row.GetString(2));
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await AcquireAsync(collector, "collector/catalog/reconcile");
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        Assert.True(await maintenance.EnsureM10PartitionSetAsync(lease, Timeout, CancellationToken.None) >= 9);
        await using (var again = collector.CreateCommand("SELECT control.ensure_backup_v2_evidence_partition_set(@owner,@fence);"))
        {
            again.Parameters.AddWithValue("owner", lease.Owner.Value); again.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            Assert.Equal(0, await again.ExecuteScalarAsync());
        }
        await using (var check = database.DataSource.CreateCommand("""
            SELECT count(*) FROM system.partition_registry registry JOIN pg_class child
              ON child.oid=to_regclass(format('%I.%I',registry.partition_schema,registry.partition_name))
            JOIN pg_inherits inheritance ON inheritance.inhrelid=child.oid
            WHERE registry.parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample')
              AND registry.parent_schema='telemetry' AND registry.lifecycle_state='attached'
              AND registry.range_end=registry.range_start+interval '1 day'
              AND inheritance.inhparent=to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table))
              AND pg_get_userbyid(child.relowner)='sqlobserver_migrator'
              AND NOT has_table_privilege('sqlobserver_collector',child.oid,'INSERT')
              AND NOT has_table_privilege('sqlobserver_server',child.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_auditor',child.oid,'SELECT')
              AND NOT EXISTS(SELECT 1 FROM aclexplode(coalesce(child.relacl,acldefault('r',child.relowner))) acl WHERE acl.grantee=0);
            """))
            Assert.Equal(9L, await check.ExecuteScalarAsync());
        var stale = new WorkerLeaseIdentity(lease.Key, lease.Owner, new(lease.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.EnsureM10PartitionSetAsync(stale, Timeout, CancellationToken.None))).SqlState);
        await using (var bare = collector.CreateCommand("SELECT control.ensure_backup_v2_evidence_partitions(current_date);"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await bare.ExecuteScalarAsync())).SqlState);
        await ExecuteAsync(database, "UPDATE system.partition_registry SET range_end=range_end+interval '1 hour' WHERE parent_table='backup_v2_sample' AND range_start=current_date::timestamptz;");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.EnsureM10PartitionSetAsync(lease, Timeout, CancellationToken.None))).SqlState);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using (var denied = server.CreateCommand("SELECT system.update_m10_retention_policy('m9_backup_evidence',false,NULL,3,1,'operator','Pause backup evidence');"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteScalarAsync())).SqlState);
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using var authorized = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.role','SecurityAdministrator',true),set_config('sqlobserver.authorization_scope','global',true);
            SELECT system.update_m10_retention_policy('m9_backup_evidence',false,NULL,3,1,'operator','Pause backup evidence');
            """, connection, transaction);
        await authorized.ExecuteNonQueryAsync(); await transaction.CommitAsync();
        await using var defaults = database.DataSource.CreateCommand("SELECT NOT enabled AND policy_revision=2 FROM system.retention_policy WHERE data_class='m9_backup_evidence';");
        Assert.True((bool)(await defaults.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task RetentionKeepsTheWholeGraphReadableDuringGraceAndAtomicallyDropsItAfterEveryHoldClears()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity catalog = await AcquireAsync(collector, "collector/catalog/reconcile");
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        await maintenance.EnsureM10PartitionSetAsync(catalog, Timeout, CancellationToken.None);
        await SeedOldGraphAsync(database);
        WorkerLeaseIdentity retention = await AcquireAsync(collector, "retention/maintenance");
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal("recovery_attestation_missing", await ReasonAsync(database));
        await ExecuteAsync(database, """
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'fixture','restored-fixture',clock_timestamp()+interval '2 hours',sha256(convert_to('fixture','UTF8')));
            CREATE TABLE public.backup_external_hold(instance_id uuid,target_revision bigint,received_at timestamptz,run_id uuid,
              database_ordinal smallint,kind smallint,copy_only boolean,
              FOREIGN KEY(instance_id,target_revision,received_at,run_id,database_ordinal,kind,copy_only)
                REFERENCES telemetry.backup_v2_sample(instance_id,target_revision,received_at,run_id,database_ordinal,kind,copy_only));
            """);
        Assert.Equal("dependency_pending", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, """
            DROP TABLE public.backup_external_hold;
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            VALUES(gen_random_uuid(),'telemetry','backup_v2_sample','backup_v2_sample_old_test',gen_random_uuid(),clock_timestamp()+interval '1 hour');
            """);
        Assert.Equal("reader_lease_active", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, """
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp();
            INSERT INTO system.retention_backfill_state(parent_schema,parent_table,partition_name,range_start,range_end,state)
            VALUES('telemetry','backup_v2_database','backup_v2_database_old_test',(current_date-40)::timestamptz,(current_date-39)::timestamptz,'pending');
            """);
        Assert.Equal("backfill_incomplete", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, """
            UPDATE system.retention_backfill_state SET state='complete';
            UPDATE system.retention_policy SET minimum_partitions_to_keep=4,policy_revision=policy_revision+1 WHERE data_class='m9_backup_evidence';
            """);
        Assert.Equal("minimum_partition_floor", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, "UPDATE system.retention_policy SET minimum_partitions_to_keep=3,policy_revision=policy_revision+1 WHERE data_class='m9_backup_evidence';");
        Assert.Equal("eligible", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal((1L, 1L, 1L, 3L), await CountsAsync(database));
        Assert.Equal("grace_in_progress", await ReasonAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, "UPDATE system.retention_policy SET policy_revision=policy_revision+1,retain_for=interval '365 days' WHERE data_class='m9_backup_evidence';");
        Assert.Equal(SystemRetentionStepOutcome.Cancelled, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal((1L, 1L, 1L, 3L), await CountsAsync(database));
        await ExecuteAsync(database, "UPDATE system.retention_policy SET policy_revision=policy_revision+1,retain_for=interval '30 days' WHERE data_class='m9_backup_evidence';");
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, """
            UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 second' WHERE data_class='m9_backup_evidence' AND state='attached_grace';
            -- Fixture-only removal simulates recovery evidence becoming unavailable;
            -- production attestations are immutable and cannot be edited in place.
            TRUNCATE system.recovery_attestation;
            """);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal((1L, 1L, 1L, 3L), await CountsAsync(database));
        await ExecuteAsync(database, """
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'fixture','renewed-restored-fixture',clock_timestamp()+interval '1 hour',sha256(convert_to('renewed-fixture','UTF8')));
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            VALUES(gen_random_uuid(),'telemetry','backup_v2_database','backup_v2_database_old_test',gen_random_uuid(),clock_timestamp()+interval '1 hour');
            """);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await ExecuteAsync(database, """
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp();
            CREATE VIEW public.backup_drop_failure AS SELECT * FROM telemetry.backup_v2_database_old_test;
            """);
        Assert.Equal(SystemRetentionStepOutcome.Retry, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        // The final-parent failure rolls back the earlier child drop and detach.
        Assert.Equal((1L, 1L, 1L, 3L), await CountsAsync(database));
        await ExecuteAsync(database, """
            DROP VIEW public.backup_drop_failure;
            -- Append a fixture retry whose delay has elapsed; never rewrite the ledger.
            INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at)
            SELECT execution_id,'2BP01','Fixture elapsed retry',clock_timestamp()-interval '1 second'
            FROM system.retention_execution WHERE data_class='m9_backup_evidence' AND state='attached_grace';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Dropped, await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal((0L, 0L, 0L, 0L), await CountsAsync(database));
        await using (var final = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM system.partition_registry WHERE parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample') AND lifecycle_state='dropped'),
              (SELECT count(*) FROM pg_constraint WHERE conrelid IN ('telemetry.backup_v2_database'::regclass,'telemetry.backup_v2_sample'::regclass) AND contype='f' AND convalidated),
              (SELECT count(*) FROM audit.activity WHERE action_name='retention.partition.drop' AND outcome='succeeded' AND safe_details->>'dataClass'='m9_backup_evidence');
            """))
        {
            await using NpgsqlDataReader row = await final.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.Equal(3L, row.GetInt64(0)); Assert.True(row.GetInt64(1)>=3); Assert.Equal(1L, row.GetInt64(2));
        }
        var stale = new WorkerLeaseIdentity(retention.Key, retention.Owner, new(retention.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.RunSystemRetentionStepAsync(stale, Timeout, CancellationToken.None))).SqlState);
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(new(
            MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures, string.Join(", ", result.Results.Where(item => item.Outcome == MigrationOutcome.Failed)
            .Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(NpgsqlDataSource source, string key)
    {
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(source).AcquireAsync(new(new(key), new(Guid.NewGuid()),
            new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task SeedOldGraphAsync(RepositoryTestDatabase database)
    {
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Backup retention fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            SET ROLE sqlobserver_migrator;
            DO $parts$ DECLARE parent_name text; BEGIN
              FOREACH parent_name IN ARRAY ARRAY['backup_v2_scan','backup_v2_database','backup_v2_sample'] LOOP
                EXECUTE format('CREATE TABLE telemetry.%I PARTITION OF telemetry.%I FOR VALUES FROM (%L) TO (%L)',
                  parent_name||'_old_test',parent_name,(current_date-40)::timestamptz,(current_date-39)::timestamptz);
                INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
                  VALUES('telemetry',parent_name,'telemetry',parent_name||'_old_test','day',(current_date-40)::timestamptz,(current_date-39)::timestamptz);
              END LOOP;
            END $parts$;
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,
              schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'backups.status',1,1,1,1,'fixture/backup-retention',@owner,1,decode(repeat('aa',32),'hex'),
              (current_date-40)::timestamptz,(current_date-40)::timestamptz);
            INSERT INTO telemetry.backup_v2_scan(instance_id,target_revision,run_id,received_at,source_observed_at,source_zone_id,
              history_lower_bound_local,coverage,source_rows_read,database_count,item_count,rejected_items,rejected_bytes)
            VALUES(@target,1,@run,(current_date-40)::timestamptz,(current_date-40)::timestamptz,'UTC',
              (current_date-75)::timestamp,1,2,1,2,0,0);
            INSERT INTO telemetry.backup_v2_database(instance_id,target_revision,received_at,run_id,database_ordinal,database_fingerprint,
              family_fingerprint,recovery_fork_fingerprint,recovery_model,catalog_database_state,scope,unclassified_backup_seen,coverage)
            VALUES(@target,1,(current_date-40)::timestamptz,@run,1,decode(repeat('dd',32),'hex'),decode(repeat('ff',32),'hex'),
              decode(repeat('aa',32),'hex'),2,'ONLINE',1,false,1);
            INSERT INTO telemetry.backup_v2_sample(instance_id,target_revision,received_at,run_id,database_ordinal,kind,copy_only,
              source_local_start,source_local_finish,recorded_start_offset_minutes,size_bytes,has_checksum,is_damaged,metadata_complete,
              first_recovery_fork_fingerprint,last_recovery_fork_fingerprint,time_state)
            VALUES(@target,1,(current_date-40)::timestamptz,@run,1,1,false,(current_date-40)::timestamp,
              (current_date-40)::timestamp+interval '1 hour',0,42,true,false,true,decode(repeat('aa',32),'hex'),decode(repeat('aa',32),'hex'),4);
            RESET ROLE;
            """);
        seed.Parameters.AddWithValue("target", Guid.NewGuid()); seed.Parameters.AddWithValue("run", Guid.NewGuid()); seed.Parameters.AddWithValue("owner", Guid.NewGuid());
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReasonAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT reason FROM system.preview_m10_retention(clock_timestamp(),NULL) WHERE data_class='m9_backup_evidence' AND range_start=(current_date-40)::timestamptz;");
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<(long Scans, long Databases, long Samples, long Attached)> CountsAsync(RepositoryTestDatabase database)
    {
        await using var read = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.backup_v2_scan),(SELECT count(*) FROM telemetry.backup_v2_database),
              (SELECT count(*) FROM telemetry.backup_v2_sample),
              (SELECT count(*) FROM system.partition_registry WHERE parent_table IN ('backup_v2_scan','backup_v2_database','backup_v2_sample') AND lifecycle_state='attached' AND range_start=(current_date-40)::timestamptz);
            """);
        await using NpgsqlDataReader row = await read.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3));
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync();
    }
}
