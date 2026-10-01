using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests
{
    [Fact]
    public async Task CacheRankingKeepsRecreatedDatabaseScopesSeparateAndUsesAnExactTopNLookahead()
    {
        await using var database = await Migrate(); Guid target = Guid.NewGuid();
        DateTimeOffset at = new(DateTime.UtcNow.AddSeconds(-10).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            for (int capture = 0; capture < 4; capture++)
            {
                var payload = Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheRuntimeCommitPayload.Serialize(
                    TypedBatch(target, at.AddSeconds(capture), [1, 2, 3, 4]))));
                int index = 0;
                foreach (JsonNode? node in payload["items"]!.AsArray())
                {
                    JsonObject item = Assert.IsType<JsonObject>(node);
                    item["queryFingerprint"] = Digest(100 + index++);
                    item["databaseIncarnation"] = Digest(capture < 2 ? 51 : 55);
                    item["identityBeforeIncarnation"] = Digest(capture < 2 ? 51 : 55);
                    item["identityAfterIncarnation"] = Digest(capture < 2 ? 51 : 55);
                    foreach (string field in CounterFields) item[field] = capture % 2 == 0 ? 0L : long.MaxValue;
                }
                Assert.Equal(4, await Apply(connection, transaction, target, payload));
            }
            await transaction.CommitAsync();
        }
        await using var server = database.CreateServerDataSource();
        using JsonDocument full = await Ranking(server, target, target, at.AddSeconds(-1), at.AddSeconds(5));
        Assert.Equal(8, full.RootElement.GetProperty("items").GetArrayLength());
        foreach (JsonElement item in full.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), item.GetProperty("executions").GetString());
            Assert.Equal(1, item.GetProperty("comparableRows").GetInt64());
        }
        using JsonDocument limited = await Ranking(server, target, target, at.AddSeconds(-1), at.AddSeconds(5), limit: 2);
        Assert.True(limited.RootElement.GetProperty("hasMore").GetBoolean());
        var firstTwo = limited.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstTwo.Length);
        Assert.Equal(firstTwo[0].GetProperty("queryFingerprint").GetString(), firstTwo[1].GetProperty("queryFingerprint").GetString());
        Assert.NotEqual(firstTwo[0].GetProperty("databaseIncarnation").GetString(), firstTwo[1].GetProperty("databaseIncarnation").GetString());
        Assert.Equal(8, limited.RootElement.GetProperty("comparableRows").GetInt64());
        Assert.Equal(8, limited.RootElement.GetProperty("unavailableRows").GetInt64());
    }

    [Fact]
    public async Task CacheWindowRankingUsesExactDeltasAndReportsMissingComparisonCoverage()
    {
        await using var database = await Migrate();
        Guid target = Guid.NewGuid();
        DateTimeOffset at = new(DateTime.UtcNow.AddSeconds(-10).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, target);
            foreach (var frame in new[] { TypedBatch(target, at, [1, 2]),
                TypedBatch(target, at.AddSeconds(1), [1, 2], increment: 250),
                TypedBatch(target, at.AddSeconds(2), [1, 2], increment: -2) })
                Assert.Equal(2, await Apply(connection, transaction, target,
                    Assert.IsType<JsonObject>(JsonNode.Parse(PlanCacheRuntimeCommitPayload.Serialize(frame)))));
            await transaction.CommitAsync();
        }
        await using var server = database.CreateServerDataSource();
        using JsonDocument result = await Ranking(server, target, target, at.AddSeconds(-1), at.AddSeconds(3));
        JsonElement root = result.RootElement;
        Assert.Equal("plan_cache_delta", root.GetProperty("semantics").GetString());
        Assert.Equal(3, root.GetProperty("sourceFrames").GetInt64());
        Assert.Equal(3, root.GetProperty("incompleteFrames").GetInt64());
        Assert.Equal(2, root.GetProperty("comparableRows").GetInt64());
        Assert.Equal(4, root.GetProperty("unavailableRows").GetInt64());
        Assert.Equal(0, root.GetProperty("boundaryRows").GetInt64());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        JsonElement row = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(6, row.GetProperty("databaseId").GetInt32());
        Assert.Equal(Digest(52), row.GetProperty("queryFingerprint").GetString());
        Assert.Equal(Digest(53), row.GetProperty("planFingerprint").GetString());
        Assert.Equal(0.5m, decimal.Parse(row.GetProperty("cpuMilliseconds").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.5m, decimal.Parse(row.GetProperty("durationMilliseconds").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        foreach (string key in new[] { "executions", "logicalReads", "writes", "rows" })
            Assert.Equal("500", row.GetProperty(key).GetString());
        Assert.Equal(2, row.GetProperty("comparableRows").GetInt64());

        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-910"),
            AuthorizationPrincipalState.Active, [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]))]);
        var service = new PlanCacheIntervalRankingQueryService(new PostgreSqlPlanCacheIntervalRankingPort(server));
        foreach (var metric in Enum.GetValues<QueryPerformanceMetric>())
        {
            var typed = await service.ReadAsync(authorization, new PlanCacheIntervalRankingRequest(new(target),
                at.AddSeconds(-1), at.AddSeconds(3), metric, 25, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), default);
            Assert.Equal("500", Assert.Single(typed.Items).Executions);
            Assert.Equal(4, typed.UnavailableRows);
        }

        // A counter difference crossing the selected boundary cannot be clipped or assigned to this window.
        using JsonDocument boundary = await Ranking(server, target, target, at.AddMilliseconds(500), at.AddSeconds(3));
        Assert.Empty(boundary.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(2, boundary.RootElement.GetProperty("boundaryRows").GetInt64());
        Assert.Equal(2, boundary.RootElement.GetProperty("unavailableRows").GetInt64());
        Assert.Equal(0, boundary.RootElement.GetProperty("comparableRows").GetInt64());
        Assert.Equal("no_comparable_rows", boundary.RootElement.GetProperty("coverage").GetString());

        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            using JsonDocument denied = await Ranking(server, target, Guid.NewGuid(), at.AddSeconds(-1), at.AddSeconds(3));
        })).SqlState);
        await using (var direct = server.CreateCommand("SELECT count(*) FROM events.plan_cache_runtime_delta;"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteScalarAsync())).SqlState);
        await using (var revise = database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;"))
        {
            revise.Parameters.AddWithValue("target", target); await revise.ExecuteNonQueryAsync();
        }
        using JsonDocument revised = await Ranking(server, target, target, at.AddSeconds(-1), at.AddSeconds(3));
        Assert.Equal(0, revised.RootElement.GetProperty("sourceFrames").GetInt64());
        Assert.Empty(revised.RootElement.GetProperty("items").EnumerateArray());
    }

    private static async Task<JsonDocument> Ranking(NpgsqlDataSource source, Guid scope, Guid target,
        DateTimeOffset from, DateTimeOffset to, int limit = 25)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.target_scope',@scope,true);
            SELECT reporting.list_plan_cache_interval_queries(@target,@from_utc,@to_utc,'cpu',@limit);
            """, connection, transaction);
        command.Parameters.AddWithValue("scope", scope.ToString()); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from_utc", from); command.Parameters.AddWithValue("to_utc", to);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync());
        return JsonDocument.Parse(reader.GetString(0));
    }
}
