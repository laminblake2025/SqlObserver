using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class ServerWaitCategorySummaryPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromMinutes(2));

    [Fact]
    public async Task UpgradeKeepsHistoricalOutcomesUnknownAndUsesTheirRawWaitsAsBaseline()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult baseline = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(136, Timeout), CancellationToken.None);
        Assert.False(baseline.HasFailures);
        Guid target = Guid.NewGuid(), oldRun = Guid.NewGuid(), newRun = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await InsertTargetAsync(connection, target, start);
        await InsertRunAsync(connection, target, oldRun, start, "LCK_M_S", 10, 100);
        await InsertOutcomeAsync(connection, oldRun, start.AddSeconds(1), "succeeded");

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, Timeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(137, Assert.Single(upgrade.Results).Migration.Number.Value);
        await using (var oldSummary = new NpgsqlCommand("""
            SELECT server_wait_category_summary IS NULL
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """, connection))
        {
            oldSummary.Parameters.AddWithValue("run", oldRun);
            Assert.True(Assert.IsType<bool>(await oldSummary.ExecuteScalarAsync()));
        }

        await InsertRunAsync(connection, target, newRun, start.AddMinutes(1), "LCK_M_S", 11, 125);
        await InsertOutcomeAsync(connection, newRun, start.AddMinutes(1).AddSeconds(1), "succeeded");
        using JsonDocument summary = await ReadSummaryAsync(connection, newRun);
        Assert.Equal(oldRun.ToString(), summary.RootElement.GetProperty("baselineRunId").GetString());
        JsonElement category = Assert.Single(summary.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal("25", category.GetProperty("waitMilliseconds").GetString());
    }

    [Fact]
    public async Task CompletedRunStoresComparableCategoryDeltasAndMarksUnknowns()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult applied = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(137, Timeout), CancellationToken.None);
        Assert.False(applied.HasFailures);
        Assert.Equal(137, applied.Results.Count);

        Guid target = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid(), reset = Guid.NewGuid(), recovered = Guid.NewGuid();
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(
            DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds() / 300 * 300);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await InsertTargetAsync(connection, target, start);

        await InsertRunAsync(connection, target, first, start, "LCK_M_S", 10, 100);
        await InsertWaitAsync(connection, target, first, start, "SLEEP_TASK", 1, 50);
        await InsertOutcomeAsync(connection, first, start.AddSeconds(1), "succeeded");
        using (JsonDocument initial = await ReadSummaryAsync(connection, first))
        {
            JsonElement summary = initial.RootElement;
            Assert.Equal(1, summary.GetProperty("version").GetInt32());
            Assert.Equal(2, summary.GetProperty("sourceRows").GetInt32());
            Assert.Equal(1, summary.GetProperty("idleTypesOmitted").GetInt32());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("baselineRunId").ValueKind);
            JsonElement lockCategory = Assert.Single(summary.GetProperty("categories").EnumerateArray());
            Assert.Equal("Lock", lockCategory.GetProperty("category").GetString());
            Assert.Equal(JsonValueKind.Null, lockCategory.GetProperty("waitMilliseconds").ValueKind);
            Assert.Equal(1, lockCategory.GetProperty("incomparableTypes").GetInt32());
        }

        await InsertRunAsync(connection, target, second, start.AddMinutes(1), "LCK_M_S", 15, 150);
        await InsertWaitAsync(connection, target, second, start.AddMinutes(1), "WRITELOG", 2, 20);
        await InsertOutcomeAsync(connection, second, start.AddMinutes(1).AddSeconds(1), "partial");
        using (JsonDocument comparable = await ReadSummaryAsync(connection, second))
        {
            JsonElement summary = comparable.RootElement;
            Assert.Equal(first.ToString(), summary.GetProperty("baselineRunId").GetString());
            JsonElement[] categories = summary.GetProperty("categories").EnumerateArray().ToArray();
            JsonElement lockCategory = Assert.Single(categories, item => item.GetProperty("category").GetString() == "Lock");
            JsonElement logCategory = Assert.Single(categories, item => item.GetProperty("category").GetString() == "Log");
            Assert.Equal("50", lockCategory.GetProperty("waitMilliseconds").GetString());
            Assert.Equal(1, lockCategory.GetProperty("comparableTypes").GetInt32());
            Assert.Equal(JsonValueKind.Null, logCategory.GetProperty("waitMilliseconds").ValueKind);
            Assert.Equal(1, logCategory.GetProperty("incomparableTypes").GetInt32());
        }

        await InsertRunAsync(connection, target, reset, start.AddMinutes(2), "LCK_M_S", 1, 5);
        await InsertOutcomeAsync(connection, reset, start.AddMinutes(2).AddSeconds(1), "succeeded");
        using JsonDocument resetSummary = await ReadSummaryAsync(connection, reset);
        JsonElement resetCategory = Assert.Single(resetSummary.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, resetCategory.GetProperty("waitMilliseconds").ValueKind);
        Assert.Equal(1, resetCategory.GetProperty("incomparableTypes").GetInt32());

        await InsertRunAsync(connection, target, recovered, start.AddMinutes(5), "LCK_M_S", 2, 15);
        await InsertOutcomeAsync(connection, recovered, start.AddMinutes(5).AddSeconds(1), "succeeded");
        Guid otherTarget = Guid.NewGuid(), otherRun = Guid.NewGuid();
        await InsertTargetAsync(connection, otherTarget, start);
        await InsertRunAsync(connection, otherTarget, otherRun, start.AddMinutes(5), "LCK_M_S", 99, 999);
        await InsertOutcomeAsync(connection, otherRun, start.AddMinutes(5).AddSeconds(2), "succeeded");

        await using (var serverRole = new NpgsqlCommand("SET ROLE sqlobserver_server;", connection))
            await serverRole.ExecuteNonQueryAsync();
        await using (var trend = new NpgsqlCommand("""
            SELECT bucket_start, category, wait_ms, run_count, missing_summary_runs,
                   partial_runs, incomparable_types
            FROM reporting.list_server_wait_category_trend(@target,@from,@to)
            WHERE category='Lock' ORDER BY bucket_start;
            """, connection))
        {
            trend.Parameters.AddWithValue("target", target);
            trend.Parameters.AddWithValue("from", start.AddMinutes(-5));
            trend.Parameters.AddWithValue("to", start.AddMinutes(10));
            var rows = new List<(decimal? Wait, long Runs, long Missing, long Partial, long Unknown)>();
            await using NpgsqlDataReader reader = await trend.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
            Assert.Equal(3, rows.Count);
            Assert.Equal((null, 0, 0, 0, 0), rows[0]);
            Assert.Equal((null, 3, 0, 1, 2), rows[1]);
            Assert.Equal((10m, 1, 0, 0, 0), rows[2]);
        }
        await using (var resetRole = new NpgsqlCommand("RESET ROLE;", connection))
            await resetRole.ExecuteNonQueryAsync();

        await using var classes = new NpgsqlCommand("""
            SELECT telemetry.server_wait_category_v1(wait_type)
            FROM (VALUES ('PAGEIOLATCH_SH'),('SOS_SCHEDULER_YIELD'),
                         ('RESOURCE_SEMAPHORE'),('CXCONSUMER'),('WRITELOG'),('LCK_M_X')) AS source(wait_type);
            """, connection);
        var labels = new List<string>();
        await using (NpgsqlDataReader reader = await classes.ExecuteReaderAsync())
            while (await reader.ReadAsync()) labels.Add(reader.GetString(0));
        Assert.Equal(["I/O", "CPU/signal", "Memory", "Parallelism", "Log", "Lock"], labels);
    }

    private static async Task InsertTargetAsync(NpgsqlConnection connection, Guid target, DateTimeOffset start)
    {
        await using var targetCommand = new NpgsqlCommand("""
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port, connect_timeout,
                 authentication_mode, transport_security_mode, lifecycle_state, revision,
                 created_at, updated_at, discovery_requested_at)
            VALUES (@target, @key, 'Wait summary target', 'sql01', 1433, interval '5 seconds',
                    'windows_integrated_service_identity', 'mandatory_validated', 'active', 1,
                    @start, @start, @start);
            """, connection);
        targetCommand.Parameters.AddWithValue("target", target);
        targetCommand.Parameters.AddWithValue("key", $"wait.summary.{target:N}");
        targetCommand.Parameters.AddWithValue("start", start);
        await targetCommand.ExecuteNonQueryAsync();
    }

    private static async Task InsertRunAsync(NpgsqlConnection connection, Guid target, Guid run,
        DateTimeOffset observedAt, string waitType, long tasks, long waitMs)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run
                (run_id, instance_id, collector_id, collector_version, output_schema_version,
                 target_revision, schedule_revision, work_key, owner_execution_id, fencing_token,
                 request_digest, scheduled_for, started_at)
            VALUES (@run, @target, 'waits.server', 1, 1, 1, 1, @work, @owner, 1,
                    decode(repeat('aa',32),'hex'), @observed, @observed);
            INSERT INTO telemetry.server_wait_snapshot
                (observed_at, collection_run_id, instance_id, target_revision, wait_type,
                 waiting_tasks_count, wait_time_ms, maximum_wait_time_ms,
                 signal_wait_time_ms, collected_at)
            VALUES (@observed, @run, @target, 1, @waitType, @tasks, @waitMs, @waitMs, 0, @observed);
            """, connection);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", $"wait.summary.{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("observed", observedAt);
        command.Parameters.AddWithValue("waitType", waitType);
        command.Parameters.AddWithValue("tasks", tasks);
        command.Parameters.AddWithValue("waitMs", waitMs);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertWaitAsync(NpgsqlConnection connection, Guid target, Guid run,
        DateTimeOffset observedAt, string waitType, long tasks, long waitMs)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO telemetry.server_wait_snapshot
                (observed_at, collection_run_id, instance_id, target_revision, wait_type,
                 waiting_tasks_count, wait_time_ms, maximum_wait_time_ms,
                 signal_wait_time_ms, collected_at)
            VALUES (@observed, @run, @target, 1, @waitType, @tasks, @waitMs, @waitMs, 0, @observed);
            """, connection);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("observed", observedAt);
        command.Parameters.AddWithValue("waitType", waitType);
        command.Parameters.AddWithValue("tasks", tasks);
        command.Parameters.AddWithValue("waitMs", waitMs);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertOutcomeAsync(NpgsqlConnection connection, Guid run,
        DateTimeOffset completedAt, string outcome)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run_outcome
                (run_id, outcome, reason_code, attempt_count, retry_count, duration_ms,
                 source_row_count, output_item_count, inserted_item_count, duplicate_item_count,
                 rejected_item_count, response_bytes, output_bytes, persisted_bytes, truncated,
                 loss_detected, loss_kind, loss_count_exact, lost_row_count, lost_byte_count,
                 completion_digest, completed_at)
            VALUES (@run, @outcome,
                    CASE WHEN @outcome='partial' THEN 'source_row_limit' ELSE 'completed' END,
                    1, 0, 10, 2, 2, 2, 0, 0, 128, 128, 128,
                    @outcome='partial', @outcome='partial',
                    CASE WHEN @outcome='partial' THEN 'source_row_limit' ELSE 'none' END,
                    true, CASE WHEN @outcome='partial' THEN 1 ELSE 0 END, 0,
                    decode(repeat('bb',32),'hex'), @completed);
            """, connection);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("completed", completedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonDocument> ReadSummaryAsync(NpgsqlConnection connection, Guid run)
    {
        await using var command = new NpgsqlCommand("""
            SELECT server_wait_category_summary::text FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """, connection);
        command.Parameters.AddWithValue("run", run);
        return JsonDocument.Parse(Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }
}
