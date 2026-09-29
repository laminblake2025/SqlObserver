using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M9TempDbRetentionDependencyTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task UpgradeAddsPrivatePairWorkerWithoutEnablingTempDbPolicies()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult before = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(162, timeout), CancellationToken.None);
        Assert.False(before.HasFailures);
        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(163, Assert.Single(upgrade.Results).Migration.Number.Value);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var inspect = new NpgsqlCommand("""
            SELECT to_regprocedure('system.run_attached_tempdb_retention_step(uuid,bigint)') IS NOT NULL,
                   has_function_privilege('sqlobserver_collector',
                     'system.run_attached_tempdb_retention_step(uuid,bigint)','EXECUTE'),
                   (SELECT count(*) FROM system.retention_policy
                     WHERE data_class IN ('m9_tempdb','m9_tempdb_files')
                       AND NOT enabled AND retain_for IS NULL);
            """, connection);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.Equal(2L, reader.GetInt64(2));
    }

    [Fact]
    public async Task FencedWorkerKeepsPopulatedPairReadableUntilGraceExpires()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult applied = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);

        DateTimeOffset day = new(DateTime.UtcNow.Date.AddDays(-32), TimeSpan.Zero);
        DateTimeOffset observed = day.AddHours(12);
        string suffix = day.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        string snapshotPartition = $"tempdb_snapshot_{suffix}";
        string filePartition = $"tempdb_file_snapshot_{suffix}";
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($"""
            SET ROLE sqlobserver_migrator;
            CREATE TABLE telemetry.{snapshotPartition} PARTITION OF telemetry.tempdb_snapshot
              FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00')
              TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');
            CREATE TABLE telemetry.{filePartition} PARTITION OF telemetry.tempdb_file_snapshot
              FOR VALUES FROM ('{day:yyyy-MM-dd} 00:00:00+00')
              TO ('{day.AddDays(1):yyyy-MM-dd} 00:00:00+00');
            RESET ROLE;
            INSERT INTO system.partition_registry
              (parent_schema,parent_table,partition_schema,partition_name,
               partition_granularity,range_start,range_end)
            VALUES ('telemetry','tempdb_snapshot','telemetry',@snapshot,'day',@start,@end),
                   ('telemetry','tempdb_file_snapshot','telemetry',@file,'day',@start,@end);
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '1 day',policy_revision=2
            WHERE data_class='m9_tempdb';
            INSERT INTO system.recovery_attestation
              (attestation_id,attested_at,attested_by,backup_set_reference,
               expires_at,attestation_digest)
            VALUES(gen_random_uuid(),clock_timestamp(),'test','restore-tested',
              clock_timestamp()+interval '3 days',sha256(convert_to('test','UTF8')));
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'TempDB retention test','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,work_key,
               owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'tempdb.health',1,1,1,1,'test/tempdb-retention',
              gen_random_uuid(),1,decode(repeat('00',32),'hex'),@observed,@observed);
            INSERT INTO telemetry.tempdb_snapshot
              (instance_id,target_revision,run_id,observed_at,state,total_bytes,
               used_bytes,log_total_bytes,log_used_bytes,truncated)
            VALUES(@target,1,@run,@observed,1,100,40,20,5,false);
            INSERT INTO telemetry.tempdb_file_snapshot
              (instance_id,target_revision,run_id,observed_at,file_id,
               size_bytes,used_bytes,free_bytes,state)
            VALUES(@target,1,@run,@observed,1,100,40,60,1);
            """, admin))
        {
            setup.Parameters.AddWithValue("snapshot", snapshotPartition);
            setup.Parameters.AddWithValue("file", filePartition);
            setup.Parameters.AddWithValue("start", day);
            setup.Parameters.AddWithValue("end", day.AddDays(1));
            setup.Parameters.AddWithValue("target", target);
            setup.Parameters.AddWithValue("key", $"tempdb-retention.{target:N}");
            setup.Parameters.AddWithValue("run", run);
            setup.Parameters.AddWithValue("observed", observed);
            await setup.ExecuteNonQueryAsync();
        }

        await using (var privileges = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_collector',
                     'system.run_attached_tempdb_retention_step(uuid,bigint)','EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                     'system.run_m10_retention_step_safe(uuid,bigint)','EXECUTE');
            """, admin))
        await using (NpgsqlDataReader reader = await privileges.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection serverConnection = await server.OpenConnectionAsync();
        foreach ((string parent, string partition) in new[]
            { ("tempdb_snapshot", snapshotPartition),
              ("tempdb_file_snapshot", filePartition) })
        {
            await using NpgsqlTransaction transaction = await serverConnection.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.role','SecurityAdministrator',true),
                       set_config('sqlobserver.authorization_scope','global',true),
                       set_config('sqlobserver.actor_sid','S-1-5-21-1-2-3-1001',true),
                       set_config('sqlobserver.retention_operation_id',@operation,true),
                       set_config('sqlobserver.retention_request_digest',repeat('a',64),true);
                """, serverConnection, transaction);
            context.Parameters.AddWithValue("operation", Guid.NewGuid().ToString());
            await context.ExecuteNonQueryAsync();
            await using var detach = new NpgsqlCommand(
                "SELECT system.detach_m10_partition('telemetry',@parent,@partition,@execution);",
                serverConnection, transaction);
            detach.Parameters.AddWithValue("parent", parent);
            detach.Parameters.AddWithValue("partition", partition);
            detach.Parameters.AddWithValue("execution", Guid.NewGuid());
            PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(
                () => detach.ExecuteScalarAsync());
            Assert.Equal("55000", blocked.SqlState);
        }

        Guid owner = Guid.NewGuid();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection worker = await collector.OpenConnectionAsync();
        await using var acquire = new NpgsqlCommand(
            "SELECT fencing_token FROM control.acquire_worker_lease('retention/maintenance',@owner,interval '5 minutes');",
            worker);
        acquire.Parameters.AddWithValue("owner", owner);
        long fence = (long)(await acquire.ExecuteScalarAsync())!;
        var identity = new WorkerLeaseIdentity(new WorkerLeaseKey("retention/maintenance"),
            new WorkerExecutionId(owner), new FencingToken(fence));
        var retention = new PostgreSqlPartitionMaintenancePort(collector);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        Guid readerLease = Guid.NewGuid();
        await using (var holdChild = new NpgsqlCommand("""
            INSERT INTO system.partition_reader_lease
              (lease_id,parent_schema,parent_table,partition_name,
               owner_execution_id,expires_at)
            VALUES(@lease,'telemetry','tempdb_file_snapshot',@partition,
              gen_random_uuid(),clock_timestamp()+interval '1 hour');
            """, admin))
        {
            holdChild.Parameters.AddWithValue("lease", readerLease);
            holdChild.Parameters.AddWithValue("partition", filePartition);
            await holdChild.ExecuteNonQueryAsync();
        }
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var release = new NpgsqlCommand("""
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp()
            WHERE lease_id=@lease;
            """, admin))
        {
            release.Parameters.AddWithValue("lease", readerLease);
            await release.ExecuteNonQueryAsync();
        }
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.tempdb_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.tempdb_file_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM system.retention_execution
                     WHERE parent_table='tempdb_snapshot' AND state='attached_grace'),
                   (SELECT count(*) FROM system.partition_registry
                     WHERE partition_name IN (@snapshot,@file)
                       AND lifecycle_state='attached');
            """, admin))
        {
            inspect.Parameters.AddWithValue("run", run);
            inspect.Parameters.AddWithValue("snapshot", snapshotPartition);
            inspect.Parameters.AddWithValue("file", filePartition);
            await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (int i = 0; i < 3; i++) Assert.Equal(1L, reader.GetInt64(i));
            Assert.Equal(2L, reader.GetInt64(3));
        }
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var pause = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=false,retain_for=NULL,policy_revision=3
            WHERE data_class='m9_tempdb';
            """, admin))
        {
            Assert.Equal(1, await pause.ExecuteNonQueryAsync());
        }
        Assert.Equal(SystemRetentionStepOutcome.Cancelled,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var resume = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '1 day',policy_revision=4
            WHERE data_class='m9_tempdb';
            """, admin))
        {
            Assert.Equal(1, await resume.ExecuteNonQueryAsync());
        }
        Assert.Equal(SystemRetentionStepOutcome.GraceStarted,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var advance = new NpgsqlCommand("""
            UPDATE system.retention_execution
            SET drop_after=clock_timestamp()-interval '1 minute'
            WHERE parent_table='tempdb_snapshot' AND state='attached_grace';
            """, admin))
        {
            Assert.Equal(1, await advance.ExecuteNonQueryAsync());
        }
        Guid dueReader = Guid.NewGuid();
        await using (var holdParent = new NpgsqlCommand("""
            INSERT INTO system.partition_reader_lease
              (lease_id,parent_schema,parent_table,partition_name,
               owner_execution_id,expires_at)
            VALUES(@lease,'telemetry','tempdb_snapshot',@partition,
              gen_random_uuid(),clock_timestamp()+interval '1 hour');
            """, admin))
        {
            holdParent.Parameters.AddWithValue("lease", dueReader);
            holdParent.Parameters.AddWithValue("partition", snapshotPartition);
            await holdParent.ExecuteNonQueryAsync();
        }
        Assert.Equal(SystemRetentionStepOutcome.Idle,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var release = new NpgsqlCommand("""
            UPDATE system.partition_reader_lease SET released_at=clock_timestamp()
            WHERE lease_id=@lease;
            """, admin))
        {
            release.Parameters.AddWithValue("lease", dueReader);
            await release.ExecuteNonQueryAsync();
        }
        await using (var blockDrop = new NpgsqlCommand($"""
            CREATE VIEW public.tempdb_retention_hold AS
              SELECT run_id FROM telemetry.{filePartition};
            """, admin))
        {
            await blockDrop.ExecuteNonQueryAsync();
        }
        Assert.Equal(SystemRetentionStepOutcome.Retry,
            await retention.RunSystemRetentionStepAsync(identity, timeout, CancellationToken.None));
        await using (var retryState = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.tempdb_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.tempdb_file_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM system.partition_registry
                     WHERE partition_name IN (@snapshot,@file)
                       AND lifecycle_state='attached'),
                   (SELECT attempt FROM system.retention_execution
                     WHERE parent_table='tempdb_snapshot' AND state='attached_grace');
            """, admin))
        {
            retryState.Parameters.AddWithValue("run", run);
            retryState.Parameters.AddWithValue("snapshot", snapshotPartition);
            retryState.Parameters.AddWithValue("file", filePartition);
            await using NpgsqlDataReader reader = await retryState.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(2L, reader.GetInt64(2));
            Assert.Equal(1, reader.GetInt32(3));
        }
        await using (var unblockDrop = new NpgsqlCommand("""
            DROP VIEW public.tempdb_retention_hold;
            INSERT INTO system.retention_drop_retry
              (execution_id,error_code,error_detail,next_attempt_at)
            SELECT execution_id,'00000','test retry release',
                   clock_timestamp()-interval '1 minute'
            FROM system.retention_execution
            WHERE parent_table='tempdb_snapshot' AND state='attached_grace';
            """, admin))
        {
            await unblockDrop.ExecuteNonQueryAsync();
        }
        SystemRetentionStepOutcome drop = await retention.RunSystemRetentionStepAsync(
            identity, timeout, CancellationToken.None);
        await using var failure = new NpgsqlCommand("""
            SELECT last_error FROM system.retention_execution
            WHERE parent_table='tempdb_snapshot' ORDER BY drop_after DESC LIMIT 1;
            """, admin);
        Assert.True(drop == SystemRetentionStepOutcome.Dropped,
            $"Expected paired drop, got {drop}: {await failure.ExecuteScalarAsync()}");
        await using var retired = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.tempdb_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.tempdb_file_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM system.partition_registry
                     WHERE partition_name IN (@snapshot,@file)
                       AND lifecycle_state='dropped'),
                   (SELECT count(*) FROM audit.activity
                     WHERE action_name='retention.partition.drop'
                       AND subject_identifier=@snapshot AND actor_kind='system'
                       AND outcome='succeeded');
            """, admin);
        retired.Parameters.AddWithValue("run", run);
        retired.Parameters.AddWithValue("snapshot", snapshotPartition);
        retired.Parameters.AddWithValue("file", filePartition);
        await using NpgsqlDataReader retiredReader = await retired.ExecuteReaderAsync();
        Assert.True(await retiredReader.ReadAsync());
        Assert.Equal(0L, retiredReader.GetInt64(0));
        Assert.Equal(0L, retiredReader.GetInt64(1));
        Assert.Equal(2L, retiredReader.GetInt64(2));
        Assert.Equal(1L, retiredReader.GetInt64(3));
    }

    [Fact]
    public async Task PopulatedSnapshotRequiresFilePartitionRemovalInSameTransaction()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult applied = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'TempDB retention test','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,work_key,
               owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'tempdb.health',1,1,1,1,'test/tempdb-retention',
              gen_random_uuid(),1,decode(repeat('00',32),'hex'),@observed,@observed);
            INSERT INTO telemetry.tempdb_snapshot
              (instance_id,target_revision,run_id,observed_at,state,total_bytes,
               used_bytes,log_total_bytes,log_used_bytes,truncated)
            VALUES(@target,1,@run,@observed,1,100,40,20,5,false);
            INSERT INTO telemetry.tempdb_file_snapshot
              (instance_id,target_revision,run_id,observed_at,file_id,
               size_bytes,used_bytes,free_bytes,state)
            VALUES(@target,1,@run,@observed,1,100,40,60,1);
            """, connection))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"tempdb-retention.{target:N}");
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("observed", observed);
            await seed.ExecuteNonQueryAsync();
        }

        async Task<string> PartitionAsync(string parent)
        {
            await using var lookup = new NpgsqlCommand("""
                SELECT partition_name::text FROM system.partition_registry
                WHERE parent_schema='telemetry' AND parent_table=@parent
                  AND range_start<=@observed AND range_end>@observed
                  AND lifecycle_state='attached';
                """, connection);
            lookup.Parameters.AddWithValue("parent", parent);
            lookup.Parameters.AddWithValue("observed", observed);
            return Assert.IsType<string>(await lookup.ExecuteScalarAsync());
        }
        string snapshotPartition = await PartitionAsync("tempdb_snapshot");
        string filePartition = await PartitionAsync("tempdb_file_snapshot");

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var detach = new NpgsqlCommand(
                $"ALTER TABLE telemetry.tempdb_snapshot DETACH PARTITION telemetry.{snapshotPartition};",
                connection, transaction);
            PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
                () => detach.ExecuteNonQueryAsync());
            Assert.Equal("23503", failure.SqlState);
            await transaction.RollbackAsync();
        }

        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync())
        {
            await using var retire = new NpgsqlCommand($"""
                ALTER TABLE telemetry.tempdb_file_snapshot
                  DETACH PARTITION telemetry.{filePartition};
                DROP TABLE telemetry.{filePartition};
                ALTER TABLE telemetry.tempdb_snapshot
                  DETACH PARTITION telemetry.{snapshotPartition};
                DROP TABLE telemetry.{snapshotPartition};
                """, connection, transaction);
            await retire.ExecuteNonQueryAsync();
            await transaction.RollbackAsync();
        }

        await using var visible = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.tempdb_snapshot WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.tempdb_file_snapshot WHERE run_id=@run);
            """, connection);
        visible.Parameters.AddWithValue("run", run);
        await using NpgsqlDataReader reader = await visible.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }
}
