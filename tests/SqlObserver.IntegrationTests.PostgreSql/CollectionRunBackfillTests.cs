using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class CollectionRunBackfillTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task HistoricalRunsCopyInBoundedBatchesAndDivergencePreservesCursor()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(
                MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))),
                CancellationToken.None);
        Assert.False(applied.HasFailures);

        Guid target = Guid.NewGuid();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), mirrored = Guid.NewGuid();
        DateTimeOffset start = new(DateTime.UtcNow.Date.AddDays(-20), TimeSpan.Zero);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target
              (instance_id,instance_key,display_name,host_name,tcp_port,
               connect_timeout,authentication_mode,transport_security_mode,
               lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Run backfill target','sql01',1433,
              interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,now(),now(),now());
            """, admin))
        {
            seed.Parameters.AddWithValue("target", target);
            await seed.ExecuteNonQueryAsync();
        }

        async Task InsertRunAsync(Guid run, DateTimeOffset time)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO telemetry.collection_run
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                VALUES(@run,@target,'queries.performance',1,1,1,1,
                  'test/run-backfill',gen_random_uuid(),1,
                  decode(repeat('00',32),'hex'),@time,@time);
                """, admin);
            insert.Parameters.AddWithValue("run", run);
            insert.Parameters.AddWithValue("target", target);
            insert.Parameters.AddWithValue("time", time);
            await insert.ExecuteNonQueryAsync();
        }

        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE telemetry.collection_run DISABLE TRIGGER collection_run_v2_mirror;", admin))
            await disable.ExecuteNonQueryAsync();
        await InsertRunAsync(first, start);
        await InsertRunAsync(second, start.AddDays(1));
        await using (var enable = new NpgsqlCommand(
            "ALTER TABLE telemetry.collection_run ENABLE TRIGGER collection_run_v2_mirror;", admin))
            await enable.ExecuteNonQueryAsync();
        await InsertRunAsync(mirrored, start.AddDays(2));

        // A conflicting pre-existing successor row must abort the batch and
        // leave the durable cursor untouched.
        await using (NpgsqlTransaction divergent = await admin.BeginTransactionAsync())
        {
            await using (var corrupt = new NpgsqlCommand("""
                SELECT control.ensure_collection_run_v2_day(
                  (started_at AT TIME ZONE 'UTC')::date)
                FROM telemetry.collection_run WHERE run_id=@run;
                INSERT INTO telemetry.collection_run_v2
                  (run_id,instance_id,collector_id,collector_version,
                   output_schema_version,target_revision,schedule_revision,
                   work_key,owner_execution_id,fencing_token,request_digest,
                   scheduled_for,started_at)
                SELECT run_id,instance_id,collector_id,collector_version,
                  output_schema_version,target_revision,schedule_revision,
                  'different-work-key',owner_execution_id,fencing_token,
                  request_digest,scheduled_for,started_at
                FROM telemetry.collection_run WHERE run_id=@run;
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, divergent))
            {
                corrupt.Parameters.AddWithValue("run", first);
                corrupt.Parameters.AddWithValue("target", target);
                await corrupt.ExecuteNonQueryAsync();
            }
            await using var reject = new NpgsqlCommand(
                "SELECT * FROM control.backfill_collection_run_v2(@target,1);",
                admin, divergent);
            reject.Parameters.AddWithValue("target", target);
            PostgresException mismatch = await Assert.ThrowsAsync<PostgresException>(
                () => reject.ExecuteNonQueryAsync());
            Assert.Equal("55000", mismatch.SqlState);
            await divergent.RollbackAsync();
        }

        async Task<(int Checked, int Copied, bool Complete)> BackfillAsync(int limit)
        {
            await using NpgsqlTransaction transaction = await admin.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("""
                SET LOCAL ROLE sqlobserver_migrator;
                SELECT set_config('sqlobserver.target_scope',@target::text,true);
                """, admin, transaction))
            {
                scope.Parameters.AddWithValue("target", target);
                await scope.ExecuteNonQueryAsync();
            }
            await using var command = new NpgsqlCommand(
                "SELECT * FROM control.backfill_collection_run_v2(@target,@limit);",
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
            SELECT (SELECT count(*) FROM telemetry.collection_run_v2
                    WHERE instance_id=@target),
                   (SELECT checked_rows FROM system.collection_run_v2_backfill
                    WHERE instance_id=@target),
                   (SELECT copied_rows FROM system.collection_run_v2_backfill
                    WHERE instance_id=@target),
                   (SELECT count(*) FROM telemetry.collection_run AS old_row
                    JOIN telemetry.collection_run_v2 AS new_row
                      USING(run_id,started_at)
                    WHERE old_row.instance_id=@target
                      AND to_jsonb(old_row) IS DISTINCT FROM to_jsonb(new_row)),
                   has_function_privilege('sqlobserver_collector',
                     'control.backfill_collection_run_v2(uuid,integer)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                     'control.backfill_collection_run_v2(uuid,integer)','EXECUTE'),
                   (SELECT indisvalid FROM pg_index WHERE indexrelid=
                     'telemetry.ix_collection_run_v2_backfill_order'::regclass);
            """, admin);
        inspect.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader results = await inspect.ExecuteReaderAsync();
        Assert.True(await results.ReadAsync());
        Assert.Equal(3L, results.GetInt64(0));
        Assert.Equal(3L, results.GetInt64(1));
        Assert.Equal(2L, results.GetInt64(2));
        Assert.Equal(0L, results.GetInt64(3));
        Assert.False(results.GetBoolean(4));
        Assert.False(results.GetBoolean(5));
        Assert.True(results.GetBoolean(6));
    }
}
