using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunRollbackSealTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task OriginalCopyCleanupRefusesTheDefaultUnsealedInstallation()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        await using (NpgsqlConnection policyReader = await database.DataSource.OpenConnectionAsync())
        await using (var policy = new NpgsqlCommand("""
            SELECT enabled,retain_for IS NULL,minimum_partitions_to_keep,
                   policy_revision
            FROM system.retention_policy
            WHERE data_class='collection_run_original_copy'
              AND parent_schema='telemetry'
              AND parent_table='collection_run'
              AND partition_granularity='none';
            """, policyReader))
        await using (NpgsqlDataReader row = await policy.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.False(row.GetBoolean(0));
            Assert.True(row.GetBoolean(1));
            Assert.Equal(1, row.GetInt32(2));
            Assert.Equal(1L, row.GetInt64(3));
        }
        await using (NpgsqlConnection policyAdmin = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction premature = await policyAdmin.BeginTransactionAsync())
        {
            await SetMigratorAsync(policyAdmin, premature);
            await using var enable = new NpgsqlCommand("""
                UPDATE system.retention_policy
                SET enabled=true,retain_for=interval '30 days',
                    policy_revision=policy_revision+1
                WHERE data_class='collection_run_original_copy';
                """, policyAdmin, premature);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => enable.ExecuteNonQueryAsync())).SqlState);
            await premature.RollbackAsync();
        }
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey("retention/maintenance"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        Assert.Equal(OriginalCopyRetentionStepOutcome.Idle,
            await new PostgreSqlPartitionMaintenancePort(collectorSource)
                .RunOriginalCopyRetentionStepAsync(lease,
                    new RepositoryCallTimeout(TimeSpan.FromSeconds(15)),
                    CancellationToken.None));
        await using (NpgsqlConnection collector = await collectorSource.OpenConnectionAsync())
        await using (var grant = new NpgsqlCommand("""
            SELECT has_function_privilege(current_user,
              'control.retire_collection_run_original_copies(uuid,timestamptz,uuid,bigint,integer)',
              'EXECUTE');
            """, collector))
            Assert.False((bool)(await grant.ExecuteScalarAsync())!);
        Guid target = Guid.NewGuid();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
        await SetMigratorAsync(admin, transaction);
        await using var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target::text,true);",
            admin, transaction);
        scope.Parameters.AddWithValue("target", target);
        await scope.ExecuteScalarAsync();
        await using var cleanup = new NpgsqlCommand("""
            SELECT control.retire_collection_run_original_copies(
              @target,@cutoff,@owner,@fence,10);
            """, admin, transaction);
        cleanup.Parameters.AddWithValue("target", target);
        cleanup.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow.AddDays(-40));
        cleanup.Parameters.AddWithValue("owner", lease.Owner.Value);
        cleanup.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => cleanup.ExecuteScalarAsync())).SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SealRequiresAllRoutesAndStopsOriginalMirroringPermanently()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();

        await using (NpgsqlTransaction denied = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, denied);
            await using var command = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, denied);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteScalarAsync())).SqlState);
            await denied.RollbackAsync();
        }

        var children = new List<string>();
        await using (var inventory = new NpgsqlCommand(
            "SELECT child_relation::text FROM control.collection_run_identity_fk_targets() ORDER BY child_relation::oid;",
            admin))
        await using (NpgsqlDataReader rows = await inventory.ExecuteReaderAsync())
            while (await rows.ReadAsync()) children.Add(rows.GetString(0));
        Assert.Equal(20, children.Count);
        await using (NpgsqlTransaction stage = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, stage);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.stage_collection_run_identity_fk(@child::regclass,10000);", admin, stage);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await stage.CommitAsync();
        }
        await using (NpgsqlTransaction validate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, validate);
            foreach (string child in children)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT control.validate_collection_run_identity_fk(@child::regclass);", admin, validate);
                command.Parameters.AddWithValue("child", child);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            await validate.CommitAsync();
        }

        DateTimeOffset sealedAt;
        await using (NpgsqlTransaction activate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, activate);
            await using (var cutover = new NpgsqlCommand(
                "SELECT control.cutover_collection_run_identity_fks(10000);", admin, activate))
                Assert.Equal(20, await cutover.ExecuteScalarAsync());
            foreach (string route in new[]
            {
                "activity", "health", "m9", "m10", "overview", "ple",
                "query_catalog", "scheduler", "sql_volume", "wait", "writer"
            })
            {
                // This fixed test inventory is deliberately not supplied by a
                // caller; the production seal verifies its own route list.
                await using var command = new NpgsqlCommand(
                    $"SELECT control.set_collection_run_v2_{route}_read_route(true,10000);",
                    admin, activate);
                if (route == "writer") command.CommandText =
                    "SELECT control.set_collection_run_v2_writer_route(true,10000);";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            await using var seal = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, activate);
            sealedAt = new DateTimeOffset((DateTime)(await seal.ExecuteScalarAsync())!);
            await activate.CommitAsync();
        }
        await using (NpgsqlTransaction policyUpdate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, policyUpdate);
            await using (var context = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true);
                SELECT set_config('sqlobserver.authorization_scope','global',true);
                """, admin, policyUpdate))
                await context.ExecuteNonQueryAsync();
            await using (var tooShort = new NpgsqlCommand("""
                SELECT system.update_m10_retention_policy(
                  'collection_run_original_copy',true,interval '29 days',1,1,
                  'test','original-copy retention');
                """, admin, policyUpdate))
            {
                Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
                    () => tooShort.ExecuteScalarAsync())).SqlState);
            }
            await policyUpdate.RollbackAsync();
        }
        await using (NpgsqlTransaction policyUpdate = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, policyUpdate);
            await using (var context = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true);
                SELECT set_config('sqlobserver.authorization_scope','global',true);
                """, admin, policyUpdate))
                await context.ExecuteNonQueryAsync();
            await using var enable = new NpgsqlCommand("""
                SELECT system.update_m10_retention_policy(
                  'collection_run_original_copy',true,interval '30 days',1,1,
                  'test','original-copy retention');
                """, admin, policyUpdate);
            Assert.Equal(2L, await enable.ExecuteScalarAsync());
            await policyUpdate.CommitAsync();
        }

        Guid target = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,
              connect_timeout,authentication_mode,transport_security_mode,
              lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'sealed-run target','sql01',1433,
             interval '5 seconds','windows_integrated_service_identity',
             'mandatory_validated','active',1,now(),now(),now());
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            insert.Parameters.AddWithValue("target", target);
            await insert.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collectorSource = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collectorSource);
        var leases = new PostgreSqlWorkerLeasePort(collectorSource);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None)).Items, item =>
                item.TargetId.Value == target && item.CollectorId.Value == "engine.core");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.core/{target:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquired.Status);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                work, run, lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
                CancellationToken.None)).Status);
        var summary = new CollectorRunSummary(
            run, work.TargetId, work.TargetRevision, work.CollectorId,
            work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.TransientFailure,
            CollectorRunReason.TransientTargetFailure,
            TimeSpan.FromMilliseconds(1), 1,
            new CollectorRunAccounting(0, 0, 0, 0),
            CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(
            work, summary, CollectorPayload.Empty,
            new CollectorCircuitSnapshot(CollectorCircuitState.Closed, 1,
                work.RepositoryTimeUtc), lease,
            new RepositoryCallTimeout(TimeSpan.FromSeconds(10)));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        await using (var counts = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run);
            """, admin))
        {
            counts.Parameters.AddWithValue("run", run.Value);
            await using NpgsqlDataReader row = await counts.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
        }
        await using (var outcome = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;", admin))
        {
            outcome.Parameters.AddWithValue("run", run.Value);
            Assert.Equal(1L, await outcome.ExecuteScalarAsync());
        }

        await using (var due = new NpgsqlCommand("""
            UPDATE control.collector_schedule
            SET next_due_at=statement_timestamp()-interval '1 second'
            WHERE instance_id=@target AND collector_id='engine.core';
            """, admin))
        {
            due.Parameters.AddWithValue("target", target);
            await due.ExecuteNonQueryAsync();
        }
        CollectorDueWorkItem pending = Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None)).Items, item =>
                item.TargetId.Value == target && item.CollectorId.Value == "engine.core");
        var invalidatedRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(
                pending, invalidatedRun, lease,
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
                CancellationToken.None)).Status);
        await using (var revise = new NpgsqlCommand("""
            UPDATE control.observation_target
            SET revision=2,host_name='sql02',updated_at=statement_timestamp()
            WHERE instance_id=@target;
            """, admin))
        {
            revise.Parameters.AddWithValue("target", target);
            await revise.ExecuteNonQueryAsync();
        }
        await using (var invalidated = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome
                    WHERE run_id=@run AND outcome='permanent_failure'
                      AND reason_code='target_revision_changed'),
                   (SELECT count(*) FROM telemetry.visibility_gap
                    WHERE run_id=@run AND reason_code='target_revision_changed'),
                   (SELECT active_run_id FROM control.collector_schedule
                    WHERE instance_id=@target AND collector_id='engine.core');
            """, admin))
        {
            invalidated.Parameters.AddWithValue("run", invalidatedRun.Value);
            invalidated.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await invalidated.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
            Assert.Equal(1L, row.GetInt64(3));
            Assert.True(row.IsDBNull(4));
        }

        Guid queryRun = Guid.NewGuid();
        await using (NpgsqlTransaction seed = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, seed);
            await using var insert = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT telemetry.insert_collection_run_runtime(
                  @run,@target,'queries.performance',1,1,2,1,
                  'test/m7-sealed',@owner,1,decode(repeat('00',32),'hex'),
                  statement_timestamp(),statement_timestamp());
                """, admin, seed);
            insert.Parameters.AddWithValue("run", queryRun);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }
        await using (var header = new NpgsqlCommand("""
            INSERT INTO events.query_performance_run
              (collection_run_id,instance_id,target_revision,window_start,
               window_end,source,source_state,coverage,freshness,truncated,
               completion_digest)
            VALUES(@run,@target,2,statement_timestamp()-interval '1 hour',
                   statement_timestamp(),'query_store','read_write',
                   'complete',true,false,decode(repeat('00',32),'hex'));
            """, admin))
        {
            header.Parameters.AddWithValue("run", queryRun);
            header.Parameters.AddWithValue("target", target);
            await header.ExecuteNonQueryAsync();
        }
        LeaseAcquisitionResult retentionAcquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey("retention/maintenance"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(10))),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, retentionAcquired.Status);
        WorkerLeaseIdentity retentionLease = Assert.IsType<WorkerLease>(retentionAcquired.Lease).Identity;
        await using (NpgsqlTransaction retention = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, retention);
            await using var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,true);",
                admin, retention);
            scope.Parameters.AddWithValue("target", target);
            await scope.ExecuteScalarAsync();
            await using var retire = new NpgsqlCommand("""
                SELECT system.retire_query_performance_run(@target,@run,@owner,@fence,4096);
                """, admin, retention);
            retire.Parameters.AddWithValue("target", target);
            retire.Parameters.AddWithValue("run", queryRun);
            retire.Parameters.AddWithValue("owner", retentionLease.Owner.Value);
            retire.Parameters.AddWithValue("fence", retentionLease.FencingToken.Value);
            Assert.Equal("held", await retire.ExecuteScalarAsync());
            await retention.RollbackAsync();
        }

        Guid historicalRun = Guid.NewGuid();
        DateTimeOffset historicalStart = DateTimeOffset.UtcNow.AddDays(-60);
        DateTimeOffset cutoff = historicalStart.AddDays(1);
        await using (NpgsqlTransaction seed = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, seed);
            await using var insert = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT telemetry.insert_collection_run_runtime(
                  @run,@target,'engine.core',1,1,2,1,
                  'test/original-copy',@owner,1,decode(repeat('00',32),'hex'),
                  @started,@started);
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT run_id,instance_id,collector_id,collector_version,
                       output_schema_version,target_revision,schedule_revision,
                       work_key,owner_execution_id,fencing_token,request_digest,
                       scheduled_for,started_at
                FROM telemetry.collection_run_v2 WHERE run_id=@run;
                INSERT INTO telemetry.collection_run_outcome
                  (run_id,outcome,reason_code,attempt_count,retry_count,
                   duration_ms,source_row_count,output_item_count,
                   inserted_item_count,duplicate_item_count,rejected_item_count,
                   response_bytes,output_bytes,persisted_bytes,truncated,
                   loss_detected,loss_kind,loss_count_exact,lost_row_count,
                   lost_byte_count,completion_digest,completed_at)
                VALUES(@run,'succeeded','completed',1,0,1,0,0,0,0,0,0,0,0,
                       false,false,'none',true,0,0,
                       decode(repeat('00',32),'hex'),@started+interval '1 minute');
                """, admin, seed);
            insert.Parameters.AddWithValue("run", historicalRun);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("owner", Guid.NewGuid());
            insert.Parameters.AddWithValue("started", historicalStart);
            await insert.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }
        async Task<object?> RetireOriginalAsync(long fence)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await SetMigratorAsync(admin, transaction);
            await using var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,true);",
                admin, transaction);
            scope.Parameters.AddWithValue("target", target);
            await scope.ExecuteScalarAsync();
            await using var command = new NpgsqlCommand("""
                SELECT control.retire_collection_run_original_copies(
                  @target,@cutoff,@owner,@fence,10);
                """, admin, transaction);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("cutoff", cutoff);
            command.Parameters.AddWithValue("owner", retentionLease.Owner.Value);
            command.Parameters.AddWithValue("fence", fence);
            object? result = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return result;
        }
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => RetireOriginalAsync(retentionLease.FencingToken.Value))).SqlState);
        await using (NpgsqlTransaction directDelete = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, directDelete);
            await using var delete = new NpgsqlCommand(
                "DELETE FROM telemetry.collection_run WHERE run_id=@run;",
                admin, directDelete);
            delete.Parameters.AddWithValue("run", historicalRun);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => delete.ExecuteNonQueryAsync())).SqlState);
            await directDelete.RollbackAsync();
        }
        await using (var attestation = new NpgsqlCommand("""
            INSERT INTO system.recovery_attestation
              (attestation_id,attested_at,attested_by,backup_set_reference,
               expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'test','restore-tested',
                   clock_timestamp()+interval '3 days',
                   sha256(convert_to('sealed-copy-test','UTF8')));
            """, admin))
            await attestation.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => RetireOriginalAsync(retentionLease.FencingToken.Value + 1))).SqlState);
        await using (var unexpected = new NpgsqlCommand("""
            CREATE TABLE system.test_original_run_reference
             (run_id uuid REFERENCES telemetry.collection_run(run_id));
            """, admin))
            await unexpected.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => RetireOriginalAsync(retentionLease.FencingToken.Value))).SqlState);
        await using (var removeReference = new NpgsqlCommand(
            "DROP TABLE system.test_original_run_reference;", admin))
            await removeReference.ExecuteNonQueryAsync();
        await using (var unexpectedView = new NpgsqlCommand("""
            CREATE VIEW system.test_original_run_view AS
            SELECT run_id FROM telemetry.collection_run;
            """, admin))
            await unexpectedView.ExecuteNonQueryAsync();
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => RetireOriginalAsync(retentionLease.FencingToken.Value))).SqlState);
        await using (var removeView = new NpgsqlCommand(
            "DROP VIEW system.test_original_run_view;", admin))
            await removeView.ExecuteNonQueryAsync();
        Assert.Equal(1, await RetireOriginalAsync(retentionLease.FencingToken.Value));
        Assert.Equal(0, await RetireOriginalAsync(retentionLease.FencingToken.Value));
        await using (var retired = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run),
                   (SELECT count(*) FROM system.collection_run_original_copy_retirement
                    WHERE instance_id=@target AND row_count=1),
                   (SELECT count(*) FROM audit.activity
                    WHERE action_name='retention.collection_run_original.retire');
            """, admin))
        {
            retired.Parameters.AddWithValue("run", historicalRun);
            retired.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await retired.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
            Assert.Equal(1L, row.GetInt64(3));
            Assert.Equal(1L, row.GetInt64(4));
            Assert.Equal(1L, row.GetInt64(5));
        }
        Guid scheduledRun = Guid.NewGuid();
        await using (NpgsqlTransaction seed = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, seed);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO telemetry.collection_run_v2
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT @new,instance_id,collector_id,collector_version,
                       output_schema_version,target_revision,schedule_revision,
                       work_key,owner_execution_id,fencing_token,request_digest,
                       scheduled_for+interval '1 second',started_at+interval '1 second'
                FROM telemetry.collection_run_v2 WHERE run_id=@old;
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT run_id,instance_id,collector_id,collector_version,
                       output_schema_version,target_revision,schedule_revision,
                       work_key,owner_execution_id,fencing_token,request_digest,
                       scheduled_for,started_at
                FROM telemetry.collection_run_v2 WHERE run_id=@new;
                INSERT INTO telemetry.collection_run_outcome
                  (run_id,outcome,reason_code,attempt_count,retry_count,
                   duration_ms,source_row_count,output_item_count,
                   inserted_item_count,duplicate_item_count,rejected_item_count,
                   response_bytes,output_bytes,persisted_bytes,truncated,
                   loss_detected,loss_kind,loss_count_exact,lost_row_count,
                   lost_byte_count,completion_digest,completed_at)
                SELECT @new,outcome,reason_code,attempt_count,retry_count,
                       duration_ms,source_row_count,output_item_count,
                       inserted_item_count,duplicate_item_count,rejected_item_count,
                       response_bytes,output_bytes,persisted_bytes,truncated,
                       loss_detected,loss_kind,loss_count_exact,lost_row_count,
                       lost_byte_count,completion_digest,
                       completed_at+interval '1 second'
                FROM telemetry.collection_run_outcome WHERE run_id=@old;
                """, admin, seed);
            insert.Parameters.AddWithValue("new", scheduledRun);
            insert.Parameters.AddWithValue("old", historicalRun);
            await insert.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }
        var maintenance = new PostgreSqlPartitionMaintenancePort(collectorSource);
        Assert.Equal(OriginalCopyRetentionStepOutcome.Retired,
            await maintenance.RunOriginalCopyRetentionStepAsync(
                retentionLease,new RepositoryCallTimeout(TimeSpan.FromSeconds(15)),
                CancellationToken.None));
        await using (var scheduled = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity WHERE run_id=@run),
                   (SELECT count(*) FROM system.collection_run_original_copy_retirement
                    WHERE instance_id=@target),
                   (SELECT count(*) FROM system.collection_run_original_copy_blocked);
            """, admin))
        {
            scheduled.Parameters.AddWithValue("run", scheduledRun);
            scheduled.Parameters.AddWithValue("target", target);
            await using NpgsqlDataReader row = await scheduled.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(0L, row.GetInt64(0));
            Assert.Equal(1L, row.GetInt64(1));
            Assert.Equal(1L, row.GetInt64(2));
            Assert.Equal(2L, row.GetInt64(3));
            Assert.Equal(0L, row.GetInt64(4));
        }

        await using (NpgsqlTransaction rollback = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, rollback);
            await using var route = new NpgsqlCommand(
                "UPDATE system.collection_run_v2_activity_read_route SET use_v2=false;", admin, rollback);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => route.ExecuteNonQueryAsync())).SqlState);
            await rollback.RollbackAsync();
        }
        await using (NpgsqlTransaction deleteRoute = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, deleteRoute);
            await using var command = new NpgsqlCommand(
                "DELETE FROM system.collection_run_v2_activity_read_route;", admin, deleteRoute);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync())).SqlState);
            await deleteRoute.RollbackAsync();
        }
        await using (NpgsqlTransaction unseal = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, unseal);
            await using var command = new NpgsqlCommand(
                "UPDATE system.collection_run_original_rollback_seal SET sealed_at=NULL,sealed_by=NULL;",
                admin, unseal);
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
                () => command.ExecuteNonQueryAsync())).SqlState);
            await unseal.RollbackAsync();
        }
        await using (NpgsqlTransaction repeat = await admin.BeginTransactionAsync())
        {
            await SetMigratorAsync(admin, repeat);
            await using var command = new NpgsqlCommand(
                "SELECT control.seal_collection_run_original_rollback(10000);", admin, repeat);
            Assert.Equal(sealedAt, new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync())!));
            await using var lookup = new NpgsqlCommand(
                "SELECT count(*) FROM telemetry.lookup_collection_run_runtime(@run);", admin, repeat);
            lookup.Parameters.AddWithValue("run", run.Value);
            Assert.Equal(1L, await lookup.ExecuteScalarAsync());
            await repeat.CommitAsync();
        }
    }

    private static async Task SetMigratorAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var role = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator;", connection, transaction);
        await role.ExecuteNonQueryAsync();
    }
}
