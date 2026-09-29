using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M7PartitionedObservationBackfillTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task HistoricalNullIdentityRowsCopyInBoundedResumableBatches()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult applied = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'M7 backfill test','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,work_key,
               owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,'test/query-backfill',
              gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now());
            INSERT INTO events.query_performance_run
              (collection_run_id,instance_id,target_revision,window_start,window_end,
               source,source_state,coverage,freshness,truncated,completion_digest)
            VALUES(@run,@target,1,now()-interval '1 hour',now(),
              'query_store','read_write','complete',true,false,
              decode(repeat('00',32),'hex'));
            INSERT INTO events.query_performance_query
              (collection_run_id,instance_id,database_id,query_fingerprint)
            VALUES(@run,@target,5,decode(repeat('11',32),'hex'));
            -- Permit the pre-0089 nullable shape in this disposable database.
            ALTER TABLE events.query_performance_observation
              DROP CONSTRAINT ck_query_observation_instance_present;
            ALTER TABLE events.query_performance_observation
              DISABLE TRIGGER query_observation_target_identity;
            ALTER TABLE events.query_performance_observation
              DISABLE TRIGGER query_observation_v3_mirror;
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,
               semantics,cpu_ms)
            VALUES
              (@run,5,decode(repeat('11',32),'hex'),decode(repeat('11',16),'hex'),
               'query_store','read_write',now()-interval '20 minutes',now()-interval '19 minutes',now(),
               'query_store_interval',11),
              (@run,5,decode(repeat('11',32),'hex'),decode(repeat('22',16),'hex'),
               'query_store','read_write',now()-interval '18 minutes',now()-interval '17 minutes',now(),
               'query_store_interval',22);
            ALTER TABLE events.query_performance_observation
              ENABLE TRIGGER query_observation_target_identity;
            ALTER TABLE events.query_performance_observation
              ENABLE TRIGGER query_observation_v3_mirror;
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,
               semantics,cpu_ms)
            VALUES(@run,5,decode(repeat('11',32),'hex'),decode(repeat('33',16),'hex'),
               'query_store','read_write',now()-interval '16 minutes',now()-interval '15 minutes',now(),
               'query_store_interval',33);
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("run", run);
            await seed.ExecuteNonQueryAsync();
        }

        // A conflicting row in the successor must abort the batch and leave
        // its cursor untouched. Roll the deliberate corruption back afterward.
        await using (NpgsqlTransaction divergent = await admin.BeginTransactionAsync())
        {
            await using (var corrupt = new NpgsqlCommand("""
                INSERT INTO events.query_performance_observation_v3
                  (instance_id,collection_run_id,database_id,query_fingerprint,
                   observation_key,source,source_state,interval_start,
                   interval_end,observed_at,semantics,cpu_ms)
                SELECT @target,collection_run_id,database_id,query_fingerprint,
                   observation_key,source,source_state,interval_start,
                   interval_end,observed_at,semantics,999
                FROM events.query_performance_observation
                WHERE collection_run_id=@run
                  AND observation_key=decode(repeat('11',16),'hex');
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, divergent))
            {
                corrupt.Parameters.AddWithValue("target", target);
                corrupt.Parameters.AddWithValue("run", run);
                await corrupt.ExecuteNonQueryAsync();
            }
            await using var reject = new NpgsqlCommand(
                "SELECT * FROM control.backfill_query_observation_v3(@target, 1);",
                admin, divergent);
            reject.Parameters.AddWithValue("target", target);
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => reject.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
            await divergent.RollbackAsync();
        }

        async Task<(int Checked, int Copied, bool Complete)> BackfillAsync(int limit)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using (var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction))
            {
                context.Parameters.AddWithValue("target", target);
                await context.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand(
                "SELECT * FROM control.backfill_query_observation_v3(@target, @limit);",
                admin, transaction);
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("limit", limit);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var result = (reader.GetInt32(0), reader.GetInt32(1), reader.GetBoolean(2));
            await reader.DisposeAsync();
            await transaction.CommitAsync();
            return result;
        }

        Assert.Equal((1, 1, false), await BackfillAsync(1));
        Assert.Equal((1, 1, false), await BackfillAsync(1));
        Assert.Equal((1, 0, false), await BackfillAsync(1));
        Assert.Equal((0, 0, true), await BackfillAsync(1));
        Assert.Equal((0, 0, true), await BackfillAsync(1));

        await using var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_observation_v3),
                   (SELECT checked_rows FROM system.query_observation_v3_backfill
                    WHERE instance_id=@target),
                   (SELECT copied_rows FROM system.query_observation_v3_backfill
                    WHERE instance_id=@target),
                   (SELECT count(*) FROM events.query_performance_observation AS old_row
                    JOIN events.query_performance_observation_v3 AS new_row
                     USING(interval_end,collection_run_id,database_id,
                           query_fingerprint,observation_key)
                    WHERE to_jsonb(new_row) IS DISTINCT FROM
                      jsonb_set(to_jsonb(old_row),'{instance_id}',to_jsonb(@target::uuid))),
                   has_function_privilege('sqlobserver_collector',
                     'control.backfill_query_observation_v3(uuid,integer)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.verify_query_observation_v3_window(uuid,timestamptz,timestamptz,integer)',
                     'EXECUTE');
            """, admin);
        inspect.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader resultReader = await inspect.ExecuteReaderAsync();
        Assert.True(await resultReader.ReadAsync());
        Assert.Equal(3L, resultReader.GetInt64(0));
        Assert.Equal(3L, resultReader.GetInt64(1));
        Assert.Equal(2L, resultReader.GetInt64(2));
        Assert.Equal(0L, resultReader.GetInt64(3));
        Assert.False(resultReader.GetBoolean(4));
        Assert.False(resultReader.GetBoolean(5));
        await resultReader.DisposeAsync();

        async Task<(long Source, long Successor, string SourceHash,
            string SuccessorHash, bool Parity)> VerifyAsync(int maxRows = 100)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using (var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction))
            {
                context.Parameters.AddWithValue("target", target);
                await context.ExecuteNonQueryAsync();
            }
            await using var verify = new NpgsqlCommand("""
                SELECT * FROM control.verify_query_observation_v3_window(
                  @target,now()-interval '1 hour',now()+interval '1 hour',@maxRows);
                """, admin, transaction);
            verify.Parameters.AddWithValue("target", target);
            verify.Parameters.AddWithValue("maxRows", maxRows);
            await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var result = (reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
            await reader.DisposeAsync();
            await transaction.CommitAsync();
            return result;
        }

        var matched = await VerifyAsync();
        Assert.Equal((3L, 3L), (matched.Source, matched.Successor));
        Assert.Equal(matched.SourceHash, matched.SuccessorHash);
        Assert.Equal(64, matched.SourceHash.Length);
        Assert.True(matched.Parity);

        await using (var live = new NpgsqlCommand("""
            INSERT INTO events.query_performance_query
              (collection_run_id,instance_id,database_id,query_fingerprint)
            VALUES(@run,@target,5,decode(repeat('22',32),'hex'));
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,
               semantics,cpu_ms)
            VALUES(@run,5,decode(repeat('22',32),'hex'),decode(repeat('55',16),'hex'),
               'query_store','read_write',now()-interval '6 minutes',
               now()-interval '5 minutes',now(),'query_store_interval',44);
            """, admin))
        {
            live.Parameters.AddWithValue("run", run);
            live.Parameters.AddWithValue("target", target);
            await live.ExecuteNonQueryAsync();
        }

        await using (NpgsqlTransaction shadow = await admin.BeginTransactionAsync())
        {
            await using (var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, shadow))
            {
                context.Parameters.AddWithValue("target", target);
                await context.ExecuteNonQueryAsync();
            }
            await using var compare = new NpgsqlCommand("""
                WITH old_top AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_top_queries_projection(
                    @target,now()-interval '1 hour',now()+interval '1 hour',
                    'cpu',1,NULL::integer,NULL::timestamptz,NULL::bytea,
                    NULL::bytea,NULL::uuid,NULL::bigint,NULL::bytea,
                    now()+interval '1 hour') AS item
                ), new_top AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_top_queries_projection_v3(
                    @target,now()-interval '1 hour',now()+interval '1 hour',
                    'cpu',1,NULL::integer,NULL::timestamptz,NULL::bytea,
                    NULL::bytea,NULL::uuid,NULL::bigint,NULL::bytea,
                    now()+interval '1 hour') AS item
                ), old_history AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_query_history_projection(
                    @target,5,decode(repeat('11',32),'hex'),
                    now()-interval '1 hour',now()+interval '1 hour',200,
                    NULL::timestamptz,NULL::uuid,NULL::bytea,NULL::bytea,
                    now()+interval '1 hour') AS item
                ), new_history AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_query_history_projection_v3(
                    @target,5,decode(repeat('11',32),'hex'),
                    now()-interval '1 hour',now()+interval '1 hour',200,
                    NULL::timestamptz,NULL::uuid,NULL::bytea,NULL::bytea,
                    now()+interval '1 hour') AS item
                ), old_history_page AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_query_history_projection(
                    @target,5,decode(repeat('11',32),'hex'),
                    now()-interval '1 hour',now()+interval '1 hour',1,
                    (SELECT interval_end FROM events.query_performance_observation
                     WHERE collection_run_id=@run
                       AND observation_key=decode(repeat('11',16),'hex')),
                    @run,NULL::bytea,decode(repeat('11',16),'hex'),
                    now()+interval '1 hour') AS item
                ), new_history_page AS (
                  SELECT to_jsonb(item) AS payload
                  FROM control.get_query_history_projection_v3(
                    @target,5,decode(repeat('11',32),'hex'),
                    now()-interval '1 hour',now()+interval '1 hour',1,
                    (SELECT interval_end FROM events.query_performance_observation
                     WHERE collection_run_id=@run
                       AND observation_key=decode(repeat('11',16),'hex')),
                    @run,NULL::bytea,decode(repeat('11',16),'hex'),
                    now()+interval '1 hour') AS item
                )
                SELECT (SELECT count(*) FROM (
                  (SELECT payload FROM old_top EXCEPT ALL SELECT payload FROM new_top)
                  UNION ALL
                  (SELECT payload FROM new_top EXCEPT ALL SELECT payload FROM old_top)
                ) AS differences),
                (SELECT count(*) FROM (
                  (SELECT payload FROM old_history EXCEPT ALL SELECT payload FROM new_history)
                  UNION ALL
                  (SELECT payload FROM new_history EXCEPT ALL SELECT payload FROM old_history)
                ) AS differences),
                (SELECT count(*) FROM new_top),
                (SELECT count(*) FROM new_history),
                (SELECT count(*) FROM (
                  (SELECT payload FROM old_history_page EXCEPT ALL
                   SELECT payload FROM new_history_page)
                  UNION ALL
                  (SELECT payload FROM new_history_page EXCEPT ALL
                   SELECT payload FROM old_history_page)
                ) AS differences),
                (SELECT count(*) FROM new_history_page),
                has_function_privilege('sqlobserver_server',
                  'control.get_top_queries_projection_v3(uuid,timestamptz,timestamptz,text,integer,integer,timestamptz,bytea,bytea,uuid,bigint,bytea,timestamptz)',
                  'EXECUTE'),
                has_function_privilege('sqlobserver_server',
                  'control.get_query_history_projection_v3(uuid,integer,bytea,timestamptz,timestamptz,integer,timestamptz,uuid,bytea,bytea,timestamptz)',
                  'EXECUTE');
                """, admin, shadow);
            compare.Parameters.AddWithValue("target", target);
            compare.Parameters.AddWithValue("run", run);
            await using NpgsqlDataReader comparison = await compare.ExecuteReaderAsync();
            Assert.True(await comparison.ReadAsync());
            Assert.Equal(0L, comparison.GetInt64(0));
            Assert.Equal(0L, comparison.GetInt64(1));
            Assert.Equal(2L, comparison.GetInt64(2));
            Assert.Equal(3L, comparison.GetInt64(3));
            Assert.Equal(0L, comparison.GetInt64(4));
            Assert.Equal(2L, comparison.GetInt64(5));
            Assert.False(comparison.GetBoolean(6));
            Assert.False(comparison.GetBoolean(7));
            await comparison.DisposeAsync();
            await shadow.CommitAsync();
        }

        await using (var extra = new NpgsqlCommand("""
            INSERT INTO events.query_performance_observation_v3
              (instance_id,collection_run_id,database_id,query_fingerprint,
               observation_key,source,source_state,interval_start,
               interval_end,observed_at,semantics,cpu_ms)
            SELECT @target,collection_run_id,database_id,query_fingerprint,
              decode(repeat('44',16),'hex'),source,source_state,interval_start,
              interval_end,observed_at,semantics,cpu_ms
            FROM events.query_performance_observation
            WHERE collection_run_id=@run
              AND observation_key=decode(repeat('33',16),'hex');
            """, admin))
        {
            extra.Parameters.AddWithValue("target", target);
            extra.Parameters.AddWithValue("run", run);
            Assert.Equal(1, await extra.ExecuteNonQueryAsync());
        }
        var extraRowParity = await VerifyAsync();
        Assert.Equal((4L, 5L), (extraRowParity.Source, extraRowParity.Successor));
        Assert.NotEqual(extraRowParity.SourceHash, extraRowParity.SuccessorHash);
        Assert.False(extraRowParity.Parity);
        PostgresException tooDense = await Assert.ThrowsAsync<PostgresException>(
            () => VerifyAsync(4));
        Assert.Equal("54000", tooDense.SqlState);
    }
}
