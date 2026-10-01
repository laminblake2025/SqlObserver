using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M6DeadlockPostgreSqlIntegrationTests
{
    [Fact]
    public async Task DeadlockReceiptRetentionKeepsReadableGraceChecksScopedHoldsAndRestoresDroppedEvidence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var seed = await SeedDeadlockReceiptMonthsAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await DeadlockReceiptRetentionLeaseAsync(collector);
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        Assert.Equal("recovery_attestation_missing", await ReceiptRetentionReasonAsync(database, seed.Old));
        Assert.Equal("checkpoint_active", await ReceiptRetentionReasonAsync(database, seed.Held));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        string originalReceipts = await ReceiptContentsAsync(database);
        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using (RepositoryTestDatabase restored = await backup.RestoreAsync())
        {
            Assert.Equal(2L, await ReceiptCountAsync(restored));
            Assert.Equal(originalReceipts, await ReceiptContentsAsync(restored));
            Assert.Equal("checkpoint_active", await ReceiptRetentionReasonAsync(restored, seed.Held));
        }
        await ExecuteAsync(database, """
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'fixture',@reference,clock_timestamp()+interval '2 hours',@digest);
            """, ("reference", backup.Reference), ("digest", Convert.FromHexString(backup.Digest)));
        await ExecuteAsync(database, """
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            VALUES(gen_random_uuid(),'events','deadlock_read_receipt',@part,gen_random_uuid(),clock_timestamp()+interval '1 hour');
            """, ("part", ReceiptPartition(seed.Old)));
        Assert.Equal("reader_lease_active", await ReceiptRetentionReasonAsync(database, seed.Old));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        await ExecuteAsync(database, """
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp() WHERE parent_table='deadlock_read_receipt';
            INSERT INTO system.retention_backfill_state(parent_schema,parent_table,partition_name,range_start,range_end,state)
            VALUES('events','deadlock_read_receipt',@part,@start,@finish,'pending');
            """, ("part", ReceiptPartition(seed.Old)), ("start", seed.Old), ("finish", seed.Old.AddMonths(1)));
        Assert.Equal("backfill_incomplete", await ReceiptRetentionReasonAsync(database, seed.Old));
        await ExecuteAsync(database, "UPDATE system.retention_backfill_state SET state='complete' WHERE parent_table='deadlock_read_receipt';");
        Assert.Equal("eligible", await ReceiptRetentionReasonAsync(database, seed.Old));
        // Baseline jobs do not consume replay receipts. An overlapping fleet
        // job must not become a blanket hold on this unrelated data class.
        await ExecuteAsync(database, """
            INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,from_utc,to_utc,status)
            VALUES(gen_random_uuid(),'baseline',@target,1,'analytics/derivation',@start,@finish,'queued');
            """, ("target", seed.Target), ("start", seed.Old), ("finish", seed.Old.AddMonths(1)));
        // A different month's latest checkpoint does not hold this month.
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted, await Step());
        Assert.Equal(2L, await ReceiptCountAsync(database));
        Assert.Equal("grace_in_progress", await ReceiptPreviewReasonAsync(database, seed.Old));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        await ExecuteAsync(database, "UPDATE system.retention_policy SET retain_for=interval '365 days',policy_revision=policy_revision+1 WHERE data_class='m6_deadlock_read_receipts';");
        Assert.Equal(SystemRetentionStepOutcome.Cancelled, await Step());
        Assert.Equal(2L, await ReceiptCountAsync(database));
        await ExecuteAsync(database, "UPDATE system.retention_policy SET retain_for=interval '30 days',policy_revision=policy_revision+1 WHERE data_class='m6_deadlock_read_receipts';");
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted, await Step());
        await ElapseReceiptGraceAsync(database);
        await ExecuteAsync(database, "TRUNCATE system.recovery_attestation;");
        Assert.Equal("recovery_attestation_missing", await ReceiptRetentionReasonAsync(database, seed.Old));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        Assert.Equal(2L, await ReceiptCountAsync(database));
        await ExecuteAsync(database, """
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'fixture',@reference,clock_timestamp()+interval '2 hours',@digest);
            """, ("reference", backup.Reference), ("digest", Convert.FromHexString(backup.Digest)));
        // Real SQL reads hold the physical partition, even without an explicit
        // reader-lease row. Cleanup returns idle rather than detaching mid-read.
        await using (NpgsqlConnection reader = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await reader.BeginTransactionAsync())
        {
            await using var read = new NpgsqlCommand("SELECT count(*) FROM events.deadlock_read_receipt WHERE completed_at>=@start AND completed_at<@finish;", reader, transaction);
            read.Parameters.AddWithValue("start", seed.Old); read.Parameters.AddWithValue("finish", seed.Old.AddMonths(1));
            Assert.Equal(1L, await read.ExecuteScalarAsync());
            Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
            await transaction.RollbackAsync();
        }
        await ExecuteAsync(database, $"CREATE VIEW public.deadlock_receipt_drop_hold AS SELECT * FROM events.{ReceiptPartition(seed.Old)};");
        Assert.Equal(SystemRetentionStepOutcome.Retry, await Step());
        Assert.Equal(2L, await ReceiptCountAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        await ExecuteAsync(database, """
            DROP VIEW public.deadlock_receipt_drop_hold;
            INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at)
            SELECT execution_id,'2BP01','Fixture elapsed retry',clock_timestamp()-interval '1 second'
            FROM system.retention_execution WHERE data_class='m6_deadlock_read_receipts' AND state='attached_grace';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Dropped, await Step());
        Assert.Equal(1L, await ReceiptCountAsync(database));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step());
        Assert.Equal("checkpoint_active", await ReceiptPreviewReasonAsync(database, seed.Held));
        await using (RepositoryTestDatabase recovered = await backup.RestoreAsync())
        {
            Assert.Equal(2L, await ReceiptCountAsync(recovered));
            Assert.Equal(originalReceipts, await ReceiptContentsAsync(recovered));
        }
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease, Timeout), default);
        ValueTask<SystemRetentionStepOutcome> Step() => maintenance.RunSystemRetentionStepAsync(lease, Timeout, default);
    }

    [Fact]
    public async Task DeadlockReceiptRetentionPolicyAndFinalFenceCannotBypassDurableProtection()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var seed = await SeedDeadlockReceiptMonthsAsync(database);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLeaseIdentity lease = await DeadlockReceiptRetentionLeaseAsync(collector);
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        await ExecuteAsync(database, """
            INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'fixture','isolated-fixture',clock_timestamp()+interval '1 hour',sha256(convert_to('fixture','UTF8')));
            """);
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted, await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default));
        await ElapseReceiptGraceAsync(database);
        await ExecuteAsync(database, """
            CREATE FUNCTION public.invalidate_receipt_retention_fence() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.action_name='retention.partition.drop' AND NEW.safe_details->>'dataClass'='m6_deadlock_read_receipts' THEN
                UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='retention/maintenance';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER invalidate_receipt_retention_fence AFTER INSERT ON audit.activity
              FOR EACH ROW EXECUTE FUNCTION public.invalidate_receipt_retention_fence();
            """);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => maintenance.RunSystemRetentionStepAsync(lease, Timeout, default).AsTask())).SqlState);
        Assert.Equal(2L, await ReceiptCountAsync(database));
        Assert.Equal(LeaseOwnershipStatus.Current, await new PostgreSqlWorkerLeasePort(collector).AssertOwnershipAsync(new(lease, Timeout), default));
        await ExecuteAsync(database, "DROP TRIGGER invalidate_receipt_retention_fence ON audit.activity; DROP FUNCTION public.invalidate_receipt_retention_fence();");
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var denied = new NpgsqlCommand("SELECT system.update_m10_retention_policy('m6_deadlock_read_receipts',false,NULL,2,1,'fixture','test');", connection, transaction))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await denied.ExecuteScalarAsync())).SqlState);
        await transaction.RollbackAsync();
        await using NpgsqlTransaction authorized = await connection.BeginTransactionAsync();
        await using (var identity = new NpgsqlCommand("SELECT set_config('sqlobserver.role','SecurityAdministrator',true),set_config('sqlobserver.authorization_scope','global',true),set_config('sqlobserver.actor_sid','S-1-5-21-1-2-3-1001',true);", connection, authorized))
            await identity.ExecuteNonQueryAsync();
        await using (var manual = new NpgsqlCommand("SELECT system.detach_m10_partition('events','deadlock_read_receipt',@part,@id);", connection, authorized))
        {
            manual.Parameters.AddWithValue("part", ReceiptPartition(seed.Old)); manual.Parameters.AddWithValue("id", Guid.NewGuid());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await manual.ExecuteScalarAsync())).SqlState);
        }
        await authorized.RollbackAsync();
        await using NpgsqlTransaction mutation = await connection.BeginTransactionAsync();
        await using (var identity = new NpgsqlCommand("SELECT set_config('sqlobserver.role','SecurityAdministrator',true),set_config('sqlobserver.authorization_scope','global',true);", connection, mutation))
            await identity.ExecuteNonQueryAsync();
        foreach (var invalid in new[] { (Days: 29, Floor: 2), (Days: 30, Floor: 1) })
        {
            await mutation.SaveAsync("invalid_receipt_policy");
            await using var bounds = new NpgsqlCommand("SELECT system.update_m10_retention_policy('m6_deadlock_read_receipts',true,@duration,@floor,1,'fixture','Invalid replay lifetime');", connection, mutation);
            bounds.Parameters.AddWithValue("duration", TimeSpan.FromDays(invalid.Days)); bounds.Parameters.AddWithValue("floor", invalid.Floor);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await bounds.ExecuteScalarAsync())).SqlState);
            await mutation.RollbackAsync("invalid_receipt_policy");
        }
        await using (var disable = new NpgsqlCommand("SELECT system.update_m10_retention_policy('m6_deadlock_read_receipts',false,NULL,2,1,'fixture','Suspend receipt cleanup');", connection, mutation))
            Assert.Equal(2L, await disable.ExecuteScalarAsync());
        await mutation.CommitAsync();
        Assert.Equal(SystemRetentionStepOutcome.Cancelled, await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default));
        Assert.Equal(SystemRetentionStepOutcome.Idle, await maintenance.RunSystemRetentionStepAsync(lease, Timeout, default));
        Assert.Equal(2L, await ReceiptCountAsync(database));
        await using var privateStep = collector.CreateCommand("SELECT system.run_attached_deadlock_read_retention_step(NULL,NULL);");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await privateStep.ExecuteScalarAsync())).SqlState);
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease, Timeout), default);
    }

    private static async Task<WorkerLeaseIdentity> DeadlockReceiptRetentionLeaseAsync(NpgsqlDataSource collector)
    {
        var result = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("retention/maintenance"), new(Guid.NewGuid()), new(TimeSpan.FromSeconds(90)), Timeout), default);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status); return result.Lease!.Identity;
    }
    private static string ReceiptPartition(DateTimeOffset month) => "deadlock_read_receipt_p" + month.ToString("yyyyMM", CultureInfo.InvariantCulture);
    private static async Task<long> ReceiptCountAsync(RepositoryTestDatabase database)
    { await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM events.deadlock_read_receipt;"); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task<string> ReceiptContentsAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT jsonb_agg(to_jsonb(r) ORDER BY completed_at,run_id)::text FROM events.deadlock_read_receipt r;");
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<string> ReceiptRetentionReasonAsync(RepositoryTestDatabase database, DateTimeOffset month)
    {
        await using var command = database.DataSource.CreateCommand("SELECT system.deadlock_read_receipt_retention_reason(@start,@finish,clock_timestamp());");
        command.Parameters.AddWithValue("start", month); command.Parameters.AddWithValue("finish", month.AddMonths(1)); return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<string> ReceiptPreviewReasonAsync(RepositoryTestDatabase database, DateTimeOffset month)
    {
        await using var command = database.DataSource.CreateCommand("SELECT reason FROM system.preview_m10_retention(clock_timestamp(),NULL) WHERE data_class='m6_deadlock_read_receipts' AND range_start=@start;");
        command.Parameters.AddWithValue("start", month); return (string)(await command.ExecuteScalarAsync())!;
    }
    private static Task ElapseReceiptGraceAsync(RepositoryTestDatabase database) => ExecuteAsync(database, "UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 second' WHERE data_class='m6_deadlock_read_receipts' AND state='attached_grace';");

    private static async Task<(DateTimeOffset Old, DateTimeOffset Held, Guid Target)> SeedDeadlockReceiptMonthsAsync(RepositoryTestDatabase database)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; DateTimeOffset month = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset old = month.AddMonths(-4), held = month.AddMonths(-3); Guid target = Guid.NewGuid();
        await ExecuteAsync(database, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Receipt retention fixture','sql01',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            INSERT INTO control.collector_contract
            SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(c)||jsonb_build_object('collector_version',2,'output_schema_version',2,'maximum_rows',8193))).*
            FROM control.collector_contract c WHERE c.collector_id='deadlocks.system-health' AND c.collector_version=1
            ON CONFLICT(collector_id,collector_version) DO NOTHING;
            """, ("target", target));
        foreach (DateTimeOffset start in new[] { old, held, month.AddMonths(-1), month })
        {
            await ExecuteAsync(database, $"""
                SET ROLE sqlobserver_migrator;
                CREATE TABLE events.{ReceiptPartition(start)} PARTITION OF events.deadlock_read_receipt FOR VALUES FROM ('{ReadTime(start)}') TO ('{ReadTime(start.AddMonths(1))}');
                ALTER TABLE events.{ReceiptPartition(start)} ENABLE ROW LEVEL SECURITY;
                ALTER TABLE events.{ReceiptPartition(start)} FORCE ROW LEVEL SECURITY;
                INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
                VALUES('events','deadlock_read_receipt','events','{ReceiptPartition(start)}','month',@start,@finish);
                """, ("start", start), ("finish", start.AddMonths(1)));
        }
        foreach (DateTimeOffset start in new[] { old, held })
        {
            Guid run = Guid.NewGuid();
            await ExecuteAsync(database, """
                SET ROLE sqlobserver_migrator;
                INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,
                    schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
                VALUES(@run,@target,'deadlocks.system-health',2,2,1,1,'fixture/deadlock-receipt',gen_random_uuid(),1,decode(repeat('a',64),'hex'),@at,@at);
                INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,
                    inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                    loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
                VALUES(@run,'succeeded','completed',1,0,1,0,0,0,0,0,128,0,0,false,false,'none',true,0,0,decode(repeat('b',64),'hex'),@at);
                INSERT INTO events.deadlock_read_receipt(completed_at,run_id,instance_id,completion_digest,read_digest,checkpoint_digest)
                VALUES(@at,@run,@target,decode(repeat('b',64),'hex'),decode(repeat('c',64),'hex'),decode(repeat('d',64),'hex'));
                INSERT INTO control.deadlock_read_checkpoint(instance_id,target_revision,schedule_revision,run_id,completed_at,completion_digest,
                    source_state,source_epoch,window_state)
                VALUES(@target,1,1,@run,@at,decode(repeat('b',64),'hex'),'ready',decode(repeat('a',64),'hex'),
                    control.merge_deadlock_read_window(NULL,@target,1,decode(repeat('a',64),'hex'),@at,@at-interval '10 minutes',
                        ARRAY[]::uuid[],ARRAY[]::timestamptz[],ARRAY[]::boolean[],true,false))
                ON CONFLICT(instance_id) DO UPDATE SET run_id=EXCLUDED.run_id,completed_at=EXCLUDED.completed_at,window_state=EXCLUDED.window_state;
                """, ("target", target), ("run", run), ("at", start.AddDays(1)));
        }
        return (old, held, target);
    }
}
