using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryPerformanceFilterIntegrationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task FiltersFindLowRankedDatabaseBeyondFirstTwoHundredAndPreservePagingAndScope()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var migrations = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new MigrationApplyRequest(MigrationBatchResult.MaximumResults, PostgreSql18Fixture.MigrationSetupTimeout), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid target = Guid.NewGuid(), run = Guid.NewGuid();
        await using (var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'Filter test','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,'test/query-filters',gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now());
            INSERT INTO events.query_performance_run(collection_run_id,instance_id,target_revision,window_start,window_end,source,source_state,coverage,freshness,truncated,completion_digest)
            VALUES(@run,@target,1,now()-interval '5 minutes',now(),'mixed','mixed','complete',true,false,decode(repeat('00',32),'hex'));
            INSERT INTO events.query_performance_query(collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT @run,@target,CASE WHEN n<=230 THEN 5 ELSE 7 END,sha256(convert_to(n::text,'UTF8')) FROM generate_series(1,240) n;
            INSERT INTO events.query_performance_observation(collection_run_id,database_id,query_fingerprint,observation_key,source,source_state,interval_start,interval_end,observed_at,semantics,cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed)
            SELECT @run,CASE WHEN n<=230 THEN 5 ELSE 7 END,sha256(convert_to(n::text,'UTF8')),substring(sha256(convert_to(n::text,'UTF8')) FROM 1 FOR 16),
              CASE WHEN n<=230 THEN 'query_store' ELSE 'plan_cache' END::events.query_performance_source,
              CASE WHEN n<=230 THEN 'read_write' ELSE 'read_failure' END,
              now()-interval '5 minutes',now()-interval '4 minutes',now(),
              CASE WHEN n<=230 THEN 'query_store_interval' ELSE 'plan_cache_cumulative' END,240-n,1,1,1,0,1
            FROM generate_series(1,240) n;
            """))
        {
            command.Parameters.AddWithValue("target", target);
            command.Parameters.AddWithValue("run", run);
            await command.ExecuteNonQueryAsync();
        }

        await using var server = database.CreateServerDataSource();
        var port = new PostgreSqlQueryPerformanceApiProjectionPort(server);
        var to = DateTimeOffset.UtcNow;
        var request = new TopQueryRequest(new MonitoredInstanceId(target), to.AddHours(-1), to, QueryPerformanceMetric.CpuMilliseconds, 200, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        TopQueryPage unfiltered = await port.GetTopAsync(request, CancellationToken.None);
        Assert.Equal(200, unfiltered.Items.Count);
        Assert.True(unfiltered.HasMore);
        Assert.All(unfiltered.Items, item => Assert.Equal(5, item.Query.DatabaseId));

        TopQueryPage databasePage = await port.GetTopAsync(request with { DatabaseId = 7 }, CancellationToken.None);
        TopQueryPage sourcePage = await port.GetTopAsync(request with { Source = QueryPerformanceSource.PlanCache }, CancellationToken.None);
        Assert.Equal(10, databasePage.Items.Count);
        Assert.Equal(databasePage.Items.Select(item => item.ObservationKey), sourcePage.Items.Select(item => item.ObservationKey));
        Assert.False(databasePage.HasMore);
        Assert.All(databasePage.Items, item => { Assert.Equal(7, item.Query.DatabaseId); Assert.Equal(QueryPerformanceSource.PlanCache, item.Source); });
        Assert.Equal(0, databasePage.Items[^1].Value);
        Assert.Empty((await port.GetTopAsync(request with { DatabaseId = 7, Source = QueryPerformanceSource.QueryStore }, CancellationToken.None)).Items);

        var filtered = request with { DatabaseId = 7, Source = QueryPerformanceSource.PlanCache, Limit = 6 };
        TopQueryPage first = await port.GetTopAsync(filtered, CancellationToken.None);
        Assert.Equal(6, first.Items.Count);
        Assert.True(first.HasMore);
        var last = first.Items[^1];
        var cursor = new QueryPerformanceCursorEnvelope(request.TargetId, last.Query.DatabaseId, request.FromUtc, request.ToUtc, request.Metric, first.SnapshotUtc, last.IntervalEndUtc, last.Query.QueryFingerprint, last.Value, last.CollectionRunId, last.Plan?.PlanFingerprint, last.ObservationKey) { FilterDatabaseId = 7, FilterSource = QueryPerformanceSource.PlanCache };
        TopQueryPage second = await port.GetTopAsync(filtered with { Cursor = cursor }, CancellationToken.None);
        Assert.Equal(4, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.Equal(first.SnapshotUtc, second.SnapshotUtc);
        Assert.Equal(databasePage.Items.Select(item => item.ObservationKey), first.Items.Concat(second.Items).Select(item => item.ObservationKey));

        // New overload keeps the target claim and FORCE RLS enforcement.
        await using var connection = await server.OpenConnectionAsync();
        await using var denied = new NpgsqlCommand("""
            SELECT set_config('sqlobserver.target_scope','',false);
            SELECT count(*) FROM control.get_top_queries_projection(@target,now()-interval '1 hour',now(),'cpu',200,NULL,NULL,NULL,NULL,NULL,NULL,NULL,now(),7,'plan_cache');
            """, connection);
        denied.Parameters.AddWithValue("target", target);
        await using var reader = await denied.ExecuteReaderAsync();
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
    }
}
