using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M7PartitionedObservationMirrorTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task NewObservationsMirrorAtomicallyIntoTargetScopedIntervalPartitions()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult applied = await migrations.ApplyPendingAsync(new MigrationApplyRequest(
            MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
            CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Guid firstRun = Guid.NewGuid(), secondRun = Guid.NewGuid();
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT id,id::text,'M7 mirror test','sql01',1433,
                   interval '5 seconds','windows_integrated_service_identity',
                   'mandatory_validated','active',1,now(),now(),now()
            FROM (VALUES(@first),(@second)) AS targets(id);
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,work_key,
               owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT id,target,'queries.performance',1,1,1,1,'test/query-mirror',
                   gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now()
            FROM (VALUES(@firstRun,@first),(@secondRun,@second)) AS runs(id,target);
            INSERT INTO events.query_performance_run
              (collection_run_id,instance_id,target_revision,window_start,window_end,
               source,source_state,coverage,freshness,truncated,completion_digest)
            SELECT id,target,1,now()-interval '1 hour',now(),
                   'query_store','read_write','complete',true,false,
                   decode(repeat('00',32),'hex')
            FROM (VALUES(@firstRun,@first),(@secondRun,@second)) AS runs(id,target);
            INSERT INTO events.query_performance_query
              (collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT id,target,5,decode(repeat('11',32),'hex')
            FROM (VALUES(@firstRun,@first),(@secondRun,@second)) AS runs(id,target);
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,
               semantics,cpu_ms)
            VALUES
              (@firstRun,5,decode(repeat('11',32),'hex'),decode(repeat('21',16),'hex'),
               'query_store','read_write',now()-interval '10 minutes',now(),now(),
               'query_store_interval',21),
              (@secondRun,5,decode(repeat('11',32),'hex'),decode(repeat('22',16),'hex'),
               'query_store','read_write',now()-interval '10 minutes',now(),now(),
               'query_store_interval',22);
            """, admin))
        {
            seed.Parameters.AddWithValue("first", first);
            seed.Parameters.AddWithValue("second", second);
            seed.Parameters.AddWithValue("firstRun", firstRun);
            seed.Parameters.AddWithValue("secondRun", secondRun);
            await seed.ExecuteNonQueryAsync();
        }

        await using (var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_observation_v3),
                   (SELECT count(*) FROM system.partition_registry
                    WHERE parent_schema='events'
                      AND parent_table='query_performance_observation_v3'
                      AND lifecycle_state='attached'),
                   pg_get_partkeydef('events.query_performance_observation_v3'::regclass),
                   control.ensure_query_observation_v3_partitions(current_date),
                   has_function_privilege('sqlobserver_collector',
                     'control.ensure_query_observation_v3_partitions(date)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.ensure_query_observation_v3_partitions(date)','EXECUTE'),
                   has_table_privilege('sqlobserver_server',
                     'events.query_performance_observation_v3','SELECT'),
                   has_function_privilege('sqlobserver_collector',
                     'control.ensure_query_observation_v3_day(date)','EXECUTE');
            """, admin))
        await using (NpgsqlDataReader reader = await inspect.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(17L, reader.GetInt64(1));
            Assert.Equal("RANGE (interval_end)", reader.GetString(2));
            Assert.Equal(0, reader.GetInt32(3));
            Assert.True(reader.GetBoolean(4));
            Assert.False(reader.GetBoolean(5));
            Assert.False(reader.GetBoolean(6));
            Assert.False(reader.GetBoolean(7));
        }

        async Task<long> CountVisibleAsync(Guid? target)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var context = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                """, admin, transaction);
            context.Parameters.AddWithValue("scope", target?.ToString("D") ?? "");
            await context.ExecuteNonQueryAsync();
            await using var count = new NpgsqlCommand(
                "SELECT count(*) FROM events.query_performance_observation_v3;",
                admin, transaction);
            long visible = (long)(await count.ExecuteScalarAsync())!;
            await transaction.CommitAsync();
            return visible;
        }
        Assert.Equal(0L, await CountVisibleAsync(null));
        Assert.Equal(1L, await CountVisibleAsync(first));
        Assert.Equal(1L, await CountVisibleAsync(second));

        await using (var parity = new NpgsqlCommand("""
            SELECT count(*) FROM (
              SELECT to_jsonb(observation) AS row_value
              FROM events.query_performance_observation AS observation
              EXCEPT
              SELECT to_jsonb(mirror) AS row_value
              FROM events.query_performance_observation_v3 AS mirror
            ) AS missing;
            """, admin))
        {
            Assert.Equal(0L, (long)(await parity.ExecuteScalarAsync())!);
        }

        DateTimeOffset historicalDay = new(DateTime.UtcNow.Date.AddDays(-10), TimeSpan.Zero);
        string historicalPartition = $"query_performance_observation_v3_p{historicalDay:yyyyMMdd}";
        await using (var spoof = new NpgsqlCommand(
            $"CREATE TABLE events.{historicalPartition}(dummy integer);", admin))
        {
            await spoof.ExecuteNonQueryAsync();
        }
        await using (var beyond = new NpgsqlCommand("""
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,semantics)
            VALUES(@firstRun,5,decode(repeat('11',32),'hex'),decode(repeat('23',16),'hex'),
              'query_store','read_write',@historicalStart,
              @historicalEnd,now(),'query_store_interval');
            """, admin))
        {
            beyond.Parameters.AddWithValue("firstRun", firstRun);
            beyond.Parameters.AddWithValue("historicalStart", historicalDay.AddMinutes(50));
            beyond.Parameters.AddWithValue("historicalEnd", historicalDay.AddHours(1));
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => beyond.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
            await using var unchanged = new NpgsqlCommand("""
                SELECT count(*) FROM events.query_performance_observation
                WHERE observation_key=decode(repeat('23',16),'hex');
                """, admin);
            Assert.Equal(0L, (long)(await unchanged.ExecuteScalarAsync())!);
            await using var removeSpoof = new NpgsqlCommand(
                $"DROP TABLE events.{historicalPartition};", admin);
            await removeSpoof.ExecuteNonQueryAsync();
            Assert.Equal(1, await beyond.ExecuteNonQueryAsync());
        }
        DateTimeOffset nextMidnight = historicalDay.AddDays(1);
        await using (var boundary = new NpgsqlCommand("""
            INSERT INTO events.query_performance_observation
              (collection_run_id,database_id,query_fingerprint,observation_key,
               source,source_state,interval_start,interval_end,observed_at,semantics)
            VALUES(@firstRun,5,decode(repeat('11',32),'hex'),decode(repeat('24',16),'hex'),
              'query_store','read_write',@historicalStart,@historicalEnd,
              now(),'query_store_interval')
            ON CONFLICT DO NOTHING;
            """, admin))
        {
            boundary.Parameters.AddWithValue("firstRun", firstRun);
            boundary.Parameters.AddWithValue("historicalStart", nextMidnight.AddHours(-1));
            boundary.Parameters.AddWithValue("historicalEnd", nextMidnight);
            Assert.Equal(1, await boundary.ExecuteNonQueryAsync());
            Assert.Equal(0, await boundary.ExecuteNonQueryAsync());
        }
        await using var history = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_observation_v3),
                   (SELECT count(*) FROM system.partition_registry
                    WHERE parent_schema='events'
                      AND parent_table='query_performance_observation_v3'
                      AND lifecycle_state='attached'),
                   (SELECT tableoid::regclass::text
                    FROM events.query_performance_observation_v3
                    WHERE observation_key=decode(repeat('24',16),'hex'));
            """, admin);
        await using NpgsqlDataReader historyReader = await history.ExecuteReaderAsync();
        Assert.True(await historyReader.ReadAsync());
        Assert.Equal(4L, historyReader.GetInt64(0));
        Assert.Equal(19L, historyReader.GetInt64(1));
        Assert.EndsWith($"p{nextMidnight:yyyyMMdd}", historyReader.GetString(2));
    }
}
