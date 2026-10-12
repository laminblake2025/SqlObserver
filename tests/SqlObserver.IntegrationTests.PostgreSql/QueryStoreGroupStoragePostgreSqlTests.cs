using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStoreGroupStoragePostgreSqlTests
{
    private readonly PostgreSql18Fixture fixture;

    public QueryStoreGroupStoragePostgreSqlTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task GroupStateAndDeltaArePartitionedScopedAndUnavailableToRuntimeRoles()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var catalog = new NpgsqlCommand("""
            SELECT count(*) = 2,
                   bool_and(c.relkind = 'p' AND c.relrowsecurity AND c.relforcerowsecurity),
                   bool_and(NOT has_table_privilege('sqlobserver_collector',c.oid,'SELECT,INSERT,UPDATE,DELETE')),
                   bool_and(NOT has_table_privilege('sqlobserver_server',c.oid,'SELECT,INSERT,UPDATE,DELETE')),
                   (SELECT count(*) >= 32 FROM pg_inherits AS i
                    WHERE i.inhparent IN ('events.query_store_group_watermark'::regclass,
                                          'events.query_store_group_delta'::regclass)),
                   has_function_privilege('sqlobserver_collector',
                     'control.ensure_query_store_group_partitions(date)'::regprocedure,'EXECUTE'),
                   NOT has_function_privilege('sqlobserver_server',
                     'control.ensure_query_store_group_partitions(date)'::regprocedure,'EXECUTE'),
                   (SELECT count(*) = 32 FROM system.partition_registry AS registry
                    WHERE registry.parent_schema='events' AND registry.lifecycle_state='attached'
                      AND registry.parent_table IN
                        ('query_store_group_watermark','query_store_group_delta')),
                   (SELECT count(*) = 2 AND bool_and(policy.enabled AND
                       ((policy.data_class='m7_group_watermarks' AND
                         policy.parent_table='query_store_group_watermark' AND
                         policy.retain_for=interval '8 days') OR
                        (policy.data_class='m7_group_deltas' AND
                         policy.parent_table='query_store_group_delta' AND
                         policy.retain_for=interval '30 days')))
                    FROM system.retention_policy AS policy
                    WHERE policy.data_class IN ('m7_group_watermarks','m7_group_deltas'))
            FROM pg_class AS c
            WHERE c.oid IN ('events.query_store_group_watermark'::regclass,
                            'events.query_store_group_delta'::regclass);
            """, connection))
        await using (NpgsqlDataReader reader = await catalog.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            for (int ordinal = 0; ordinal < 9; ordinal++) Assert.True(reader.GetBoolean(ordinal));
        }

        await using (var maintain = new NpgsqlCommand(
            "SELECT control.ensure_query_store_group_partitions(current_date);", connection))
            Assert.Equal(0, await maintain.ExecuteScalarAsync());

        Guid targetA = Guid.NewGuid();
        Guid targetB = Guid.NewGuid();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@target,true);
            """, connection, transaction))
        {
            scope.Parameters.AddWithValue("target", targetA.ToString());
            await scope.ExecuteNonQueryAsync();
        }
        await using (var write = new NpgsqlCommand("""
            INSERT INTO events.query_store_group_watermark
              (instance_id,target_revision,database_id,database_incarnation,
               query_fingerprint,plan_fingerprint,plan_initial_compile_at,
               source_interval_id,interval_start,interval_end,execution_type,
               cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed,
               first_execution_at,last_execution_at,runtime_observed_at,last_run_id)
            VALUES (@target,1,6,@incarnation,decode(repeat('a',64),'hex'),
              decode(repeat('b',64),'hex'),clock_timestamp()-interval '1 hour',
              1,clock_timestamp()-interval '1 hour',clock_timestamp()+interval '1 hour',0,
              100,200,1,20,3,1,clock_timestamp()-interval '1 minute',
              clock_timestamp(),clock_timestamp(),@run);
            INSERT INTO events.query_store_group_delta
              (observed_at,run_id,instance_id,target_revision,database_id,
               database_incarnation,query_fingerprint,plan_fingerprint,
               plan_initial_compile_at,source_interval_id,interval_start,
               interval_end,execution_type,source_window_start,source_window_end,
               runtime_transition,wait_transition)
            VALUES (clock_timestamp(),@run,@target,1,6,@incarnation,
              decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),
              clock_timestamp()-interval '1 hour',1,
              clock_timestamp()-interval '1 hour',clock_timestamp()+interval '1 hour',0,
              clock_timestamp()-interval '1 hour',clock_timestamp(),
              'baseline_unavailable','unavailable');
            """, connection, transaction))
        {
            write.Parameters.AddWithValue("target", targetA);
            write.Parameters.AddWithValue("incarnation", Guid.NewGuid());
            write.Parameters.AddWithValue("run", Guid.NewGuid());
            await write.ExecuteNonQueryAsync();
        }
        await using (var own = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_store_group_watermark),
                   (SELECT count(*) FROM events.query_store_group_delta);
            """, connection, transaction))
        await using (NpgsqlDataReader reader = await own.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
        await using (var savepoint = new NpgsqlCommand("SAVEPOINT invalid_wait;", connection, transaction))
            await savepoint.ExecuteNonQueryAsync();
        await using (var invalidWait = new NpgsqlCommand("""
            UPDATE events.query_store_group_watermark
            SET wait_categories = '{}'::jsonb, wait_observed_at = NULL;
            """, connection, transaction))
        {
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(
                () => invalidWait.ExecuteNonQueryAsync());
            Assert.Equal("23514", error.SqlState);
        }
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT invalid_wait;", connection, transaction))
            await rollback.ExecuteNonQueryAsync();
        await using (var otherScope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction))
        {
            otherScope.Parameters.AddWithValue("target", targetB.ToString());
            await otherScope.ExecuteNonQueryAsync();
        }
        await using (var other = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_store_group_watermark),
                   (SELECT count(*) FROM events.query_store_group_delta);
            """, connection, transaction))
        await using (NpgsqlDataReader reader = await other.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }
        await transaction.RollbackAsync();

        await using NpgsqlTransaction policyTransaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_server;
            SELECT set_config('sqlobserver.role','SecurityAdministrator',true);
            SELECT set_config('sqlobserver.authorization_scope','global',true);
            """, connection, policyTransaction))
            await role.ExecuteNonQueryAsync();
        foreach (string dataClass in new[] { "m7_group_watermarks", "m7_group_deltas" })
        {
            await using var update = new NpgsqlCommand("""
                SELECT system.update_m10_retention_policy(
                    @data_class,true,interval '9 days',3,1,'storage-test','policy update');
                """, connection, policyTransaction);
            update.Parameters.AddWithValue("data_class", dataClass);
            Assert.Equal(2L, await update.ExecuteScalarAsync());
        }
        await policyTransaction.RollbackAsync();
    }
}
