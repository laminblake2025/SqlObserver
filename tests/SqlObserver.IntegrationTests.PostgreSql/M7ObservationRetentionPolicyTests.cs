using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M7ObservationRetentionPolicyTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task CustomizedV2PolicyMovesToV3WithoutBeingEnabledAutomatically()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromMinutes(2));
        MigrationBatchResult before = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(176, timeout), CancellationToken.None);
        Assert.False(before.HasFailures);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var customize = new NpgsqlCommand("""
            UPDATE system.retention_policy
            SET enabled=true,retain_for=interval '45 days',
                policy_revision=policy_revision+1,updated_by='operator'
            WHERE data_class='m7_query_performance';
            """, admin))
        {
            Assert.Equal(1, await customize.ExecuteNonQueryAsync());
        }
        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(177, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using (NpgsqlTransaction activation = await admin.BeginTransactionAsync())
        {
            await using var activate = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT control.set_query_observation_v3_read_route(true);
                SELECT control.activate_query_observation_v3_writer();
                """, admin, activation);
            await activate.ExecuteNonQueryAsync();
            await activation.CommitAsync();
        }
        await using var inspect = new NpgsqlCommand("""
            SELECT parent_table='query_performance_observation_v3',
                   enabled,retain_for=interval '45 days',policy_revision
            FROM system.retention_policy
            WHERE data_class='m7_query_performance';
            """, admin);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(3L, reader.GetInt64(3));
    }

    [Fact]
    public async Task FencedWorkerRetiresV3DayWithoutCascadingRunGraph()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset day = new(DateTime.UtcNow.Date.AddDays(-40), TimeSpan.Zero);
        DateTimeOffset observed = day.AddHours(12);
        string partition = $"query_performance_observation_v3_p{day:yyyyMMdd}";
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var register = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'M7 retention target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            register.Parameters.AddWithValue("target", target);
            await register.ExecuteNonQueryAsync();
        }
        await using (NpgsqlTransaction activation = await admin.BeginTransactionAsync())
        {
            await using var activate = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT complete FROM control.backfill_query_observation_v3(@target,1);
                SELECT control.set_query_observation_v3_read_route(true);
                SELECT control.activate_query_observation_v3_writer();
                """, admin, activation);
            activate.Parameters.AddWithValue("target", target);
            await activate.ExecuteNonQueryAsync();
            await activation.CommitAsync();
        }

        await using (NpgsqlTransaction seed = await admin.BeginTransactionAsync())
        {
            await using var setup = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT control.ensure_query_observation_v3_day(@day::date);
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,work_key,
                   owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
                VALUES(@run,@target,'queries.performance',1,1,1,1,
                  'test/m7-retention',gen_random_uuid(),1,
                  decode(repeat('00',32),'hex'),@observed,@observed);
                INSERT INTO events.query_performance_run
                  (collection_run_id,instance_id,target_revision,window_start,
                   window_end,source,source_state,coverage,freshness,truncated,
                   completion_digest)
                VALUES(@run,@target,1,@observed-interval '1 hour',@observed,
                  'query_store','read_write','complete',true,false,
                  decode(repeat('00',32),'hex'));
                INSERT INTO events.query_performance_query
                  (collection_run_id,instance_id,database_id,query_fingerprint)
                VALUES(@run,@target,5,decode(repeat('11',32),'hex'));
                INSERT INTO events.query_performance_observation_v3
                  (instance_id,collection_run_id,database_id,query_fingerprint,
                   observation_key,source,source_state,interval_start,
                   interval_end,observed_at,semantics,cpu_ms)
                VALUES(@target,@run,5,decode(repeat('11',32),'hex'),
                  decode(repeat('22',16),'hex'),'query_store','read_write',
                  @observed-interval '1 minute',@observed,@observed,
                  'query_store_interval',12);
                UPDATE system.retention_policy
                SET retain_for=interval '30 days',policy_revision=policy_revision+1
                WHERE data_class='m7_query_performance';
                INSERT INTO system.recovery_attestation
                  (attestation_id,attested_at,attested_by,backup_set_reference,
                   expires_at,attestation_digest)
                VALUES(gen_random_uuid(),clock_timestamp(),'test','restore-tested',
                  clock_timestamp()+interval '3 days',
                  sha256(convert_to('test','UTF8')));
                """, admin, seed);
            setup.Parameters.AddWithValue("target", target);
            setup.Parameters.AddWithValue("run", run);
            setup.Parameters.AddWithValue("day", day);
            setup.Parameters.AddWithValue("observed", observed);
            await setup.ExecuteNonQueryAsync();
            await seed.CommitAsync();
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
        Assert.Equal(SystemRetentionStepOutcome.Detached,
            await retention.RunSystemRetentionStepAsync(identity, timeout,
                CancellationToken.None));

        await using (var grace = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_observation_v3
                     WHERE collection_run_id=@run),
                   (SELECT count(*) FROM system.retention_execution
                     WHERE partition_name=@partition AND state='grace'),
                   (SELECT count(*) FROM events.query_performance_run
                     WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_query
                     WHERE collection_run_id=@run);
            """, admin))
        {
            grace.Parameters.AddWithValue("run", run);
            grace.Parameters.AddWithValue("partition", partition);
            await using NpgsqlDataReader rows = await grace.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(0L, rows.GetInt64(0));
            for (int column = 1; column < 4; column++)
                Assert.Equal(1L, rows.GetInt64(column));
        }
        await using (var expire = new NpgsqlCommand("""
            UPDATE system.retention_execution
            SET drop_after=clock_timestamp()-interval '1 minute'
            WHERE partition_name=@partition AND state='grace';
            """, admin))
        {
            expire.Parameters.AddWithValue("partition", partition);
            Assert.Equal(1, await expire.ExecuteNonQueryAsync());
        }
        Assert.Equal(SystemRetentionStepOutcome.Dropped,
            await retention.RunSystemRetentionStepAsync(identity, timeout,
                CancellationToken.None));
        await using var retired = new NpgsqlCommand("""
            SELECT to_regclass('events.'||@partition) IS NULL,
                   (SELECT count(*) FROM system.partition_registry
                     WHERE partition_name=@partition AND lifecycle_state='dropped'),
                   (SELECT count(*) FROM events.query_performance_run
                     WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_query
                     WHERE collection_run_id=@run);
            """, admin);
        retired.Parameters.AddWithValue("partition", partition);
        retired.Parameters.AddWithValue("run", run);
        await using NpgsqlDataReader final = await retired.ExecuteReaderAsync();
        Assert.True(await final.ReadAsync());
        Assert.True(final.GetBoolean(0));
        for (int column = 1; column < 4; column++)
            Assert.Equal(1L, final.GetInt64(column));
    }
}
