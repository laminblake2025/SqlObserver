using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryPerformanceWindowIntegrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task RecentRankingsKeepTheirScopeOrderAndFinalCursorWithLargeOlderHistory()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
        Assert.False(result.HasFailures);
        await using (var index = database.DataSource.CreateCommand("SELECT pg_get_indexdef('events.ix_query_performance_query_ownership'::regclass);"))
        {
            string definition = Assert.IsType<string>(await index.ExecuteScalarAsync());
            Assert.Contains("(collection_run_id, database_id, query_fingerprint) INCLUDE (instance_id)", definition);
        }
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        Guid oldRun = Guid.NewGuid(), recentRun = Guid.NewGuid(), foreignRun = Guid.NewGuid();
        await using (var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT id,id::text,'Window test','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now() FROM (VALUES(@target),(@other)) t(id);
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT id,target,'queries.performance',1,1,1,1,'test/query-window',gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now() FROM (VALUES(@old,@target),(@recent,@target),(@foreign,@other)) r(id,target);
            INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,completion_digest)
            SELECT id,target,1,now()-interval '5 minutes',now(),'query_store','read_write','complete',true,false,decode(repeat('00',32),'hex') FROM (VALUES(@old,@target),(@recent,@target),(@foreign,@other)) r(id,target);
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT r.id,r.target,CASE WHEN n%2=0 THEN 5 ELSE 6 END,sha256(convert_to(n::text,'UTF8'))
            FROM (VALUES(@old,@target,60000),(@recent,@target,120),(@foreign,@other,120)) r(id,target,count)
            CROSS JOIN LATERAL generate_series(1,r.count) n;
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed)
            SELECT q.collection_run_id,q.database_id,q.query_fingerprint,substring(q.query_fingerprint FROM 1 FOR 16),'query_store','read_write',
              CASE WHEN q.collection_run_id=@old THEN now()-interval '3 days' ELSE now()-interval '5 minutes' END,
              CASE WHEN q.collection_run_id=@old THEN now()-interval '3 days'+interval '1 minute' ELSE now()-interval '4 minutes' END,
              now(),'query_store_interval',CASE WHEN q.collection_run_id=@foreign THEN 999999 ELSE row_number() OVER(PARTITION BY q.collection_run_id ORDER BY q.query_fingerprint) END,1,1,1,0,1
            FROM events.query_performance_query q;
            ANALYZE events.query_performance_observation;
            ANALYZE events.query_performance_query;
            ANALYZE events.query_performance_run;
            """))
        {
            command.CommandTimeout = 60;
            command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("other", other);
            command.Parameters.AddWithValue("old", oldRun); command.Parameters.AddWithValue("recent", recentRun); command.Parameters.AddWithValue("foreign", foreignRun);
            await command.ExecuteNonQueryAsync();
        }
        await using var server = database.CreateServerDataSource();
        var port = new PostgreSqlQueryPerformanceApiProjectionPort(server);
        var to = DateTimeOffset.UtcNow;
        var request = new TopQueryRequest(new MonitoredInstanceId(target), to.AddHours(-24), to,
            QueryPerformanceMetric.CpuMilliseconds, 100, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        var first = await port.GetTopAsync(request, CancellationToken.None);
        Assert.Equal(100, first.Items.Count); Assert.True(first.HasMore);
        Assert.Equal(Enumerable.Range(21,100).Reverse().Select(x => (long?)x), first.Items.Select(x => x.Value));
        Assert.All(first.Items, row => Assert.Equal(recentRun, row.CollectionRunId));
        var last = first.Items[^1];
        var cursor = new QueryPerformanceCursorEnvelope(request.TargetId,last.Query.DatabaseId,request.FromUtc,request.ToUtc,
            request.Metric,first.SnapshotUtc,last.IntervalEndUtc,last.Query.QueryFingerprint,last.Value,last.CollectionRunId,last.Plan?.PlanFingerprint,last.ObservationKey);
        var final = await port.GetTopAsync(request with { Cursor = cursor }, CancellationToken.None);
        Assert.Equal(20, final.Items.Count); Assert.False(final.HasMore);
        Assert.Equal(Enumerable.Range(1,20).Reverse().Select(x => (long?)x), final.Items.Select(x => x.Value));
        await using var connection = await server.OpenConnectionAsync();
        foreach (string scope in new[] { target.ToString("D"), "" })
        {
            await using var denied = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@scope,false);
                SELECT count(*) FROM control.get_top_queries_projection(@other,now()-interval '24 hours',now(),'cpu',100,NULL,NULL,NULL,NULL,NULL,NULL,NULL,now());
                """, connection);
            denied.Parameters.AddWithValue("scope", scope); denied.Parameters.AddWithValue("other", other);
            await using var reader = await denied.ExecuteReaderAsync();
            Assert.True(await reader.NextResultAsync()); Assert.True(await reader.ReadAsync()); Assert.Equal(0,reader.GetInt64(0));
        }
    }

    [Fact]
    public async Task ScopedRankingMatches0068AndPreservesLatestSnapshotPagingAndRls()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource, PostgreSqlMigrationCatalog.LoadEmbedded());
        MigrationBatchResult result = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
        Assert.False(result.HasFailures);

        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        Guid olderRun = Guid.NewGuid(), latestRun = Guid.NewGuid(), tieRun = Guid.NewGuid(), sourceRun = Guid.NewGuid(), futureRun = Guid.NewGuid(), wrongQueryRun = Guid.NewGuid(), wrongRun = Guid.NewGuid(), otherRun = Guid.NewGuid();
        DateTimeOffset snapshot = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset from = snapshot.AddHours(-24);
        await SeedScopedRankingAsync(database, target, other, snapshot, from, olderRun, latestRun, tieRun, sourceRun, futureRun, wrongQueryRun, wrongRun, otherRun);

        await using var server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);

        await ApplyFunctionMigrationAsync(database, "0068_query_ranking_latest_observation.sql");
        ProjectionPage legacy = await ReadTopPageAsync(connection, target, from, snapshot, 200, null);
        await ApplyFunctionMigrationAsync(database, "0070_query_ranking_scoped_runs.sql");
        ProjectionPage scoped = await ReadTopPageAsync(connection, target, from, snapshot, 200, null);

        AssertPageEqual(legacy, scoped);
        Assert.Equal(4, scoped.Items.Count);
        Assert.Equal(new long?[] { 90, 80, 60, 0 }, scoped.Items.Select(static row => row.CpuMs));
        Assert.Equal(tieRun, scoped.Items[0].CollectionRunId);
        Assert.Equal("query_store", scoped.Items[0].Source);
        Assert.Equal("plan_cache", scoped.Items[1].Source);
        Assert.Equal("plan_cache_delta", scoped.Items[1].Semantics);
        Assert.Equal(scoped.Items[0].QueryFingerprint, scoped.Items[1].QueryFingerprint);
        Assert.Equal(scoped.Items[0].PlanFingerprint, scoped.Items[1].PlanFingerprint);
        Assert.Equal(0L, scoped.Items[^1].CpuMs);
        Assert.DoesNotContain(scoped.Items, row => row.CpuMs is 100 or 997 or 998 or 999);
        Assert.DoesNotContain(scoped.Items, row => row.QueryFingerprint == Fingerprint("missing"));
        Assert.Equal(snapshot.AddMinutes(-15), scoped.Items[0].CommittedAt);

        await ApplyFunctionMigrationAsync(database, "0068_query_ranking_latest_observation.sql");
        ProjectionPage legacyFirst = await ReadTopPageAsync(connection, target, from, snapshot, 2, null);
        ProjectionCursor legacyCursor = CursorFor(legacyFirst.Items[^1]);
        ProjectionPage legacySecond = await ReadTopPageAsync(connection, target, from, snapshot, 2, legacyCursor);
        await ApplyFunctionMigrationAsync(database, "0070_query_ranking_scoped_runs.sql");
        ProjectionPage scopedFirst = await ReadTopPageAsync(connection, target, from, snapshot, 2, null);
        ProjectionPage scopedSecond = await ReadTopPageAsync(connection, target, from, snapshot, 2, CursorFor(scopedFirst.Items[^1]));

        AssertPageEqual(legacyFirst, scopedFirst);
        AssertPageEqual(legacySecond, scopedSecond);
        Assert.True(scopedFirst.HasMore);
        Assert.False(scopedSecond.HasMore);
        AssertRowsEqual(legacy.Items, scopedFirst.Items.Concat(scopedSecond.Items).ToArray());

        foreach (Guid scope in new[] { other, Guid.Empty })
        {
            await SetScopeAsync(connection, scope == Guid.Empty ? string.Empty : scope.ToString("D"));
            ProjectionPage denied = await ReadTopPageAsync(connection, target, from, snapshot, 200, null);
            Assert.Empty(denied.Items);
            Assert.False(denied.HasMore);
        }

        await SetScopeAsync(connection, target);
        ProjectionPage wrongTarget = await ReadTopPageAsync(connection, other, from, snapshot, 200, null);
        Assert.Empty(wrongTarget.Items);
    }

    [Fact]
    public async Task ScopedRankingKeepsDenseWindowWorkBounded()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource, PostgreSqlMigrationCatalog.LoadEmbedded());
        MigrationBatchResult result = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
        Assert.False(result.HasFailures);

        Guid target = Guid.NewGuid();
        DateTimeOffset snapshot = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset from = snapshot.AddHours(-24);
        const int observationCount = 120_000;
        await SeedDenseRankingAsync(database, target, snapshot);

        await using var server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        await using (var timeout = new NpgsqlCommand("SET statement_timeout='60s';", connection)) await timeout.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand("""
            EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON)
            SELECT count(*)
            FROM control.get_top_queries_projection(
                @instance_id,@from_utc,@to_utc,'cpu',200,
                NULL,NULL,NULL,NULL,NULL,NULL,NULL,@snapshot_utc);
            """, connection)
        {
            CommandTimeout = 60,
        };
        explain.Parameters.AddWithValue("instance_id", target);
        explain.Parameters.AddWithValue("from_utc", from);
        explain.Parameters.AddWithValue("to_utc", snapshot);
        explain.Parameters.AddWithValue("snapshot_utc", snapshot);
        await using NpgsqlDataReader reader = await explain.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        using JsonDocument plan = JsonDocument.Parse(reader.GetString(0));
        JsonElement root = plan.RootElement[0].GetProperty("Plan");
        Assert.True(TryFindPlanNode(root, "Function Scan", out JsonElement functionScan));
        Assert.Equal(201d, functionScan.GetProperty("Actual Rows").GetDouble());
        long sharedBlocks = root.GetProperty("Shared Hit Blocks").GetInt64() + root.GetProperty("Shared Read Blocks").GetInt64();
        Assert.InRange(sharedBlocks, 0, observationCount * 6L);
    }

    private static bool TryFindPlanNode(JsonElement node, string nodeType, out JsonElement match)
    {
        if (node.TryGetProperty("Node Type", out JsonElement type) && type.GetString() == nodeType)
        {
            match = node;
            return true;
        }

        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                if (TryFindPlanNode(child, nodeType, out match)) return true;
            }
        }

        match = default;
        return false;
    }

    private static async Task SeedScopedRankingAsync(
        RepositoryTestDatabase database,
        Guid target,
        Guid other,
        DateTimeOffset snapshot,
        DateTimeOffset from,
        Guid olderRun,
        Guid latestRun,
        Guid tieRun,
        Guid sourceRun,
        Guid futureRun,
        Guid wrongQueryRun,
        Guid wrongRun,
        Guid otherRun)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Scoped ranking target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,@snapshot,@snapshot,@snapshot),
                  (@other,@other::text,'Scoped ranking other','sql02',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,@snapshot,@snapshot,@snapshot);
            WITH runs(run_id,run_instance,query_instance,committed_at) AS
            (
                VALUES
                (@older,@target,@target,@snapshot - interval '2 hours'),
                (@latest,@target,@target,@snapshot - interval '1 hour'),
                (@tie,@target,@target,@snapshot - interval '15 minutes'),
                (@source,@target,@target,@snapshot - interval '30 minutes'),
                (@future,@target,@target,@snapshot + interval '1 minute'),
                (@wrong_query,@target,@other,@snapshot - interval '10 minutes'),
                (@wrong_run,@other,@target,@snapshot - interval '10 minutes'),
                (@other_run,@other,@other,@snapshot - interval '10 minutes')
            )
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT run_id,run_instance,'queries.performance',1,1,1,1,'test/query-ranking-scoped-runs',gen_random_uuid(),1,decode(repeat('00',32),'hex'),committed_at,committed_at
            FROM runs;
            WITH runs(run_id,run_instance,source,committed_at) AS
            (
                VALUES
                (@older,@target,'query_store'::events.query_performance_source,@snapshot - interval '2 hours'),
                (@latest,@target,'query_store'::events.query_performance_source,@snapshot - interval '1 hour'),
                (@tie,@target,'query_store'::events.query_performance_source,@snapshot - interval '15 minutes'),
                (@source,@target,'mixed'::events.query_performance_source,@snapshot - interval '30 minutes'),
                (@future,@target,'query_store'::events.query_performance_source,@snapshot + interval '1 minute'),
                (@wrong_query,@target,'query_store'::events.query_performance_source,@snapshot - interval '10 minutes'),
                (@wrong_run,@other,'query_store'::events.query_performance_source,@snapshot - interval '10 minutes'),
                (@other_run,@other,'query_store'::events.query_performance_source,@snapshot - interval '10 minutes')
            )
            INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,completion_digest,committed_at)
            SELECT run_id,run_instance,1,@from,@snapshot,source,'read_write','complete',true,false,decode(repeat('00',32),'hex'),committed_at
            FROM runs;
            WITH runs(run_id,query_instance) AS
            (
                VALUES
                (@older,@target),(@latest,@target),(@tie,@target),(@source,@target),(@future,@target),
                (@wrong_query,@other),(@wrong_run,@target),(@other_run,@other)
            ), queries(database_id,query_fingerprint) AS
            (
                VALUES
                (5,sha256(convert_to('ranked','UTF8'))),
                (5,sha256(convert_to('second','UTF8'))),
                (5,sha256(convert_to('zero','UTF8'))),
                (5,sha256(convert_to('missing','UTF8'))),
                (5,sha256(convert_to('future','UTF8'))),
                (5,sha256(convert_to('mismatch-query','UTF8'))),
                (5,sha256(convert_to('mismatch-run','UTF8'))),
                (5,sha256(convert_to('foreign','UTF8')))
            )
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT runs.run_id,runs.query_instance,queries.database_id,queries.query_fingerprint
            FROM runs CROSS JOIN queries;
            WITH observations(run_id,query_label,plan_label,observation_label,source,semantics,interval_start,interval_end,observed_at,cpu_ms) AS
            (
                VALUES
                (@older,'ranked','plan-one','older-ranked','query_store','query_store_interval',@snapshot - interval '4 hours',@snapshot - interval '3 hours',@snapshot - interval '3 hours' + interval '1 minute',5::bigint),
                (@latest,'ranked','plan-one','latest-ranked','query_store','query_store_interval',@snapshot - interval '2 hours',@snapshot - interval '1 hour',@snapshot - interval '1 hour' + interval '1 minute',70::bigint),
                (@tie,'ranked','plan-one','tie-ranked','query_store','query_store_interval',@snapshot - interval '2 hours',@snapshot - interval '1 hour',@snapshot - interval '1 hour' + interval '1 minute',90::bigint),
                (@source,'ranked','plan-one','source-plan-cache','plan_cache','plan_cache_delta',@snapshot - interval '1 hour',@snapshot - interval '45 minutes',@snapshot - interval '44 minutes',80::bigint),
                (@latest,'second','','second','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '30 minutes',@snapshot - interval '29 minutes',60::bigint),
                (@latest,'zero','','zero','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '20 minutes',@snapshot - interval '19 minutes',0::bigint),
                (@older,'missing','','missing-old','query_store','query_store_interval',@snapshot - interval '2 hours',@snapshot - interval '2 hours' + interval '1 minute',@snapshot - interval '2 hours' + interval '2 minutes',42::bigint),
                (@latest,'missing','','missing-new','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '10 minutes',@snapshot - interval '9 minutes',NULL::bigint),
                (@future,'future','plan-one','future','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '5 minutes',@snapshot - interval '4 minutes',100::bigint),
                (@wrong_query,'mismatch-query','plan-one','mismatch-query','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '4 minutes',@snapshot - interval '3 minutes',999::bigint),
                (@wrong_run,'mismatch-run','plan-one','mismatch-run','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '3 minutes',@snapshot - interval '2 minutes',998::bigint),
                (@other_run,'foreign','plan-one','foreign','query_store','query_store_interval',@snapshot - interval '1 hour',@snapshot - interval '2 minutes',@snapshot - interval '1 minute',997::bigint)
            )
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,plan_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed)
            SELECT observations.run_id,5,sha256(convert_to(observations.query_label,'UTF8')),NULLIF(sha256(convert_to(observations.plan_label,'UTF8')),sha256(convert_to('','UTF8'))),substring(sha256(convert_to(observations.observation_label,'UTF8')) FROM 1 FOR 16),observations.source::events.query_performance_source,'read_write',observations.interval_start,observations.interval_end,observations.observed_at,observations.semantics,observations.cpu_ms,1,1,1,0,1
            FROM observations;
            """);
        {
            command.CommandTimeout = 60;
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("other", other);
            command.Parameters.AddWithValue("snapshot", snapshot);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("older", olderRun);
            command.Parameters.AddWithValue("latest", latestRun);
            command.Parameters.AddWithValue("tie", tieRun);
            command.Parameters.AddWithValue("source", sourceRun);
            command.Parameters.AddWithValue("future", futureRun);
            command.Parameters.AddWithValue("wrong_query", wrongQueryRun);
            command.Parameters.AddWithValue("wrong_run", wrongRun);
            command.Parameters.AddWithValue("other_run", otherRun);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedDenseRankingAsync(
        RepositoryTestDatabase database,
        Guid target,
        DateTimeOffset snapshot)
    {
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Dense ranking target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,@snapshot,@snapshot,@snapshot);
            CREATE TEMP TABLE dense_ranking_runs ON COMMIT DROP AS
            SELECT gen_random_uuid() AS run_id,n AS run_number,@target::uuid AS instance_id,@snapshot - n * interval '2 minutes' AS committed_at
            FROM generate_series(1,120) AS numbers(n);
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT run_id,instance_id,'queries.performance',1,1,1,1,'test/query-ranking-dense',gen_random_uuid(),1,decode(repeat('00',32),'hex'),committed_at,committed_at
            FROM dense_ranking_runs;
            INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,completion_digest,committed_at)
            SELECT run_id,instance_id,1,committed_at - interval '5 minutes',committed_at,'query_store','read_write','complete',true,false,decode(repeat('00',32),'hex'),committed_at
            FROM dense_ranking_runs;
            CREATE TEMP TABLE dense_ranking_queries ON COMMIT DROP AS
            SELECT runs.run_id,runs.run_number,queries.query_number,
                   sha256(convert_to(concat('dense-query-',queries.query_number),'UTF8')) AS query_fingerprint
            FROM dense_ranking_runs AS runs
            CROSS JOIN generate_series(1,1000) AS queries(query_number);
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT run_id,@target,5,query_fingerprint
            FROM dense_ranking_queries;
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed)
            SELECT queries.run_id,5,queries.query_fingerprint,
                   substring(sha256(convert_to(concat('dense-observation-',queries.run_number,'-',queries.query_number),'UTF8')) FROM 1 FOR 16),
                   'query_store','read_write',runs.committed_at - interval '5 minutes',runs.committed_at,runs.committed_at,
                   'query_store_interval',queries.query_number,1,1,1,0,1
            FROM dense_ranking_queries AS queries
            JOIN dense_ranking_runs AS runs ON runs.run_id=queries.run_id;
            ANALYZE events.query_performance_observation;
            ANALYZE events.query_performance_query;
            ANALYZE events.query_performance_run;
            """);
        {
            command.CommandTimeout = 60;
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("snapshot", snapshot);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task ApplyFunctionMigrationAsync(RepositoryTestDatabase database, string fileName)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            await File.ReadAllTextAsync(MigrationPath(fileName)), connection, transaction)
        {
            CommandTimeout = 60,
        };
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static string MigrationPath(string fileName) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations", fileName));

    private static async Task SetScopeAsync(NpgsqlConnection connection, Guid scope) =>
        await SetScopeAsync(connection, scope.ToString("D"));

    private static async Task SetScopeAsync(NpgsqlConnection connection, string scope)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection);
        command.Parameters.AddWithValue("scope", scope);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ProjectionPage> ReadTopPageAsync(
        NpgsqlConnection connection,
        Guid target,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        ProjectionCursor? cursor)
    {
        await using var command = new NpgsqlCommand("""
            SELECT *
            FROM control.get_top_queries_projection(
                @instance_id,@from_utc,@to_utc,'cpu',@limit,
                @after_database_id,@after_interval_end,@after_query,@after_plan,
                @after_run_id,@after_metric,@after_observation_key,@snapshot_utc);
            """, connection)
        {
            CommandTimeout = 60,
        };
        command.Parameters.AddWithValue("instance_id", target);
        command.Parameters.AddWithValue("from_utc", from);
        command.Parameters.AddWithValue("to_utc", to);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("after_database_id", (object?)cursor?.DatabaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("after_interval_end", (object?)cursor?.IntervalEnd ?? DBNull.Value);
        command.Parameters.AddWithValue("after_query", (object?)(cursor is null ? null : Convert.FromHexString(cursor.QueryFingerprint)) ?? DBNull.Value);
        command.Parameters.AddWithValue("after_plan", (object?)(cursor?.PlanFingerprint is null ? null : Convert.FromHexString(cursor.PlanFingerprint)) ?? DBNull.Value);
        command.Parameters.AddWithValue("after_run_id", (object?)cursor?.CollectionRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("after_metric", (object?)cursor?.MetricValue ?? DBNull.Value);
        command.Parameters.AddWithValue("after_observation_key", (object?)(cursor is null ? null : Convert.FromHexString(cursor.ObservationKey)) ?? DBNull.Value);
        command.Parameters.AddWithValue("snapshot_utc", to);

        var items = new List<ProjectionRow>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new ProjectionRow(
                reader.GetInt32(0),
                Fingerprint(reader.GetFieldValue<byte[]>(1)),
                reader.IsDBNull(2) ? null : Fingerprint(reader.GetFieldValue<byte[]>(2)),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetString(6),
                ReadNullableLong(reader, 7),
                ReadNullableLong(reader, 8),
                ReadNullableLong(reader, 9),
                ReadNullableLong(reader, 10),
                ReadNullableLong(reader, 11),
                ReadNullableLong(reader, 12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetBoolean(16),
                reader.GetBoolean(17),
                reader.GetFieldValue<DateTimeOffset>(18),
                reader.GetGuid(19),
                Fingerprint(reader.GetFieldValue<byte[]>(20))));
        }

        bool hasMore = items.Count > limit;
        if (hasMore) items.RemoveAt(items.Count - 1);
        return new ProjectionPage(items, hasMore);
    }

    private static long? ReadNullableLong(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static ProjectionCursor CursorFor(ProjectionRow row) =>
        new(row.DatabaseId, row.IntervalEnd, row.QueryFingerprint, row.PlanFingerprint, row.CollectionRunId, row.CpuMs ?? throw new InvalidOperationException("CPU cursor value was unexpectedly missing."), row.ObservationKey);

    private static void AssertPageEqual(ProjectionPage expected, ProjectionPage actual)
    {
        Assert.Equal(expected.HasMore, actual.HasMore);
        AssertRowsEqual(expected.Items, actual.Items);
    }

    private static void AssertRowsEqual(IReadOnlyList<ProjectionRow> expected, IReadOnlyList<ProjectionRow> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++) Assert.Equal(expected[index], actual[index]);
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Fingerprint(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private sealed record ProjectionPage(IReadOnlyList<ProjectionRow> Items, bool HasMore);

    private sealed record ProjectionCursor(
        int DatabaseId,
        DateTimeOffset IntervalEnd,
        string QueryFingerprint,
        string? PlanFingerprint,
        Guid CollectionRunId,
        long MetricValue,
        string ObservationKey);

    private sealed record ProjectionRow(
        int DatabaseId,
        string QueryFingerprint,
        string? PlanFingerprint,
        DateTimeOffset IntervalStart,
        DateTimeOffset IntervalEnd,
        DateTimeOffset ObservedAt,
        string Semantics,
        long? CpuMs,
        long? DurationMs,
        long? ExecutionCount,
        long? LogicalReads,
        long? Writes,
        long? RowsProcessed,
        string Source,
        string SourceState,
        string Coverage,
        bool Freshness,
        bool Truncated,
        DateTimeOffset CommittedAt,
        Guid CollectionRunId,
        string ObservationKey);
}
