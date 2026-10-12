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
    public async Task FencedGraphRetirementPreservesCollectionAccountingAndRecordsBoundedDeletion()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid(), owner = Guid.NewGuid();
        DateTimeOffset observed = new(DateTime.UtcNow.Date.AddDays(-40), TimeSpan.Zero);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var register = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'M7 graph target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            register.Parameters.AddWithValue("target", target);
            await register.ExecuteNonQueryAsync();
        }
        await using (NpgsqlTransaction seed = await admin.BeginTransactionAsync())
        {
            await using var setup = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT complete FROM control.backfill_query_observation_v3(@target,1);
                SELECT control.set_query_observation_v3_read_route(true);
                SELECT control.activate_query_observation_v3_writer();
                UPDATE system.retention_policy
                SET retain_for=interval '30 days',policy_revision=policy_revision+1
                WHERE data_class='m7_query_performance';
                INSERT INTO system.recovery_attestation
                  (attestation_id,attested_at,attested_by,backup_set_reference,
                   expires_at,attestation_digest)
                VALUES(gen_random_uuid(),clock_timestamp(),'test','restore-tested',
                  clock_timestamp()+interval '3 days',
                  sha256(convert_to('test','UTF8')));
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,work_key,
                   owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
                VALUES(@run,@target,'queries.performance',1,1,1,1,
                  'test/m7-graph',gen_random_uuid(),1,
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
                INSERT INTO events.query_performance_plan
                  (collection_run_id,database_id,query_fingerprint,plan_fingerprint)
                VALUES(@run,5,decode(repeat('11',32),'hex'),
                  decode(repeat('22',32),'hex'));
                INSERT INTO security.protected_diagnostic_payload
                  (payload_id,payload_kind,fingerprint,protection_algorithm,
                   key_identifier,nonce,authentication_tag,ciphertext,instance_id)
                VALUES
                  (@text_id,'query_text',decode(repeat('33',32),'hex'),
                   'AES-GCM','test',decode(repeat('44',12),'hex'),
                   decode(repeat('55',16),'hex'),decode('66','hex'),@target),
                  (@plan_id,'execution_plan',decode(repeat('77',32),'hex'),
                   'AES-GCM','test',decode(repeat('88',12),'hex'),
                   decode(repeat('99',16),'hex'),decode('aa','hex'),@target);
                INSERT INTO events.query_performance_content_link
                  (collection_run_id,instance_id,database_id,query_fingerprint,
                   content_reference,content_available)
                VALUES(@run,@target,5,decode(repeat('11',32),'hex'),@text_id,true);
                INSERT INTO events.query_performance_plan_content_link
                  (collection_run_id,instance_id,database_id,query_fingerprint,
                   plan_fingerprint,content_reference,content_available)
                VALUES(@run,@target,5,decode(repeat('11',32),'hex'),
                  decode(repeat('22',32),'hex'),@plan_id,true);
                INSERT INTO events.query_performance_wait_snapshot
                  (collection_run_id,instance_id,database_id,query_fingerprint,
                   plan_fingerprint,categories)
                VALUES(@run,@target,5,decode(repeat('11',32),'hex'),
                  decode(repeat('22',32),'hex'),'[]'::jsonb);
                INSERT INTO events.query_performance_database_status
                  (collection_run_id,instance_id,database_id,status,source_state,
                   reason,fallback_attempted,truncated,source_rows_read,response_bytes)
                VALUES(@run,@target,5,'available','read_write','test',false,
                  false,1,0);
                INSERT INTO events.query_performance_stage
                  (collection_run_id,instance_id,database_id,payload,payload_bytes)
                VALUES(@run,@target,5,'{}'::jsonb,2);
                """, admin, seed);
            setup.Parameters.AddWithValue("target", target);
            setup.Parameters.AddWithValue("run", run);
            setup.Parameters.AddWithValue("observed", observed);
            setup.Parameters.AddWithValue("text_id", Guid.NewGuid());
            setup.Parameters.AddWithValue("plan_id", Guid.NewGuid());
            await setup.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection worker = await collector.OpenConnectionAsync();
        await using var acquire = new NpgsqlCommand(
            "SELECT fencing_token FROM control.acquire_worker_lease('retention/maintenance',@owner,interval '5 minutes');",
            worker);
        acquire.Parameters.AddWithValue("owner", owner);
        long fence = (long)(await acquire.ExecuteScalarAsync())!;
        await using (var grants = new NpgsqlCommand("""
            SELECT has_function_privilege(current_user,
                     'system.query_performance_run_retention_holds(uuid,uuid)',
                     'EXECUTE'),
                   has_function_privilege(current_user,
                     'system.retire_query_performance_run(uuid,uuid,uuid,bigint,integer)',
                     'EXECUTE'),
                   has_function_privilege(current_user,
                     'system.run_query_run_retention_step(uuid,bigint)',
                     'EXECUTE');
            """, worker))
        {
            await using NpgsqlDataReader grantReader = await grants.ExecuteReaderAsync();
            Assert.True(await grantReader.ReadAsync());
            Assert.False(grantReader.GetBoolean(0));
            Assert.False(grantReader.GetBoolean(1));
            Assert.True(grantReader.GetBoolean(2));
        }
        var identity = new WorkerLeaseIdentity(
            new WorkerLeaseKey("retention/maintenance"),
            new WorkerExecutionId(owner), new FencingToken(fence));
        var retention = new PostgreSqlPartitionMaintenancePort(collector);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(15));

        async Task<string> RetireAsync(int limit, long? suppliedFence = null)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction);
            context.Parameters.AddWithValue("target", target);
            await context.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand("""
                SELECT system.retire_query_performance_run(
                  @target,@run,@owner,@fence,@limit);
                """, admin, transaction);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("run", run);
            command.Parameters.AddWithValue("owner", owner);
            command.Parameters.AddWithValue("fence", suppliedFence ?? fence);
            command.Parameters.AddWithValue("limit", limit);
            object? result = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return Assert.IsType<string>(result);
        }

        PostgresException staleFence = await Assert.ThrowsAsync<PostgresException>(
            () => RetireAsync(4096, fence + 1));
        Assert.Equal("55000", staleFence.SqlState);
        await using (NpgsqlTransaction denied = await admin.BeginTransactionAsync())
        {
            await using var server = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_server;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT system.retire_query_performance_run(
                  @target,@run,@owner,@fence,4096);
                """, admin, denied);
            server.Parameters.AddWithValue("target", target);
            server.Parameters.AddWithValue("run", run);
            server.Parameters.AddWithValue("owner", owner);
            server.Parameters.AddWithValue("fence", fence);
            PostgresException deniedError = await Assert.ThrowsAsync<PostgresException>(
                () => server.ExecuteNonQueryAsync());
            Assert.Equal("42501", deniedError.SqlState);
            await denied.RollbackAsync();
        }
        Assert.Equal("held", await RetireAsync(4096));
        Assert.Equal(QueryRunRetentionStepOutcome.Held,
            await retention.RunQueryRunRetentionStepAsync(
                identity, timeout, CancellationToken.None));
        await using (var outcome = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run_outcome
              (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
               source_row_count,output_item_count,inserted_item_count,
               duplicate_item_count,rejected_item_count,response_bytes,
               output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
               loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
               completed_at)
            VALUES(@run,'succeeded','completed',1,0,1,1,1,1,0,0,0,0,0,
              false,false,'none',true,0,0,decode(repeat('00',32),'hex'),@observed);
            """, admin))
        {
            outcome.Parameters.AddWithValue("run", run);
            outcome.Parameters.AddWithValue("observed", observed);
            await outcome.ExecuteNonQueryAsync();
        }
        Assert.Equal("oversize", await RetireAsync(1));
        Assert.Equal(QueryRunRetentionStepOutcome.Idle,
            await retention.RunQueryRunRetentionStepAsync(
                identity, timeout, CancellationToken.None));
        await using (var bulk = new NpgsqlCommand("""
            INSERT INTO events.query_performance_stage
              (collection_run_id,instance_id,database_id,payload,payload_bytes)
            SELECT @run,@target,database_id,'{}'::jsonb,2
            FROM generate_series(6,4101) AS database_id;
            """, admin))
        {
            bulk.Parameters.AddWithValue("run", run);
            bulk.Parameters.AddWithValue("target", target);
            Assert.Equal(4096, await bulk.ExecuteNonQueryAsync());
        }
        Assert.Equal(QueryRunRetentionStepOutcome.Oversize,
            await retention.RunQueryRunRetentionStepAsync(
                identity, timeout, CancellationToken.None));
        await using (var blocked = new NpgsqlCommand("""
            SELECT count(*) FROM system.query_run_retention_blocked
            WHERE run_id=@run AND instance_id=@target AND reason='oversize';
            """, admin))
        {
            blocked.Parameters.AddWithValue("run", run);
            blocked.Parameters.AddWithValue("target", target);
            Assert.Equal(1L, (long)(await blocked.ExecuteScalarAsync())!);
        }
        await using (var clearBulk = new NpgsqlCommand("""
            DELETE FROM events.query_performance_stage
            WHERE collection_run_id=@run AND database_id>=6;
            """, admin))
        {
            clearBulk.Parameters.AddWithValue("run", run);
            Assert.Equal(4096, await clearBulk.ExecuteNonQueryAsync());
        }
        Assert.Equal(QueryRunRetentionStepOutcome.Idle,
            await retention.RunQueryRunRetentionStepAsync(
                identity, timeout, CancellationToken.None));
        Assert.Equal(QueryRunRetentionStepOutcome.Retired,
            await retention.RunQueryRunRetentionStepAsync(
                identity, timeout, CancellationToken.None));
        Assert.Equal("missing", await RetireAsync(4096));
        await using var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_run
                    WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_query
                    WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_wait_snapshot
                    WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_stage
                    WHERE collection_run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome
                    WHERE run_id=@run),
                   (SELECT count(*) FROM security.protected_diagnostic_payload
                    WHERE instance_id=@target),
                   (SELECT child_count FROM system.query_run_retirement
                    WHERE run_id=@run),
                   (SELECT count(*) FROM system.query_run_retention_blocked
                    WHERE run_id=@run),
                   (SELECT count(*) FROM audit.activity
                    WHERE action_name='retention.query_run.retire'
                      AND safe_details->>'runId'=@run::text);
            """, admin);
        inspect.Parameters.AddWithValue("run", run);
        inspect.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader rows = await inspect.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        for (int column = 0; column < 4; column++) Assert.Equal(0L, rows.GetInt64(column));
        Assert.Equal(1L, rows.GetInt64(4));
        Assert.Equal(2L, rows.GetInt64(5));
        Assert.Equal(7, rows.GetInt32(6));
        Assert.Equal(0L, rows.GetInt64(7));
        Assert.Equal(1L, rows.GetInt64(8));
    }

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
                INSERT INTO telemetry.collection_run_outcome
                  (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                   source_row_count,output_item_count,inserted_item_count,
                   duplicate_item_count,rejected_item_count,response_bytes,
                   output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                   loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                   completed_at)
                VALUES(@run,'succeeded','completed',1,0,1,1,1,1,0,0,0,0,0,
                  false,false,'none',true,0,0,
                  decode(repeat('00',32),'hex'),@observed);
                """, admin, seed);
            setup.Parameters.AddWithValue("target", target);
            setup.Parameters.AddWithValue("run", run);
            setup.Parameters.AddWithValue("day", day);
            setup.Parameters.AddWithValue("observed", observed);
            await setup.ExecuteNonQueryAsync();
            await seed.CommitAsync();
        }

        async Task<string[]> HoldsAsync()
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction);
            context.Parameters.AddWithValue("target", target);
            await context.ExecuteNonQueryAsync();
            await using var inspect = new NpgsqlCommand("""
                SELECT hold_code
                FROM system.query_performance_run_retention_holds(@target,@run);
                """, admin, transaction);
            inspect.Parameters.AddWithValue("target", target);
            inspect.Parameters.AddWithValue("run", run);
            var holds = new List<string>();
            await using (NpgsqlDataReader rows = await inspect.ExecuteReaderAsync())
                while (await rows.ReadAsync()) holds.Add(rows.GetString(0));
            await transaction.CommitAsync();
            return holds.ToArray();
        }
        string[] beforeDetach = await HoldsAsync();
        Assert.Contains("v3_observation", beforeDetach);
        Assert.DoesNotContain("outcome_missing", beforeDetach);
        Assert.DoesNotContain("unreviewed_foreign_key", beforeDetach);
        await using (NpgsqlTransaction denied = await admin.BeginTransactionAsync())
        {
            await using var server = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_server;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                SELECT * FROM system.query_performance_run_retention_holds(@target,@run);
                """, admin, denied);
            server.Parameters.AddWithValue("target", target);
            server.Parameters.AddWithValue("run", run);
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => server.ExecuteNonQueryAsync());
            Assert.Equal("42501", error.SqlState);
            await denied.RollbackAsync();
        }

        Guid owner = Guid.NewGuid();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection worker = await collector.OpenConnectionAsync();
        await using var acquire = new NpgsqlCommand(
            "SELECT fencing_token FROM control.acquire_worker_lease('retention/maintenance',@owner,interval '5 minutes');",
            worker);
        acquire.Parameters.AddWithValue("owner", owner);
        long fence = (long)(await acquire.ExecuteScalarAsync())!;
        async Task<string> RetireResultAsync()
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction);
            context.Parameters.AddWithValue("target", target);
            await context.ExecuteNonQueryAsync();
            await using var retire = new NpgsqlCommand("""
                SELECT system.retire_query_performance_run(
                  @target,@run,@owner,@fence,4096);
                """, admin, transaction);
            retire.Parameters.AddWithValue("target", target);
            retire.Parameters.AddWithValue("run", run);
            retire.Parameters.AddWithValue("owner", owner);
            retire.Parameters.AddWithValue("fence", fence);
            string result = Assert.IsType<string>(await retire.ExecuteScalarAsync());
            await transaction.CommitAsync();
            return result;
        }
        Assert.Equal("held", await RetireResultAsync());
        var identity = new WorkerLeaseIdentity(new WorkerLeaseKey("retention/maintenance"),
            new WorkerExecutionId(owner), new FencingToken(fence));
        var retention = new PostgreSqlPartitionMaintenancePort(collector);
        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        Assert.Equal(SystemRetentionStepOutcome.Detached,
            await retention.RunSystemRetentionStepAsync(identity, timeout,
                CancellationToken.None));
        string[] duringGrace = await HoldsAsync();
        Assert.Contains("partition_grace", duringGrace);
        Assert.DoesNotContain("v3_observation", duringGrace);
        Assert.Equal("held", await RetireResultAsync());

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
        string[] afterDrop = await HoldsAsync();
        Assert.Contains("no_known_hold", afterDrop);
        Assert.DoesNotContain("partition_grace", afterDrop);
        await using (NpgsqlTransaction incident = await admin.BeginTransactionAsync())
        {
            await using var evidence = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                INSERT INTO analytics.evidence_packet_v2
                  (occurred_at,packet_id,instance_id,target_revision,evidence_kind,
                   source_run_id,source_digest,evidence,confidence,visibility_state)
                VALUES(clock_timestamp(),gen_random_uuid(),@target,1,'metric',
                  @run,sha256(convert_to('test','UTF8')),'{}'::jsonb,1,'complete');
                """, admin, incident);
            evidence.Parameters.AddWithValue("target", target);
            evidence.Parameters.AddWithValue("run", run);
            await evidence.ExecuteNonQueryAsync();
            await incident.CommitAsync();
        }
        Assert.Contains("incident_evidence", await HoldsAsync());
        Assert.Equal("held", await RetireResultAsync());
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
