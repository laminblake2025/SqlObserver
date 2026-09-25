using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerWaitHistoryIndexMigrationTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromMinutes(2));

    [Fact]
    public async Task PartialPartitionBuildResumesAndFuturePartitionsInheritTheIndex()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult baseline = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(135, Timeout), CancellationToken.None);
        Assert.False(baseline.HasFailures);
        Assert.Equal(135, baseline.Results.Count);

        await using (NpgsqlConnection setup = await database.DataSource.OpenConnectionAsync())
        {
            await ExecuteAsync(setup, "SET ROLE sqlobserver_migrator;");
            await ExecuteAsync(setup, "CREATE INDEX ix_server_wait_target_history ON ONLY telemetry.server_wait_snapshot (instance_id, observed_at, collection_run_id, wait_type);");
            string child = Assert.IsType<string>(await ScalarAsync(setup, """
                SELECT child.relname FROM pg_inherits AS link
                JOIN pg_class AS child ON child.oid=link.inhrelid
                WHERE link.inhparent='telemetry.server_wait_snapshot'::regclass
                ORDER BY child.relname LIMIT 1;
                """));
            string childIndex = $"ix_server_wait_target_history_{child[^8..]}";
            await ExecuteAsync(setup, $"CREATE INDEX CONCURRENTLY {childIndex} ON telemetry.{child} (instance_id, observed_at, collection_run_id, wait_type);");
            await ExecuteAsync(setup, $"ALTER INDEX telemetry.ix_server_wait_target_history ATTACH PARTITION telemetry.{childIndex};");
        }

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(136, Assert.Single(upgrade.Results).Migration.Number.Value);
        await AssertCompleteIndexAsync(database);

        await using NpgsqlConnection future = await database.DataSource.OpenConnectionAsync();
        await ExecuteAsync(future, "SELECT control.ensure_activity_daily_partitions(current_date+2);");
        await AssertCompleteIndexAsync(database);
        MigrationBatchResult repeat = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.Empty(repeat.Results);
    }

    [Fact]
    public async Task MixedFleetHistoryPageUsesTargetAndTimeIndex()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(136, Timeout), CancellationToken.None);
        Assert.False(applied.HasFailures);
        DateTimeOffset from = DateTimeOffset.UtcNow.AddHours(-1);
        Guid requestedTarget = Guid.Empty;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        for (int targetNumber = 0; targetNumber < 12; targetNumber++)
        {
            Guid target = Guid.NewGuid(), run = Guid.NewGuid();
            if (targetNumber == 0) requestedTarget = target;
            await using var seed = new NpgsqlCommand("""
                INSERT INTO control.observation_target
                    (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                     authentication_mode, transport_security_mode, lifecycle_state, revision,
                     created_at, updated_at, discovery_requested_at)
                VALUES (@target, @key, 'Wait plan target', 'sql01', 1433, interval '5 seconds',
                        'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                        @from, @from, @from);
                INSERT INTO telemetry.collection_run
                    (run_id, instance_id, collector_id, collector_version, output_schema_version,
                     target_revision, schedule_revision, work_key, owner_execution_id, fencing_token,
                     request_digest, scheduled_for, started_at)
                VALUES (@run, @target, 'waits.server', 1, 1, 1, 1, @key, @owner, 1,
                        decode(repeat('aa',32),'hex'), @from, @from);
                INSERT INTO telemetry.collection_run_outcome
                    (run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
                     source_row_count, output_item_count, inserted_item_count, duplicate_item_count,
                     rejected_item_count, response_bytes, output_bytes, persisted_bytes, truncated,
                     loss_detected, loss_kind, loss_count_exact, lost_row_count, lost_byte_count,
                     completion_digest, completed_at)
                VALUES (@run, 'succeeded', 'completed', 1, 0, 10, 2500, 2500, 2500,
                        0, 0, 128, 128, 128, false, false, 'none', true, 0, 0,
                        decode(repeat('bb',32),'hex'), @from + interval '42 minutes');
                INSERT INTO telemetry.server_wait_snapshot
                    (observed_at, collection_run_id, instance_id, target_revision, wait_type,
                     waiting_tasks_count, wait_time_ms, maximum_wait_time_ms,
                     signal_wait_time_ms, collected_at)
                SELECT @from + sample * interval '1 second', @run, @target, 1,
                       'LCK_M_S', sample, sample, sample, 0,
                       @from + sample * interval '1 second'
                FROM generate_series(0,2499) AS sample;
                """, connection);
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("run", run);
            seed.Parameters.AddWithValue("owner", Guid.NewGuid());
            seed.Parameters.AddWithValue("key", $"wait.plan.{target:N}");
            seed.Parameters.AddWithValue("from", from);
            await seed.ExecuteNonQueryAsync();
        }
        await ExecuteAsync(connection, "ANALYZE telemetry.server_wait_snapshot;");
        await using var plan = new NpgsqlCommand("""
            EXPLAIN (FORMAT TEXT)
            SELECT observed_at, collection_run_id, wait_type
            FROM telemetry.server_wait_snapshot
            WHERE instance_id=@target AND observed_at>=@from
              AND observed_at<@to
            ORDER BY observed_at DESC, collection_run_id DESC, wait_type DESC
            LIMIT 25;
            """, connection);
        plan.Parameters.AddWithValue("target", requestedTarget);
        plan.Parameters.AddWithValue("from", from);
        plan.Parameters.AddWithValue("to", from.AddHours(1));
        var lines = new List<string>();
        await using NpgsqlDataReader reader = await plan.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        Assert.Contains(lines, line => line.Contains("ix_server_wait_target_history_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnrelatedParentIndexCollisionFailsWithoutAdvancingTheLedger()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult baseline = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(135, Timeout), CancellationToken.None);
        Assert.False(baseline.HasFailures);
        await using (NpgsqlConnection setup = await database.DataSource.OpenConnectionAsync())
        {
            await ExecuteAsync(setup, "SET ROLE sqlobserver_migrator;");
            await ExecuteAsync(setup, "CREATE INDEX ix_server_wait_target_history ON ONLY telemetry.server_wait_snapshot (instance_id, wait_type);");
        }
        MigrationBatchResult failed = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.True(failed.HasFailures);
        Assert.Equal(136, Assert.Single(failed.Results).Migration.Number.Value);
        await using (NpgsqlConnection check = await database.DataSource.OpenConnectionAsync())
        {
            Assert.Equal(135L, await ScalarAsync(check, "SELECT count(*) FROM system.schema_migration;"));
            Assert.Contains("(instance_id, wait_type)",
                Assert.IsType<string>(await ScalarAsync(check,
                    "SELECT pg_get_indexdef('telemetry.ix_server_wait_target_history'::regclass);")),
                StringComparison.Ordinal);
            await ExecuteAsync(check, "SET ROLE sqlobserver_migrator;");
            await ExecuteAsync(check, "DROP INDEX telemetry.ix_server_wait_target_history;");
        }
        MigrationBatchResult retry = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(retry.HasFailures);
        await AssertCompleteIndexAsync(database);
    }

    private static async Task AssertCompleteIndexAsync(RepositoryTestDatabase database)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT parent_index.indisvalid AND parent_index.indisready,
                   (SELECT count(*) FROM pg_inherits AS table_link
                    WHERE table_link.inhparent='telemetry.server_wait_snapshot'::regclass),
                   (SELECT count(*) FROM pg_inherits AS index_link
                    WHERE index_link.inhparent='telemetry.ix_server_wait_target_history'::regclass)
            FROM pg_index AS parent_index
            WHERE parent_index.indexrelid='telemetry.ix_server_wait_target_history'::regclass;
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
