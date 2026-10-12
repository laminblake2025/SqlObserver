using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunPartitionedMirrorTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task NewRunMirrorsExactlyAndSpoofedDayRollsBackOriginal()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid(), blockedRun = Guid.NewGuid();
        DateTimeOffset started = new(DateTime.UtcNow.Date.AddDays(-20), TimeSpan.Zero);
        DateTimeOffset blockedDay = started.AddDays(-1);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var targetInsert = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Run mirror target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            targetInsert.Parameters.AddWithValue("target", target);
            await targetInsert.ExecuteNonQueryAsync();
        }

        async Task InsertRunAsync(Guid runId, DateTimeOffset time)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using var insert = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                VALUES(@run,@target,'queries.performance',1,1,1,1,
                  'test/run-mirror',gen_random_uuid(),1,
                  decode(repeat('00',32),'hex'),@time,@time);
                """, admin, transaction);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("time", time);
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await InsertRunAsync(run, started);
        await using (var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run
                    WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2
                    WHERE run_id=@run),
                   (SELECT to_jsonb(original)=to_jsonb(successor)
                    FROM telemetry.collection_run AS original
                    JOIN telemetry.collection_run_v2 AS successor
                      ON successor.run_id=original.run_id
                     AND successor.started_at=original.started_at
                    WHERE original.run_id=@run),
                   (SELECT count(*) FROM system.partition_registry
                    WHERE parent_schema='telemetry'
                      AND parent_table='collection_run_v2'
                      AND range_start=@time
                      AND lifecycle_state='attached');
            """, admin))
        {
            inspect.Parameters.AddWithValue("run", run);
            inspect.Parameters.AddWithValue("time", started);
            await using NpgsqlDataReader rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(1L, rows.GetInt64(0));
            Assert.Equal(1L, rows.GetInt64(1));
            Assert.True(rows.GetBoolean(2));
            Assert.Equal(1L, rows.GetInt64(3));
        }

        string occupied = $"collection_run_v2_p{blockedDay:yyyyMMdd}";
        await using (var spoof = new NpgsqlCommand(
            $"CREATE TABLE telemetry.{occupied}(occupied boolean);", admin))
            await spoof.ExecuteNonQueryAsync();
        PostgresException obstruction = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRunAsync(blockedRun, blockedDay));
        Assert.Equal("55000", obstruction.SqlState);
        await using (var rolledBack = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run);
            """, admin))
        {
            rolledBack.Parameters.AddWithValue("run", blockedRun);
            await using NpgsqlDataReader rows = await rolledBack.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(0L, rows.GetInt64(0));
            Assert.Equal(0L, rows.GetInt64(1));
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection worker = await collector.OpenConnectionAsync();
        await using var grants = new NpgsqlCommand("""
            SELECT has_table_privilege(current_user,'telemetry.collection_run_v2',
                     'SELECT'),
                   has_function_privilege(current_user,
                     'control.ensure_collection_run_v2_partitions(date)',
                     'EXECUTE');
            """, worker);
        await using NpgsqlDataReader privileges = await grants.ExecuteReaderAsync();
        Assert.True(await privileges.ReadAsync());
        Assert.False(privileges.GetBoolean(0));
        Assert.True(privileges.GetBoolean(1));
    }
}
