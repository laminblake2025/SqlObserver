using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryStoreGroupTransitionPostgreSqlTests
{
    private readonly PostgreSql18Fixture fixture;

    public QueryStoreGroupTransitionPostgreSqlTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    public void TypedPayloadIsCanonicalAndFailureEnvelopeContainsNoGroups()
    {
        Guid target = Guid.NewGuid();
        Guid incarnation = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QueryStoreGroupObservation first = TypedGroup(target, incarnation, now, 1);
        QueryStoreGroupObservation second = TypedGroup(target, incarnation, now, 2);
        var targetId = new MonitoredInstanceId(target);
        var revision = new ObservationTargetRevision(1);
        QueryStoreGroupOutputBatch Batch(params QueryStoreGroupObservation[] items) => new(
            targetId, revision, now.AddMinutes(-5), now, true,
            QueryStoreGroupWaitCoverage.Complete, items);

        Assert.Equal(QueryStoreGroupCommitPayload.Serialize(Batch(first, second)),
            QueryStoreGroupCommitPayload.Serialize(Batch(second, first)));
        QueryStoreGroupObservation off = new(TypedGroup(target, incarnation,
            now, 3, 7).Runtime, null);
        QueryStoreGroupOutputBatch Mixed(QueryStoreGroupObservation[] items,
            IReadOnlyDictionary<int, QueryStoreGroupWaitCoverage> coverage) => new(
                targetId, revision, now.AddMinutes(-5), now, true,
                QueryStoreGroupWaitCoverage.Mixed, items, coverage);
        byte[] canonicalMixed = QueryStoreGroupCommitPayload.Serialize(Mixed([first, off],
            new Dictionary<int, QueryStoreGroupWaitCoverage>
            {
                [6] = QueryStoreGroupWaitCoverage.Complete,
                [7] = QueryStoreGroupWaitCoverage.CaptureOff,
            }));
        Assert.Equal(canonicalMixed, QueryStoreGroupCommitPayload.Serialize(Mixed([off, first],
            new Dictionary<int, QueryStoreGroupWaitCoverage>
            {
                [7] = QueryStoreGroupWaitCoverage.CaptureOff,
                [6] = QueryStoreGroupWaitCoverage.Complete,
            })));
        using (JsonDocument mixed = JsonDocument.Parse(canonicalMixed))
        {
            Assert.Equal(2, mixed.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("capture_off", mixed.RootElement.GetProperty("databaseWaitCoverage")
                .GetProperty("7").GetString());
        }
        using JsonDocument failure = JsonDocument.Parse(
            QueryStoreGroupCommitPayload.SerializeFailure(targetId, revision,
                "permission_denied", "required_permission_missing"));
        Assert.Empty(failure.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("permission_denied", failure.RootElement.GetProperty("outcome").GetString());
        Assert.False(failure.RootElement.TryGetProperty("windowStartUtc", out _));
    }

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
            TypedPayload(target, incarnation, now)));
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

        Guid mixedIncarnation = Guid.NewGuid();
        QueryStoreGroupObservation completeGroup = TypedGroup(target, mixedIncarnation,
            now, 2, observedAt: now.AddSeconds(-1));
        QueryStoreGroupObservation captureOffGroup = new(
            TypedGroup(target, mixedIncarnation, now, 2, 7,
                observedAt: now.AddSeconds(-1)).Runtime, null);
        var mixedBatch = new QueryStoreGroupOutputBatch(new MonitoredInstanceId(target),
            new ObservationTargetRevision(1), now.AddMinutes(-5), now, true,
            QueryStoreGroupWaitCoverage.Mixed, [completeGroup, captureOffGroup],
            new Dictionary<int, QueryStoreGroupWaitCoverage>
            {
                [6] = QueryStoreGroupWaitCoverage.Complete,
                [7] = QueryStoreGroupWaitCoverage.CaptureOff,
            });
        string mixedPayload = Encoding.UTF8.GetString(
            QueryStoreGroupCommitPayload.Serialize(mixedBatch));
        Assert.Equal(2, await ApplyV2Async(connection, transaction, target, mixedPayload));

        await using (var evidence = new NpgsqlCommand("""
            SELECT runtime_transition,cpu_ms,wait_transition,wait_delta
            FROM events.query_store_group_delta ORDER BY observed_at,database_id;
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
            Assert.True(await reader.ReadAsync());
            Assert.Equal("baseline_unavailable", reader.GetString(0));
            Assert.Equal("baseline_unavailable", reader.GetString(2));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("baseline_unavailable", reader.GetString(0));
            Assert.Equal("unavailable", reader.GetString(2));
            Assert.False(await reader.ReadAsync());
        }

        await using (var save = new NpgsqlCommand("SAVEPOINT invalid_mixed_coverage;", connection, transaction))
            await save.ExecuteNonQueryAsync();
        string missingDatabase = mixedPayload.Replace("\"7\":\"capture_off\"",
            "\"8\":\"capture_off\"", StringComparison.Ordinal);
        Assert.NotEqual(mixedPayload, missingDatabase);
        PostgresException coverageError = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyV2Async(connection, transaction, target, missingDatabase));
        Assert.Equal("22023", coverageError.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT invalid_mixed_coverage;", connection, transaction))
            await rollback.ExecuteNonQueryAsync();
        await using (var save = new NpgsqlCommand("SAVEPOINT missing_group_database;", connection, transaction))
            await save.ExecuteNonQueryAsync();
        string missingId = mixedPayload.Replace("\"databaseId\":6", "\"databaseId\":null",
            StringComparison.Ordinal);
        Assert.NotEqual(mixedPayload, missingId);
        PostgresException missingIdError = await Assert.ThrowsAsync<PostgresException>(() =>
            ApplyV2Async(connection, transaction, target, missingId));
        Assert.Equal("22023", missingIdError.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT missing_group_database;", connection, transaction))
            await rollback.ExecuteNonQueryAsync();

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
            Assert.Equal(10L, await count.ExecuteScalarAsync());
        await transaction.RollbackAsync();

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection collectorConnection = await collector.OpenConnectionAsync();
        await using var privilege = new NpgsqlCommand("""
            SELECT has_function_privilege(current_user,
              'events.apply_query_store_group_batch(uuid,uuid,bigint,jsonb)'::regprocedure,'EXECUTE');
            """, collectorConnection);
        Assert.False((bool)(await privilege.ExecuteScalarAsync())!);
        await using var v2Privilege = new NpgsqlCommand("""
            SELECT has_function_privilege(current_user,
              'events.apply_query_store_group_batch_v2(uuid,uuid,bigint,jsonb)'::regprocedure,'EXECUTE');
            """, collectorConnection);
        Assert.False((bool)(await v2Privilege.ExecuteScalarAsync())!);
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

    private static async Task<int> ApplyV2Async(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid target, string payload)
    {
        await using var command = new NpgsqlCommand(
            "SELECT events.apply_query_store_group_batch_v2(@run,@target,1,@payload);",
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

    private static string TypedPayload(Guid target, Guid incarnation, DateTimeOffset now)
    {
        QueryStoreGroupObservation group = TypedGroup(target, incarnation, now, 1);
        QueryStoreRuntimeWatermarkKey key = group.Runtime.Key;
        var batch = new QueryStoreGroupOutputBatch(key.TargetId, key.TargetRevision,
            now.AddMinutes(-5), now, true, QueryStoreGroupWaitCoverage.Complete, [group]);
        string json = Encoding.UTF8.GetString(QueryStoreGroupCommitPayload.Serialize(batch));
        Assert.DoesNotContain("queryText", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("planXml", json, StringComparison.OrdinalIgnoreCase);
        return json;
    }

    private static QueryStoreGroupObservation TypedGroup(Guid target, Guid incarnation,
        DateTimeOffset now, long sourceIntervalId, int databaseId = 6,
        DateTimeOffset? observedAt = null)
    {
        DateTimeOffset observed = observedAt ?? now.AddMinutes(-4);
        var query = new QueryOpaqueIdentity(databaseId, new string('a', 64));
        var plan = new PlanOpaqueIdentity(query, new string('b', 64));
        var key = new QueryStoreRuntimeWatermarkKey(new MonitoredInstanceId(target),
            new ObservationTargetRevision(1), incarnation, query, plan,
            now.AddHours(-2), sourceIntervalId, now.AddHours(-1), now.AddHours(1), 0);
        var runtime = new QueryStoreRuntimeWatermark(key,
            new QueryStoreRuntimeCounters(100, 200, 100, 100, 100, 100),
            now.AddMinutes(-10), now.AddMinutes(-4), observed);
        var waits = new QueryStoreWaitWatermark(key,
            new Dictionary<int, long> { [3] = 100 }, observed);
        return new QueryStoreGroupObservation(runtime, waits);
    }
}
