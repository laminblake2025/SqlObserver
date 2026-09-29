using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunIdentityBridgeTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task NewRunsCreateUniqueIdentityWithoutMovingExistingChildReferences()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        DateTimeOffset started = new(DateTime.UtcNow.Date, TimeSpan.Zero);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Run identity target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run
              (run_id,instance_id,collector_id,collector_version,
               output_schema_version,target_revision,schedule_revision,
               work_key,owner_execution_id,fencing_token,request_digest,
               scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,
              'test/run-identity',gen_random_uuid(),1,
              decode(repeat('00',32),'hex'),@time,@time);
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("time", started);
            await seed.ExecuteNonQueryAsync();
        }

        await using (var inspect = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM telemetry.collection_run_v2
                    WHERE run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_identity
                    WHERE run_id=@run AND started_at=@time),
                   (SELECT count(*) FROM pg_constraint AS fk
                    WHERE fk.contype='f' AND fk.conparentid=0
                      AND fk.confrelid='telemetry.collection_run'::regclass),
                   (SELECT count(*) FROM pg_constraint AS fk
                    WHERE fk.contype='f'
                      AND fk.conrelid='telemetry.collection_run_identity'::regclass
                      AND fk.confrelid='telemetry.collection_run_v2'::regclass),
                   (SELECT relrowsecurity AND relforcerowsecurity
                    FROM pg_class WHERE oid=
                      'telemetry.collection_run_identity'::regclass),
                   has_table_privilege('sqlobserver_server',
                     'telemetry.collection_run_identity','SELECT'),
                   has_table_privilege('sqlobserver_collector',
                     'telemetry.collection_run_identity','SELECT');
            """, admin))
        {
            inspect.Parameters.AddWithValue("run", run);
            inspect.Parameters.AddWithValue("time", started);
            await using NpgsqlDataReader rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(1L, rows.GetInt64(0));
            Assert.Equal(1L, rows.GetInt64(1));
            Assert.Equal(20L, rows.GetInt64(2));
            Assert.Equal(1L, rows.GetInt64(3));
            Assert.True(rows.GetBoolean(4));
            Assert.False(rows.GetBoolean(5));
            Assert.False(rows.GetBoolean(6));
        }

        // A successor row in another UTC partition cannot reuse the run ID.
        DateTimeOffset later = started.AddDays(1);
        await using (NpgsqlTransaction duplicate = await admin.BeginTransactionAsync())
        {
            await using var reuse = new NpgsqlCommand("""
                INSERT INTO telemetry.collection_run_v2
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT run_id,instance_id,collector_id,collector_version,
                  output_schema_version,target_revision,schedule_revision,
                  work_key,owner_execution_id,fencing_token,request_digest,
                  @later,@later
                FROM telemetry.collection_run WHERE run_id=@run;
                """, admin, duplicate);
            reuse.Parameters.AddWithValue("run", run);
            reuse.Parameters.AddWithValue("later", later);
            PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(
                () => reuse.ExecuteNonQueryAsync());
            Assert.Equal("23505", conflict.SqlState);
            await duplicate.RollbackAsync();
        }
        await using (var unchanged = new NpgsqlCommand(
            "SELECT count(*) FROM telemetry.collection_run_v2 WHERE run_id=@run;",
            admin))
        {
            unchanged.Parameters.AddWithValue("run", run);
            Assert.Equal(1L, (long)(await unchanged.ExecuteScalarAsync())!);
        }

        await using (NpgsqlTransaction mutation = await admin.BeginTransactionAsync())
        {
            await using var update = new NpgsqlCommand("""
                UPDATE telemetry.collection_run_identity
                SET started_at=@later WHERE run_id=@run;
                """, admin, mutation);
            update.Parameters.AddWithValue("run", run);
            update.Parameters.AddWithValue("later", later);
            PostgresException immutable = await Assert.ThrowsAsync<PostgresException>(
                () => update.ExecuteNonQueryAsync());
            Assert.Equal("55000", immutable.SqlState);
            await mutation.RollbackAsync();
        }

        string partition = $"collection_run_v2_p{started:yyyyMMdd}";
        await using (NpgsqlTransaction detach = await admin.BeginTransactionAsync())
        {
            await using var remove = new NpgsqlCommand(
                $"ALTER TABLE telemetry.collection_run_v2 DETACH PARTITION telemetry.{partition};",
                admin, detach);
            PostgresException referenced = await Assert.ThrowsAsync<PostgresException>(
                () => remove.ExecuteNonQueryAsync());
            Assert.Equal("23503", referenced.SqlState);
            await detach.RollbackAsync();
        }
    }
}
