using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    private static readonly int[] RequestReadCoveringMigrations = [376, 377, 378, 379];
    private static readonly string[] RequestReadExpectedIdentities = ["51/1", "51/2", "52/0"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCoveringUpgradePreservesLatestEvidenceAndPinnedPaging(bool partitionedRoute)
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new(375, Timeout), default)).HasFailures);
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-3);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid winner = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff0");
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "activity.requests", at.AddHours(-13), outputItemCount: 0);
        await SeedTargetAndRunAsync(database, other, Guid.NewGuid(), "engine.core", at.AddHours(-13), outputItemCount: 0);
        await EnsurePartitionsAsync(database, at.AddHours(-12), at.AddHours(2));
        await SeedRequestReadHistoryAsync(database, target, at.AddHours(-12), 12000);
        await SeedRequestReadHistoryAsync(database, other, at.AddHours(-11), 50000, collector: "engine.core");
        // Unrelated newer completions must never displace this target's evidence.
        await SeedRequestReadHistoryAsync(database, other, at.AddHours(1), 1100);
        await SeedTargetAndRunAsync(database, target, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "activity.requests", at, outputItemCount: 0, startedAt: at.AddSeconds(-1));
        await SeedTargetAndRunAsync(database, target, winner, "activity.requests", at,
            outputItemCount: 3, startedAt: at.AddMinutes(-30), outcome: "partial", loss: true);
        foreach ((int session, int request) in new[] { (51, 1), (51, 2), (52, 0) })
            await InsertRequestAsync(database, target, winner, at, session, request);
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "activity.requests", at.AddSeconds(1),
            outputItemCount: 0, outcome: "permission_denied");
        await SeedTargetAndRunAsync(database, target, Guid.NewGuid(), "activity.sessions", at.AddSeconds(2), outputItemCount: 0);
        await SeedRequestReadHistoryAsync(database, target, at.AddSeconds(3), 1, revision: 2);
        if (partitionedRoute) await ActivateRequestReadRouteAsync(database, [target, other], at.AddHours(-13), at.AddHours(2));
        foreach (string table in new[] { "collection_run", "collection_run_v2", "collection_run_outcome" })
            await ExecuteAsync(database, $"VACUUM (ANALYZE) telemetry.{table};");

        string function = partitionedRoute ? "list_activity_requests_v2_shadow" : "list_activity_requests_legacy";
        JsonElement before = await ExplainRequestReadAsync(database, function, target.Value);
        string security = await RequestReadSecurityAsync(database);
        var upgrade = await migrations.ApplyPendingAsync(new(4, Timeout), default);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(RequestReadCoveringMigrations, upgrade.Results.Select(result => result.Migration.Number.Value));
        Assert.Equal(security, await RequestReadSecurityAsync(database));
        foreach (string table in new[] { "collection_run", "collection_run_v2", "collection_run_outcome" })
            await ExecuteAsync(database, $"VACUUM (ANALYZE) telemetry.{table};");
        JsonElement after = await ExplainRequestReadAsync(database, function, target.Value);
        JsonElement[] scans = RequestPlanNodes(after.GetProperty("Plan")).ToArray();
        var covering = scans.Where(node => node.TryGetProperty("Index Name", out var index) &&
            index.GetString()!.StartsWith("ix_activity_request_", StringComparison.Ordinal) &&
            node.TryGetProperty("Alias", out var alias) && alias.GetString() != "outcome").ToArray();
        Assert.True(covering.Any(node => node.GetProperty("Node Type").GetString() == "Index Only Scan"), JsonSerializer.Serialize(scans.Select(node => new
        {
            kind = node.GetProperty("Node Type").GetString(),
            index = node.TryGetProperty("Index Name", out var index) ? index.GetString() : null,
            alias = node.TryGetProperty("Alias", out var alias) ? alias.GetString() : null,
            rows = node.GetProperty("Actual Rows").GetDouble(),
            loops = node.GetProperty("Actual Loops").GetDouble()
        })));
        Assert.All(covering, node => Assert.Equal("Index Only Scan", node.GetProperty("Node Type").GetString()));
        Assert.InRange(covering.Sum(node => node.TryGetProperty("Heap Fetches", out var count) ? count.GetInt64() : 0), 0, 10);
        long heapVisitsBefore = RequestHeaderHeapVisits(before), heapVisitsAfter = RequestHeaderHeapVisits(after);
        Assert.True(heapVisitsBefore > 1024, $"The history fixture did not exercise historical heap lookups: {heapVisitsBefore}.");
        Assert.True(heapVisitsAfter <= 1, $"Heap visits {heapVisitsBefore} -> {heapVisitsAfter}; {await RequestCoverStatsAsync(database)}; {after.GetRawText()}");
        Assert.Contains(scans, node => node.TryGetProperty("Relation Name", out var table) && table.GetString() == "collection_run_outcome" &&
            node.GetProperty("Node Type").GetString() == "Index Scan" && node.GetProperty("Actual Loops").GetDouble() == 1 &&
            node.GetProperty("Actual Rows").GetDouble() == 1);

        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new WaitHistoryHttpFactory(connection, target.Value);
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Snooze-Identity", "viewer");
        string route = $"/api/v1/observation-targets/{target.Value:D}/activity/requests";
        string? pinned = null;
        foreach (int size in new[] { 1, 2, 25 })
        {
            string? cursor = null;
            var identities = new List<string>();
            do
            {
                using var response = await client.GetAsync(route + $"?limit={size}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                JsonElement page = document.RootElement;
                Assert.Equal(winner, page.GetProperty("evidence").GetProperty("runId").GetGuid());
                Assert.Equal("partial", page.GetProperty("evidence").GetProperty("outcome").GetString());
                Assert.True(page.GetProperty("evidence").GetProperty("isPartial").GetBoolean());
                var rows = page.GetProperty("items").EnumerateArray().ToArray();
                Assert.InRange(rows.Length, 1, size);
                identities.AddRange(rows.Select(row => row.GetProperty("sessionId").GetInt32() + "/" + row.GetProperty("requestId").GetInt32()));
                cursor = page.GetProperty("nextCursor").GetString();
                if (size == 1 && identities.Count == 1) pinned = cursor;
            } while (cursor is not null);
            Assert.Equal(RequestReadExpectedIdentities, identities);
        }
        Guid newer = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, newer, "activity.requests", at.AddMinutes(1), outputItemCount: 1);
        await InsertRequestAsync(database, target, newer, at.AddMinutes(1), 99, 0);
        using (var response = await client.GetAsync(route + "?limit=1&cursor=" + Uri.EscapeDataString(Assert.IsType<string>(pinned))))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(winner, document.RootElement.GetProperty("evidence").GetProperty("runId").GetGuid());
            Assert.Equal(2, document.RootElement.GetProperty("items")[0].GetProperty("requestId").GetInt32());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/observation-targets/{other.Value:D}/activity/requests")).StatusCode);
        using HttpClient anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Guid empty = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, target, empty, "activity.requests", at.AddMinutes(2), outputItemCount: 0);
        using (var response = await client.GetAsync(route))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(empty, document.RootElement.GetProperty("evidence").GetProperty("runId").GetGuid());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextCursor").ValueKind);
        }
        await ReconfigureTargetAsync(database, target, 2);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "?limit=1&cursor=" + Uri.EscapeDataString(pinned!))).StatusCode);
    }

    private static async Task SeedRequestReadHistoryAsync(RepositoryTestDatabase database, MonitoredInstanceId target, DateTimeOffset at, int count, long revision = 1, string collector = "activity.requests")
    {
        await ExecuteAsync(database, """
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,
                 work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT md5(@target::text||@at::text||n::text)::uuid,@target,@collector,1,1,@revision,1,
                   'request.plan.'||n, @owner,1,decode(repeat('aa',32),'hex'),@at+n*interval '1 millisecond',@at+n*interval '1 millisecond'
            FROM generate_series(1,@count) n;
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,
                 loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
            SELECT md5(@target::text||@at::text||n::text)::uuid,'succeeded','completed',1,0,10,
                   CASE WHEN @collector='activity.requests' THEN 1 ELSE 0 END,
                   CASE WHEN @collector='activity.requests' THEN 1 ELSE 0 END,
                   CASE WHEN @collector='activity.requests' THEN 1 ELSE 0 END,0,0,128,128,128,false,false,
                   'none',true,0,0,decode(repeat('bb',32),'hex'),@at+n*interval '1 millisecond'
            FROM generate_series(1,@count) n;
            INSERT INTO telemetry.activity_request_snapshot
                (observed_at,collection_run_id,instance_id,target_revision,session_id,request_id,status_code,command_code,
                 database_id,cpu_ms,total_elapsed_ms,reads,writes,logical_reads,row_count,percent_complete,collected_at)
            SELECT @at+n*interval '1 millisecond',md5(@target::text||@at::text||n::text)::uuid,@target,@revision,51,0,
                   'running','select',5,10,20,30,40,50,60,25,@at+n*interval '1 millisecond'
            FROM generate_series(1,@count) n WHERE @collector='activity.requests';
            """, ("target", target.Value), ("at", at), ("count", count), ("revision", revision), ("owner", Guid.NewGuid()), ("collector", collector));
    }

    private static async Task ActivateRequestReadRouteAsync(RepositoryTestDatabase database, MonitoredInstanceId[] targets, DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var target in targets)
        {
            await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target::text,true);", connection, transaction))
            { scope.Parameters.AddWithValue("target", target.Value); await scope.ExecuteScalarAsync(); }
            foreach (string function in new[] { "backfill_collection_run_v2", "backfill_collection_run_identity" })
            {
                bool complete = false;
                for (int page = 0; page < 128 && !complete; page++)
                {
                    await using var copy = new NpgsqlCommand($"SELECT complete FROM control.{function}(@target,1000);", connection, transaction);
                    copy.Parameters.AddWithValue("target", target.Value);
                    complete = (bool)(await copy.ExecuteScalarAsync())!;
                }
                Assert.True(complete);
            }
            for (DateTimeOffset day = new(from.UtcDateTime.Date, TimeSpan.Zero); day <= to; day = day.AddDays(1))
                foreach (string collector in new[] { "activity.sessions", "activity.requests", "blocking.current", "engine.core" })
                {
                    await using var parity = new NpgsqlCommand("SELECT parity FROM control.record_collection_run_v2_parity_window(@target,@collector,@day,@end,100000);", connection, transaction);
                    parity.Parameters.AddWithValue("target", target.Value); parity.Parameters.AddWithValue("collector", collector);
                    parity.Parameters.AddWithValue("day", day); parity.Parameters.AddWithValue("end", day.AddDays(1));
                    Assert.True((bool)(await parity.ExecuteScalarAsync())!);
                }
            await transaction.CommitAsync();
        }
        await ExecuteAsync(database, "SELECT control.set_collection_run_v2_activity_read_route(true,100000);");
    }

    private static async Task<string> RequestReadSecurityAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(jsonb_build_array(oid,proowner,prosecdef,proacl,proconfig) ORDER BY oid)::text
            FROM pg_proc WHERE pronamespace='reporting'::regnamespace AND proname IN
              ('list_activity_requests','list_activity_requests_legacy','list_activity_requests_v2_shadow');
            """);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> ExplainRequestReadAsync(RepositoryTestDatabase database, string function, Guid target)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator", connection)) await role.ExecuteNonQueryAsync();
        await using var definition = new NpgsqlCommand("SELECT prosrc FROM pg_proc WHERE oid=('reporting.'||@name||'(uuid,uuid,bigint,integer,integer,integer)')::regprocedure", connection);
        definition.Parameters.AddWithValue("name", function);
        string body = (string)(await definition.ExecuteScalarAsync())!;
        string query = body[body.IndexOf("WITH repository_clock", StringComparison.Ordinal)..body.LastIndexOf("END", StringComparison.Ordinal)].Trim();
        foreach (var pair in new Dictionary<string, string> { ["p_instance_id"] = "@target", ["p_snapshot_run_id"] = "NULL::uuid", ["p_snapshot_target_revision"] = "NULL::bigint", ["p_after_session_id"] = "NULL::integer", ["p_after_request_id"] = "NULL::integer", ["p_max_results"] = "25" })
            query = Regex.Replace(query, @"\b" + pair.Key + @"\b", pair.Value);
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE,BUFFERS,VERBOSE,FORMAT JSON) " + query, connection);
        explain.Parameters.AddWithValue("target", target);
        using var document = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
        return document.RootElement[0].Clone();
    }

    private static IEnumerable<JsonElement> RequestPlanNodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray()) foreach (var descendant in RequestPlanNodes(child)) yield return descendant;
    }

    private static long RequestHeaderHeapVisits(JsonElement plan)
    {
        double visits = 0;
        foreach (var node in RequestPlanNodes(plan.GetProperty("Plan")))
        {
            if (!node.TryGetProperty("Relation Name", out var relation) ||
                !(relation.GetString() == "collection_run" || relation.GetString() == "collection_run_outcome" ||
                  relation.GetString()!.StartsWith("collection_run_v2_", StringComparison.Ordinal))) continue;
            if (node.GetProperty("Node Type").GetString() == "Index Only Scan")
                visits += node.GetProperty("Heap Fetches").GetInt64();
            else
                visits += (node.GetProperty("Actual Rows").GetDouble() +
                    (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetDouble() : 0)) *
                    node.GetProperty("Actual Loops").GetDouble();
        }
        return (long)Math.Ceiling(visits);
    }

    private static async Task<string> RequestCoverStatsAsync(RepositoryTestDatabase database)
    {
        await using var query = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object('tables',(SELECT jsonb_agg(jsonb_build_array(relname,relpages,relallvisible))
              FROM pg_class WHERE oid IN ('telemetry.collection_run'::regclass,'telemetry.collection_run_outcome'::regclass)),
              'indexes',(SELECT jsonb_agg(jsonb_build_array(c.relname,i.indisvalid,i.indisready,i.indcheckxmin,pg_get_indexdef(c.oid)))
              FROM pg_class c JOIN pg_index i ON i.indexrelid=c.oid WHERE c.relname LIKE 'ix_activity_request_%'))::text;
            """);
        return (string)(await query.ExecuteScalarAsync())!;
    }
}
