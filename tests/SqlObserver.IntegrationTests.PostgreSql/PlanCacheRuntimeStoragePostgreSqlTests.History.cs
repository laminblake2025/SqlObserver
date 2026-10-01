using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using System.Text.Json;
using Npgsql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests
{
    [Fact]
    public async Task CacheHistorySnapshotExcludesLateCommitsAndPreservesUnknownAndBoundaryIntervals()
    {
        await using var database = await Migrate();
        Guid target = Guid.NewGuid();
        DateTimeOffset at = new(DateTime.UtcNow.AddSeconds(-20).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            foreach (var frame in new[] { Payload(target, at, Row(1), Row(2)),
                Payload(target, at.AddSeconds(1), Row(1, 250), Row(2, 250)),
                Payload(target, at.AddSeconds(2), Row(1, -2), Row(2, -2)) })
                Assert.Equal(2, await Apply(connection, transaction, target, frame));
            await transaction.CommitAsync();
        }
        await using var server = database.CreateServerDataSource();
        using JsonDocument first = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5));
        Assert.Equal(6, first.RootElement.GetProperty("totalRows").GetInt64());
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.All(first.RootElement.GetProperty("items").EnumerateArray(), row =>
        {
            Assert.Equal("reset", row.GetProperty("transition").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("executions").ValueKind);
        });
        await using var lateConnection = await database.DataSource.OpenConnectionAsync();
        await using var lateTransaction = await lateConnection.BeginTransactionAsync();
        await Scope(lateConnection, lateTransaction, target);
        // The later capture is assigned its sequence inside a subtransaction.
        // A source timestamp inside the window must not admit it to older pages.
        await Execute(lateConnection, lateTransaction, "SAVEPOINT late_capture;");
        Assert.Equal(2, await Apply(lateConnection, lateTransaction, target,
            Payload(target, at.AddSeconds(3), Row(1, 500), Row(2, 500))));
        await lateTransaction.CommitAsync();
        var cursor = HistoryCursor(first.RootElement);
        using JsonDocument second = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), cursor);
        Assert.All(second.RootElement.GetProperty("items").EnumerateArray(), row =>
        {
            Assert.Equal("comparable", row.GetProperty("transition").GetString());
            Assert.Equal("250", row.GetProperty("executions").GetString());
            Assert.Equal(0.25m, decimal.Parse(row.GetProperty("cpuMilliseconds").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        });
        using JsonDocument third = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), HistoryCursor(second.RootElement));
        Assert.False(third.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.All(third.RootElement.GetProperty("items").EnumerateArray(), row => Assert.Equal("baseline_unavailable", row.GetProperty("transition").GetString()));
        var keys = new[] { first, second, third }.SelectMany(page => page.RootElement.GetProperty("items").EnumerateArray())
            .Select(row => (row.GetProperty("collectionRunId").GetGuid(), row.GetProperty("cacheEntryFingerprint").GetString())).ToArray();
        Assert.Equal(6, keys.Distinct().Count());
        using JsonDocument fresh = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5));
        Assert.Equal(8, fresh.RootElement.GetProperty("totalRows").GetInt64());
        using JsonDocument boundary = await CacheHistory(server, target, target, at.AddMilliseconds(500), at.AddSeconds(2));
        Assert.All(boundary.RootElement.GetProperty("items").EnumerateArray(), row =>
        {
            Assert.Equal("window_boundary", row.GetProperty("transition").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("cpuMilliseconds").ValueKind);
            Assert.True(row.GetProperty("intervalFromUtc").GetDateTimeOffset() < at.AddMilliseconds(500));
        });
        using JsonDocument otherIncarnation = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), incarnation: Digest(99));
        Assert.Equal(0, otherIncarnation.RootElement.GetProperty("totalRows").GetInt64());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
        { using var denied = await CacheHistory(server, Guid.NewGuid(), target, at.AddSeconds(-1), at.AddSeconds(5)); })).SqlState);
        var expired = HistoryCursor(first.RootElement);
        expired["snapshotUtc"] = DateTimeOffset.UtcNow.AddMinutes(-31).ToString("O");
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
        { using var old = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), expired); })).SqlState);
        cursor["totalRows"] = 7;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
        { using var drift = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), cursor); })).SqlState);
        cursor["totalRows"] = 6;
        await using (var revise = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;"))
        { revise.Parameters.AddWithValue("target", target); await revise.ExecuteNonQueryAsync(); }
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
        { using var drift = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), cursor); })).SqlState);
        await using (var retire = database.DataSource.CreateCommand($"UPDATE control.observation_target SET revision=1 WHERE instance_id=@target; ALTER TABLE events.plan_cache_runtime_delta DETACH PARTITION events.plan_cache_runtime_delta_p{at:yyyyMMdd};"))
        { retire.Parameters.AddWithValue("target", target); await retire.ExecuteNonQueryAsync(); }
        var retired = await Assert.ThrowsAsync<PostgresException>(async () =>
        { using var changed = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), HistoryCursor(first.RootElement)); });
        Assert.Equal("22023", retired.SqlState); Assert.Contains("retired", retired.MessageText, StringComparison.Ordinal);

    }

    [Fact]
    public async Task CacheHistoryFirstPageWaitsForAnEarlierWriterBeforeAllocatingItsFence()
    {
        await using var database = await Migrate(); Guid target = Guid.NewGuid();
        DateTimeOffset at = new(DateTime.UtcNow.AddSeconds(-10).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var seed = await connection.BeginTransactionAsync())
        {
            await Scope(connection, seed, target); await SeedCanonicalRun(connection, seed, target, Guid.NewGuid(), at); await seed.CommitAsync();
        }
        await using var writer = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await Scope(writer, transaction, target);
        Assert.Equal(2, await Apply(writer, transaction, target, Payload(target, at, Row(1), Row(2))));
        await using var server = database.CreateServerDataSource();
        Task<JsonDocument> pending = CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5));
        bool committed = false;
        try
        {
            bool waiting = false; var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!waiting && DateTime.UtcNow < deadline)
            {
                await using var check = database.DataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory');");
                waiting = Equals(true, await check.ExecuteScalarAsync());
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting); Assert.False(pending.IsCompleted);
            await transaction.CommitAsync(); committed = true;
            using JsonDocument result = await pending;
            Assert.Equal(2, result.RootElement.GetProperty("totalRows").GetInt64());
            Assert.All(result.RootElement.GetProperty("items").EnumerateArray(), row => Assert.Equal("baseline_unavailable", row.GetProperty("transition").GetString()));
        }
        finally
        {
            if (!committed) await transaction.RollbackAsync();
            using JsonDocument settled = await pending;
        }

    }

    [Fact]
    public async Task CacheHistoryUpgradeAndBackupRestorePreserveEvidenceAndSequenceFences()
    {
        await using var database = await Migrate(320); Guid target = Guid.NewGuid();
        DateTimeOffset at = new(DateTime.UtcNow.AddSeconds(-10).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            Assert.Equal(2, await Apply(connection, transaction, target, Payload(target, at, Row(1), Row(2))));
            Assert.Equal(2, await Apply(connection, transaction, target, Payload(target, at.AddSeconds(1), Row(1, 250), Row(2, 250))));
            await transaction.CommitAsync();
        }
        var migration = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(1, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), default);
        Assert.False(migration.HasFailures); Assert.Equal(321, Assert.Single(migration.Results).Migration.Number.Value);
        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using RepositoryTestDatabase restored = await backup.RestoreAsync();
        foreach (var repositoryDatabase in new[] { database, restored })
        {
            await using var server = repositoryDatabase.CreateServerDataSource();
            using JsonDocument first = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5));
            Assert.Equal(4, first.RootElement.GetProperty("totalRows").GetInt64());
            Assert.All(first.RootElement.GetProperty("items").EnumerateArray(), row => Assert.Equal("250", row.GetProperty("executions").GetString()));
            await using (var connection = await repositoryDatabase.DataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await Scope(connection, transaction, target);
                Assert.Equal(2, await Apply(connection, transaction, target, Payload(target, at.AddSeconds(2), Row(1, 500), Row(2, 500))));
                await transaction.CommitAsync();
            }
            using JsonDocument second = await CacheHistory(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), HistoryCursor(first.RootElement));
            Assert.Equal(4, second.RootElement.GetProperty("totalRows").GetInt64());
            Assert.All(second.RootElement.GetProperty("items").EnumerateArray(), row => Assert.Equal("baseline_unavailable", row.GetProperty("transition").GetString()));
            var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-910"), AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(target)]))]);
            var service = new PlanCacheIntervalHistoryQueryService(new PostgreSqlPlanCacheIntervalHistoryPort(server));
            var request = new PlanCacheIntervalHistoryRequest(new(target), 6, Digest(51), Digest(52), Digest(53), at.AddSeconds(-1), at.AddSeconds(5), 100, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            var typed = await service.ReadAsync(authorization, request, default);
            Assert.Equal(6, typed.TotalRows); Assert.Equal(6, typed.Items.Count);
        }
    }

    private static System.Text.Json.Nodes.JsonObject HistoryCursor(JsonElement page)
    {
        var last = page.GetProperty("items").EnumerateArray().Last();
        return new()
        {
            ["revision"] = page.GetProperty("revision").GetInt64(),
            ["snapshot"] = page.GetProperty("snapshot").GetString(),
            ["snapshotUtc"] = page.GetProperty("snapshotUtc").GetString(),
            ["totalRows"] = page.GetProperty("totalRows").GetInt64(),
            ["observedAtUtc"] = last.GetProperty("observedAtUtc").GetString(),
            ["committedAtUtc"] = last.GetProperty("committedAtUtc").GetString(),
            ["collectionRunId"] = last.GetProperty("collectionRunId").GetString(),
            ["cacheEntryFingerprint"] = last.GetProperty("cacheEntryFingerprint").GetString(),
        };
    }
    private static async Task<JsonDocument> CacheHistory(NpgsqlDataSource source, Guid scope, Guid target,
        DateTimeOffset from, DateTimeOffset to, System.Text.Json.Nodes.JsonObject? cursor = null, string? incarnation = null)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true); SELECT reporting.list_plan_cache_interval_history(@target,6,@inc,@query,@plan,@from,@to,2,@cursor);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope.ToString()); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("inc", incarnation ?? Digest(51)); command.Parameters.AddWithValue("query", Digest(52)); command.Parameters.AddWithValue("plan", Digest(53));
        command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("cursor", NpgsqlTypes.NpgsqlDbType.Jsonb, (object?)cursor?.ToJsonString() ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        return JsonDocument.Parse(reader.GetString(0));
    }
}
