using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class AvailabilityReplicaEvidenceRetentionTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly string[] Parents = ["availability_replica_evidence", "availability_database_replica_evidence"];

    [Fact]
    public async Task UpgradePreservesV1PolicyAndCreatesBoundedPrivateRegisteredPartitionsUnderTheCatalogLease()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var migrationTimeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult prefix = await migrations.ApplyPendingAsync(new MigrationApplyRequest(265,
            migrationTimeout), CancellationToken.None);
        Assert.False(prefix.HasFailures);
        await using (var configure = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET enabled=true,retain_for=interval '365 days',
              policy_revision=7,updated_by='operator-fixture' WHERE data_class='m9_ag_replicas';
            """))
            await configure.ExecuteNonQueryAsync();
        MigrationBatchResult upgraded = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1,
            migrationTimeout), CancellationToken.None);
        Assert.False(upgraded.HasFailures);
        Assert.Equal(266, Assert.Single(upgraded.Results).Migration.Number.Value);
        await using (var old = database.DataSource.CreateCommand("""
            SELECT parent_table::text,retain_for,policy_revision,updated_by
            FROM system.retention_policy WHERE data_class='m9_ag_replicas';
            """))
        {
            await using NpgsqlDataReader row = await old.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal("availability_group_replica_snapshot", row.GetString(0));
            Assert.Equal(TimeSpan.FromDays(365), row.GetFieldValue<TimeSpan>(1));
            Assert.Equal(7L, row.GetInt64(2)); Assert.Equal("operator-fixture", row.GetString(3));
        }
        await using (var defaults = database.DataSource.CreateCommand("""
            SELECT count(*) FROM system.retention_policy
            WHERE data_class IN ('m9_ag_replica_evidence','m9_ag_database_evidence')
              AND enabled AND retain_for=interval '30 days' AND partition_granularity='day'
              AND minimum_partitions_to_keep=3 AND policy_revision=1;
            """))
            Assert.Equal(2L, await defaults.ExecuteScalarAsync());

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity catalog = await AcquireAsync(collector, "collector/catalog/reconcile");
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        Assert.True(await maintenance.EnsureM10PartitionSetAsync(catalog, Timeout, CancellationToken.None) >= 6);
        await using (var again = collector.CreateCommand("""
            SELECT control.ensure_availability_evidence_partition_set(@owner,@fence);
            """))
        {
            again.Parameters.AddWithValue("owner", catalog.Owner.Value);
            again.Parameters.AddWithValue("fence", catalog.FencingToken.Value);
            Assert.Equal(0, await again.ExecuteScalarAsync());
        }
        await using (var registered = database.DataSource.CreateCommand("""
            SELECT count(*) FROM system.partition_registry AS registry
            JOIN pg_class AS child ON child.oid=to_regclass(format('%I.%I',registry.partition_schema,registry.partition_name))
            JOIN pg_inherits AS inheritance ON inheritance.inhrelid=child.oid
            WHERE registry.parent_table IN ('availability_replica_evidence','availability_database_replica_evidence')
              AND registry.parent_schema='telemetry' AND registry.partition_granularity='day'
              AND registry.lifecycle_state='attached' AND registry.range_end=registry.range_start+interval '1 day'
              AND registry.range_start BETWEEN current_date-1 AND current_date+1
              AND inheritance.inhparent=to_regclass(format('%I.%I',registry.parent_schema,registry.parent_table))
              AND pg_get_userbyid(child.relowner)='sqlobserver_migrator'
              AND NOT has_table_privilege('sqlobserver_collector',child.oid,'INSERT')
              AND NOT has_table_privilege('sqlobserver_server',child.oid,'SELECT')
              AND NOT has_table_privilege('sqlobserver_auditor',child.oid,'SELECT')
              AND NOT EXISTS (SELECT 1 FROM aclexplode(coalesce(child.relacl,acldefault('r',child.relowner))) AS acl
                              WHERE acl.grantee=0);
            """))
            Assert.Equal(6L, await registered.ExecuteScalarAsync());
        await using (var bare = collector.CreateCommand("SELECT control.ensure_availability_evidence_partitions(current_date);"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await bare.ExecuteScalarAsync())).SqlState);
        await using (var oldAnchor = database.DataSource.CreateCommand("SELECT control.ensure_availability_evidence_partitions(current_date-1);"))
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await oldAnchor.ExecuteScalarAsync())).SqlState);
        var stale = new WorkerLeaseIdentity(catalog.Key, catalog.Owner, new FencingToken(catalog.FencingToken.Value+1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.EnsureM10PartitionSetAsync(stale, Timeout, CancellationToken.None))).SqlState);
        await using (var corrupt = database.DataSource.CreateCommand("""
            UPDATE system.partition_registry SET range_end=range_end+interval '1 hour'
            WHERE parent_table='availability_replica_evidence' AND range_start=current_date::timestamptz;
            """))
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.EnsureM10PartitionSetAsync(catalog, Timeout, CancellationToken.None))).SqlState);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using (var denied = server.CreateCommand("""
            SELECT system.update_m10_retention_policy('m9_ag_database_evidence',false,NULL,3,1,'operator','Pause AG evidence');
            """))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await denied.ExecuteScalarAsync())).SqlState);
        await using NpgsqlConnection authorized = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await authorized.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
              set_config('sqlobserver.authorization_scope','global',true);
            SELECT system.update_m10_retention_policy('m9_ag_database_evidence',false,NULL,3,1,'operator','Pause AG evidence');
            """, authorized, transaction))
            await update.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        await using (var changed = database.DataSource.CreateCommand("""
            SELECT NOT enabled AND retain_for IS NULL AND policy_revision=2
            FROM system.retention_policy WHERE data_class='m9_ag_database_evidence';
            """))
            Assert.True((bool)(await changed.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WrongPartitionBoundsRejectTheWholeCreationTransaction()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        await using (var occupied = database.DataSource.CreateCommand("""
            SET ROLE sqlobserver_migrator;
            DO $partition$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.%I PARTITION OF telemetry.availability_database_replica_evidence FOR VALUES FROM (%L) TO (%L)',
                'availability_database_replica_evidence_p'||to_char(current_date,'YYYYMMDD'),
                (current_date-5)::timestamptz,(current_date-4)::timestamptz);
            END $partition$;
            RESET ROLE;
            """))
            await occupied.ExecuteNonQueryAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity catalog = await AcquireAsync(collector, "collector/catalog/reconcile");
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await new PostgreSqlPartitionMaintenancePort(collector).EnsureM10PartitionSetAsync(catalog,
                Timeout, CancellationToken.None))).SqlState);
        await using var rollback = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM system.partition_registry
                     WHERE parent_table IN ('availability_replica_evidence','availability_database_replica_evidence')),
                   (SELECT count(*) FROM pg_inherits WHERE inhparent='telemetry.availability_replica_evidence'::regclass);
            """);
        await using NpgsqlDataReader row = await rollback.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal(0L, row.GetInt64(0)); Assert.Equal(0L, row.GetInt64(1));
    }

    [Fact]
    public async Task PopulatedEvidenceUsesRecoveryReaderBackfillGraceAndFencedDropGates()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity catalog = await AcquireAsync(collector, "collector/catalog/reconcile");
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        await maintenance.EnsureM10PartitionSetAsync(catalog, Timeout, CancellationToken.None);
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@target::text,'AG retention fixture','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            SET ROLE sqlobserver_migrator;
            DO $partitions$ DECLARE parent_name text; BEGIN
              FOREACH parent_name IN ARRAY ARRAY['availability_replica_evidence','availability_database_replica_evidence'] LOOP
                EXECUTE format('CREATE TABLE telemetry.%I PARTITION OF telemetry.%I FOR VALUES FROM (%L) TO (%L)',
                  parent_name||'_old_test',parent_name,(current_date-40)::timestamptz,(current_date-39)::timestamptz);
                INSERT INTO system.partition_registry
                 (parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
                VALUES ('telemetry',parent_name,'telemetry',parent_name||'_old_test','day',
                  (current_date-40)::timestamptz,(current_date-39)::timestamptz);
              END LOOP;
            END $partitions$;
            INSERT INTO telemetry.collection_run
             (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,
              schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'availability-groups.health',1,1,1,1,'fixture/ag-retention',@owner,1,
              decode(repeat('aa',32),'hex'),(current_date-40)::timestamptz,(current_date-40)::timestamptz);
            INSERT INTO telemetry.availability_replica_evidence
             (instance_id,run_id,target_revision,observed_at,group_fingerprint,replica_fingerprint,
              observer_replica_fingerprint,replica_is_local,visibility_scope,state_observed,role,operational_state,connected_state)
            VALUES (@target,@run,1,(current_date-40)::timestamptz,decode(repeat('aa',32),'hex'),
              decode(repeat('11',32),'hex'),decode(repeat('11',32),'hex'),true,1,true,'PRIMARY','ONLINE','CONNECTED');
            INSERT INTO telemetry.availability_database_replica_evidence
             (instance_id,run_id,target_revision,observed_at,group_fingerprint,replica_fingerprint,database_fingerprint,
              observer_replica_fingerprint,replica_is_local,visibility_scope,state_observed,
              synchronization_state,synchronization_health,database_state,is_suspended,log_send_queue_kb)
            VALUES (@target,@run,1,(current_date-40)::timestamptz,decode(repeat('aa',32),'hex'),
              decode(repeat('11',32),'hex'),decode(repeat('dd',32),'hex'),decode(repeat('11',32),'hex'),
              true,1,true,'SYNCHRONIZED','HEALTHY','ONLINE',false,0);
            RESET ROLE;
            """))
        {
            seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("owner", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }
        WorkerLeaseIdentity retention = await AcquireAsync(collector, "retention/maintenance");
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal(["recovery_attestation_missing", "recovery_attestation_missing"], await ReasonsAsync(database));
        await using (var holds = database.DataSource.CreateCommand("""
            INSERT INTO system.recovery_attestation
             (attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES (gen_random_uuid(),clock_timestamp(),'fixture','restored-fixture',
              clock_timestamp()+interval '2 hours',sha256(convert_to('fixture','UTF8')));
            INSERT INTO system.partition_reader_lease
             (lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),
              clock_timestamp()+interval '1 hour' FROM system.partition_registry
            WHERE parent_table IN ('availability_replica_evidence','availability_database_replica_evidence')
              AND range_start=(current_date-40)::timestamptz;
            """))
            await holds.ExecuteNonQueryAsync();
        Assert.Equal(["reader_lease_active", "reader_lease_active"], await ReasonsAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var backfill = database.DataSource.CreateCommand("""
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp();
            INSERT INTO system.retention_backfill_state
             (parent_schema,parent_table,partition_name,range_start,range_end,state)
            SELECT parent_schema,parent_table,partition_name,range_start,range_end,'pending'
            FROM system.partition_registry WHERE partition_name LIKE '%_old_test';
            """))
            await backfill.ExecuteNonQueryAsync();
        Assert.Equal(["backfill_incomplete", "backfill_incomplete"], await ReasonsAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var ready = database.DataSource.CreateCommand("UPDATE system.retention_backfill_state SET state='complete';"))
            await ready.ExecuteNonQueryAsync();
        await using (var floor = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET minimum_partitions_to_keep=4,policy_revision=policy_revision+1
            WHERE data_class IN ('m9_ag_replica_evidence','m9_ag_database_evidence');
            """))
            await floor.ExecuteNonQueryAsync();
        Assert.Equal(["minimum_partition_floor", "minimum_partition_floor"], await ReasonsAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var floor = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET minimum_partitions_to_keep=3,policy_revision=policy_revision+1
            WHERE data_class IN ('m9_ag_replica_evidence','m9_ag_database_evidence');
            """))
            await floor.ExecuteNonQueryAsync();
        Assert.Equal(["eligible", "eligible"], await ReasonsAsync(database));
        for (int step = 0; step < 2; step++)
            Assert.Equal(SystemRetentionStepOutcome.Detached,
                await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        foreach (string parent in Parents)
        {
            await using var invisible = database.DataSource.CreateCommand($"SELECT count(*) FROM telemetry.{parent};");
            Assert.Equal(0L, await invisible.ExecuteScalarAsync());
            await using var preserved = database.DataSource.CreateCommand($"SELECT count(*) FROM telemetry.{parent}_old_test;");
            Assert.Equal(1L, await preserved.ExecuteScalarAsync());
        }
        await using (var pause = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET enabled=false,retain_for=NULL,policy_revision=policy_revision+1
            WHERE data_class='m9_ag_replica_evidence';
            """))
            await pause.ExecuteNonQueryAsync();
        Assert.Equal(SystemRetentionStepOutcome.Reattached,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var restored = database.DataSource.CreateCommand("SELECT count(*) FROM telemetry.availability_replica_evidence;"))
            Assert.Equal(1L, await restored.ExecuteScalarAsync());
        await using (var resume = database.DataSource.CreateCommand("""
            UPDATE system.retention_policy SET enabled=true,retain_for=interval '30 days',policy_revision=policy_revision+1
            WHERE data_class='m9_ag_replica_evidence';
            """))
            await resume.ExecuteNonQueryAsync();
        Assert.Equal(SystemRetentionStepOutcome.Detached,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var readers = database.DataSource.CreateCommand("""
            UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 minute'
            WHERE data_class IN ('m9_ag_replica_evidence','m9_ag_database_evidence');
            INSERT INTO system.partition_reader_lease
             (lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),
              clock_timestamp()+interval '1 hour' FROM system.partition_registry WHERE lifecycle_state='detached';
            """))
            await readers.ExecuteNonQueryAsync();
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using (var release = database.DataSource.CreateCommand("""
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp() WHERE released_at IS NULL;
            """))
            await release.ExecuteNonQueryAsync();
        var stale = new WorkerLeaseIdentity(retention.Key, retention.Owner, new FencingToken(retention.FencingToken.Value+1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await maintenance.RunSystemRetentionStepAsync(stale, Timeout, CancellationToken.None))).SqlState);
        for (int step = 0; step < 2; step++)
            Assert.Equal(SystemRetentionStepOutcome.Dropped,
                await maintenance.RunSystemRetentionStepAsync(retention, Timeout, CancellationToken.None));
        await using var final = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM system.partition_registry
                    WHERE parent_table IN ('availability_replica_evidence','availability_database_replica_evidence')
                      AND lifecycle_state='dropped'),
                   (SELECT count(*) FROM audit.activity WHERE action_name IN
                     ('retention.partition.detach','retention.partition.drop') AND outcome='succeeded'),
                   to_regclass('telemetry.availability_replica_evidence_old_test') IS NULL,
                   to_regclass('telemetry.availability_database_replica_evidence_old_test') IS NULL;
            """);
        await using NpgsqlDataReader evidence = await final.ExecuteReaderAsync();
        Assert.True(await evidence.ReadAsync());
        Assert.Equal(2L, evidence.GetInt64(0)); Assert.Equal(5L, evidence.GetInt64(1));
        Assert.True(evidence.GetBoolean(2)); Assert.True(evidence.GetBoolean(3));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrated.HasFailures, string.Join(", ", migrated.Results
            .Where(static item => item.Outcome == MigrationOutcome.Failed)
            .Select(static item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(NpgsqlDataSource collector, string key)
    {
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey(key), new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task<string[]> ReasonsAsync(RepositoryTestDatabase database)
    {
        await using var preview = database.DataSource.CreateCommand("""
            SELECT reason FROM system.preview_m10_retention(clock_timestamp(),NULL)
            WHERE data_class IN ('m9_ag_replica_evidence','m9_ag_database_evidence')
              AND range_start=(current_date-40)::timestamptz ORDER BY data_class;
            """);
        var reasons = new List<string>();
        await using NpgsqlDataReader row = await preview.ExecuteReaderAsync();
        while (await row.ReadAsync()) reasons.Add(row.GetString(0));
        return reasons.ToArray();
    }
}
