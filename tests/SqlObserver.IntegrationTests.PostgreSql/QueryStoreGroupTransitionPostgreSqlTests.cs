using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStoreGroupTransitionPostgreSqlTests
{
    private readonly PostgreSql18Fixture fixture;

    public QueryStoreGroupTransitionPostgreSqlTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task PrivateBatchWriterPublishesOnlyComparableDeltasAndRollsBackInvalidBatches()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        Guid target = Guid.NewGuid();
        Guid incarnation = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("""
            SET LOCAL ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@target,true);
            """, connection, transaction))
        {
            scope.Parameters.AddWithValue("target", target.ToString());
            await scope.ExecuteNonQueryAsync();
        }

        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddMinutes(-4), 100,
                new Dictionary<string, long> { ["3"] = 100 })));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddMinutes(-3), 110,
                new Dictionary<string, long> { ["3"] = 150, ["4"] = 7 })));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddMinutes(-2), 110,
                new Dictionary<string, long> { ["3"] = 20 })));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddMinutes(-1), 5,
                new Dictionary<string, long> { ["3"] = 3 })));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddSeconds(-45), 6,
                null, coverage: "capture_off")));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddSeconds(-30), 7,
                new Dictionary<string, long> { ["3"] = 5 })));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddSeconds(-20), 8,
                new Dictionary<string, long> { ["3"] = 50 },
                waitObserved: now.AddSeconds(-30))));
        Assert.Equal(1, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddSeconds(-15), 9,
                new Dictionary<string, long> { ["3"] = 6 }, freshEpoch: true)));
        Assert.Equal(0, await ApplyAsync(connection, transaction, target,
            Payload(target, incarnation, now, now.AddSeconds(-15), 900,
                new Dictionary<string, long> { ["3"] = 900 })));

        await using (var evidence = new NpgsqlCommand("""
            SELECT runtime_transition,cpu_ms,wait_transition,wait_delta
            FROM events.query_store_group_delta ORDER BY observed_at;
            """, connection, transaction))
        await using (NpgsqlDataReader reader = await evidence.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("baseline_unavailable", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("baseline_unavailable", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("comparable", reader.GetString(0));
            Assert.Equal(10L, reader.GetInt64(1));
            Assert.Equal("comparable", reader.GetString(2));
            using (JsonDocument wait = JsonDocument.Parse(reader.GetString(3)))
            {
                Assert.Equal(50L, wait.RootElement.GetProperty("3").GetInt64());
                Assert.Equal(7L, wait.RootElement.GetProperty("4").GetInt64());
            }

            Assert.True(await reader.ReadAsync());
            Assert.Equal("comparable", reader.GetString(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal("reset", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("reset", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("baseline_unavailable", reader.GetString(2));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("comparable", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("unavailable", reader.GetString(2));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("comparable", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("comparable", reader.GetString(2));
            using (JsonDocument resumed = JsonDocument.Parse(reader.GetString(3)))
                Assert.Equal(2L, resumed.RootElement.GetProperty("3").GetInt64());

            Assert.True(await reader.ReadAsync());
            Assert.Equal("comparable", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal("stale", reader.GetString(2));
            Assert.True(reader.IsDBNull(3));

            Assert.True(await reader.ReadAsync());
            Assert.Equal("epoch_ambiguous", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal("baseline_unavailable", reader.GetString(2));
            Assert.False(await reader.ReadAsync());
        }

        await using (var save = new NpgsqlCommand("SAVEPOINT invalid_group_batch;", connection, transaction))
            await save.ExecuteNonQueryAsync();
        string duplicate = Payload(target, incarnation, now, now.AddSeconds(-30), 6,
            new Dictionary<string, long> { ["3"] = 4 }, duplicate: true);
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(connection, transaction, target, duplicate));
        Assert.Equal("22023", error.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT invalid_group_batch;", connection, transaction))
            await rollback.ExecuteNonQueryAsync();
        await using (var save = new NpgsqlCommand("SAVEPOINT wrong_target;", connection, transaction))
            await save.ExecuteNonQueryAsync();
        PostgresException scopeError = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyAsync(connection, transaction, Guid.NewGuid(),
                Payload(target, incarnation, now, now.AddSeconds(-10), 10,
                    new Dictionary<string, long> { ["3"] = 7 })));
        Assert.Equal("22023", scopeError.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT wrong_target;", connection, transaction))
            await rollback.ExecuteNonQueryAsync();
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM events.query_store_group_delta;", connection, transaction))
            Assert.Equal(8L, await count.ExecuteScalarAsync());
        await transaction.RollbackAsync();

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege(current_user,
              'events.apply_query_store_group_batch(uuid,uuid,bigint,jsonb)'::regprocedure,'EXECUTE');
            """, collectorConnection);
        Assert.False((bool)(await privilege.ExecuteScalarAsync())!);
    }

    private static async Task<int> ApplyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid target, string payload)
    {
        await using var command = new NpgsqlCommand(
            "SELECT events.apply_query_store_group_batch(@run,@target,1,@payload);",
            connection, transaction);
        command.Parameters.AddWithValue("run", Guid.NewGuid());
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static string Payload(Guid target, Guid incarnation, DateTimeOffset now,
        DateTimeOffset observed, long cpu,
        Dictionary<string, long>? categories, bool duplicate = false,
        string coverage = "complete", bool freshEpoch = false,
        DateTimeOffset? waitObserved = null)
    {
        static string Utc(DateTimeOffset value) =>
            value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var item = new Dictionary<string, object?>
        {
            ["databaseId"] = 6,
            ["databaseIncarnation"] = incarnation,
            ["queryFingerprint"] = new string('a', 64),
            ["planFingerprint"] = new string('b', 64),
            ["planInitialCompileUtc"] = Utc(now.AddHours(-2)),
            ["sourceIntervalId"] = 1,
            ["intervalStartUtc"] = Utc(now.AddHours(-1)),
            ["intervalEndUtc"] = Utc(now.AddHours(1)),
            ["executionType"] = 0,
            ["cpuMs"] = cpu,
            ["durationMs"] = cpu * 2,
            ["executionCount"] = cpu,
            ["logicalReads"] = cpu,
            ["writes"] = cpu,
            ["rowsProcessed"] = cpu,
            ["firstExecutionUtc"] = Utc(now.AddMinutes(freshEpoch ? -3 : -10)),
            ["lastExecutionUtc"] = Utc(now.AddMinutes(freshEpoch ? -2 : -4)),
            ["runtimeObservedUtc"] = Utc(observed),
        };
        if (categories is not null)
        {
            item["waitCategories"] = categories;
            item["waitObservedUtc"] = Utc(waitObserved ?? observed);
        }
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            targetId = target,
            targetRevision = 1,
            windowStartUtc = Utc(now.AddMinutes(-5)),
            windowEndUtc = Utc(now),
            runtimeComplete = true,
            waitCoverage = coverage,
            items = duplicate ? new[] { item, item } : new[] { item },
        });
    }
}
