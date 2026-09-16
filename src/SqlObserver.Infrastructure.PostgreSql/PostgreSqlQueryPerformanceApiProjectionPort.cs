using Npgsql;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryPerformanceApiProjectionPort : IQueryPerformanceApiRepositoryPort
{
    private readonly NpgsqlDataSource dataSource;
    public PostgreSqlQueryPerformanceApiProjectionPort(NpgsqlDataSource dataSource) => this.dataSource=dataSource??throw new ArgumentNullException(nameof(dataSource));
    public async ValueTask<QueryPerformanceStatusDto?> GetStatusAsync(QueryPerformanceStatusRequest request,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource s=PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout,cancellationToken);
        await using NpgsqlConnection c=await dataSource.OpenConnectionAsync(s.Token);
        await SetScope(c,request.TargetId.Value,s.Token);

        DateTimeOffset snapshotUtc;
        QueryPerformanceSource? source;
        string sourceState;
        QueryCoverage coverage;
        bool fresh;
        bool truncated;
        IReadOnlyList<QueryPerformanceDatabaseStatusDto> databaseStatuses;
        string? targetStatus;
        string? targetReason;
        await using (var cmd=new NpgsqlCommand("SELECT * FROM control.get_query_performance_status(@instance_id,@from_utc,@to_utc);",c){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)})
        {
            cmd.Parameters.AddWithValue("instance_id",request.TargetId.Value);
            cmd.Parameters.AddWithValue("from_utc",request.FromUtc);
            cmd.Parameters.AddWithValue("to_utc",request.ToUtc);
            await using NpgsqlDataReader r=await cmd.ExecuteReaderAsync(s.Token);
            if(!await r.ReadAsync(s.Token))return null;
            snapshotUtc=PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,5);
            source=ParseSource(r.GetString(0));
            sourceState=ParseAggregateState(r.GetString(1));
            coverage=ParseCoverage(r.GetString(2));
            fresh=r.GetBoolean(3);
            truncated=r.GetBoolean(4);
            databaseStatuses=ReadDatabaseStatuses(r,6);
            targetStatus=r.IsDBNull(7)?null:r.GetString(7);
            targetReason=r.IsDBNull(8)?null:r.GetString(8);
        }

        await using (var catalog=new NpgsqlCommand("SELECT * FROM control.get_query_performance_database_catalog(@instance_id);",c){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)})
        {
            catalog.Parameters.AddWithValue("instance_id",request.TargetId.Value);
            await using NpgsqlDataReader r=await catalog.ExecuteReaderAsync(s.Token);
            return new QueryPerformanceStatusDto(request.TargetId,snapshotUtc,source,sourceState,coverage,fresh,truncated,false,null,databaseStatuses,targetStatus,targetReason,await ReadDatabaseCatalog(r,s.Token));
        }
    }
    public async ValueTask<TopQueryPage> GetTopAsync(TopQueryRequest request,CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(request); using CancellationTokenSource s=PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout,cancellationToken); await using NpgsqlConnection c=await dataSource.OpenConnectionAsync(s.Token);await SetScope(c,request.TargetId.Value,s.Token);DateTimeOffset snapshot=request.Cursor?.SnapshotUtc ?? await ReadRepositorySnapshotAsync(c,s.Token);await using var cmd=new NpgsqlCommand("SELECT * FROM control.get_top_queries_projection(@instance_id,@from_utc,@to_utc,@metric,@limit,@after_database_id,@after_interval_end,@after_query,@after_plan,@after_run_id,@after_metric,@after_observation_key,@snapshot_utc);",c){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)};AddTopParameters(cmd,request,snapshot);return await ReadTop(cmd,request,snapshot,s.Token); }
    public async ValueTask<QueryHistoryPage> GetHistoryAsync(QueryHistoryRequest request,CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(request); using CancellationTokenSource s=PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout,cancellationToken); await using NpgsqlConnection c=await dataSource.OpenConnectionAsync(s.Token);await SetScope(c,request.TargetId.Value,s.Token);DateTimeOffset snapshot=request.Cursor?.SnapshotUtc ?? await ReadRepositorySnapshotAsync(c,s.Token);await using var cmd=new NpgsqlCommand("SELECT * FROM control.get_query_history_projection(@instance_id,@database_id,@query_fingerprint,@from_utc,@to_utc,@limit,@after_interval_end,@after_run_id,@after_plan,@after_observation_key,@snapshot_utc);",c){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)};cmd.Parameters.AddWithValue("instance_id",request.TargetId.Value);cmd.Parameters.AddWithValue("database_id",request.Query.DatabaseId);cmd.Parameters.AddWithValue("query_fingerprint",Convert.FromHexString(request.Query.QueryFingerprint));cmd.Parameters.AddWithValue("from_utc",request.FromUtc);cmd.Parameters.AddWithValue("to_utc",request.ToUtc);cmd.Parameters.AddWithValue("limit",request.Limit);cmd.Parameters.AddWithValue("after_interval_end",(object?)request.Cursor?.IntervalEndUtc??DBNull.Value);cmd.Parameters.AddWithValue("after_run_id",(object?)request.Cursor?.CollectionRunId??DBNull.Value);cmd.Parameters.AddWithValue("after_plan",(object?)(request.Cursor?.PlanFingerprint is null?null:Convert.FromHexString(request.Cursor.PlanFingerprint))??DBNull.Value);cmd.Parameters.AddWithValue("after_observation_key",(object?)(request.Cursor?.ObservationKey is null?null:Convert.FromHexString(request.Cursor.ObservationKey))??DBNull.Value);cmd.Parameters.AddWithValue("snapshot_utc",snapshot);var result=new List<QueryHistoryDto>();await using NpgsqlDataReader r=await cmd.ExecuteReaderAsync(s.Token);while(await r.ReadAsync(s.Token)){string? plan=r.IsDBNull(2)?null:Convert.ToHexString(r.GetFieldValue<byte[]>(2)).ToLowerInvariant();string observationKey=Convert.ToHexString(r.GetFieldValue<byte[]>(20)).ToLowerInvariant();result.Add(new QueryHistoryDto(request.TargetId,request.Query,ParseSource(r.GetString(13))??throw new InvalidDataException("Invalid source."),ParseState(r.GetString(14)),ReadMetrics(r),ParseSemantics(r.GetString(6)),PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,3),PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,4),r.GetString(6)=="reset",r.GetBoolean(16),r.GetBoolean(17),false,r.GetGuid(19),ParseCoverage(r.GetString(15)),plan,observationKey));}bool more=result.Count>request.Limit;if(more)result.RemoveAt(result.Count-1);return new QueryHistoryPage(result,more,snapshot); }
    public async ValueTask<QueryPlanMetadataDto?> GetPlanAsync(QueryPlanMetadataRequest request,CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(request);using CancellationTokenSource s=PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout,cancellationToken);await using NpgsqlConnection c=await dataSource.OpenConnectionAsync(s.Token);await SetScope(c,request.TargetId.Value,s.Token);await using var cmd=new NpgsqlCommand("SELECT * FROM control.get_query_plan_metadata(@instance_id,@database_id,@query_fingerprint,@plan_fingerprint);",c){CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout)};cmd.Parameters.AddWithValue("instance_id",request.TargetId.Value);cmd.Parameters.AddWithValue("database_id",request.Plan.Query.DatabaseId);cmd.Parameters.AddWithValue("query_fingerprint",Convert.FromHexString(request.Plan.Query.QueryFingerprint));cmd.Parameters.AddWithValue("plan_fingerprint",Convert.FromHexString(request.Plan.PlanFingerprint));await using NpgsqlDataReader r=await cmd.ExecuteReaderAsync(s.Token);if(!await r.ReadAsync(s.Token))return null;return new QueryPlanMetadataDto(request.TargetId,request.Plan,ParseSource(r.GetString(1))??throw new InvalidDataException("Invalid plan source."),PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,2),ParseCoverage(r.GetString(3)),false); }
    private static void AddTopParameters(NpgsqlCommand c,TopQueryRequest r,DateTimeOffset snapshot){c.Parameters.AddWithValue("instance_id",r.TargetId.Value);c.Parameters.AddWithValue("from_utc",r.FromUtc);c.Parameters.AddWithValue("to_utc",r.ToUtc);c.Parameters.AddWithValue("metric",MetricName(r.Metric));c.Parameters.AddWithValue("limit",r.Limit);c.Parameters.AddWithValue("after_database_id",(object?)r.Cursor?.DatabaseId??DBNull.Value);c.Parameters.AddWithValue("after_interval_end",(object?)r.Cursor?.IntervalEndUtc??DBNull.Value);c.Parameters.AddWithValue("after_query",(object?)(r.Cursor is null?null:Convert.FromHexString(r.Cursor.QueryFingerprint))??DBNull.Value);c.Parameters.AddWithValue("after_plan",(object?)(r.Cursor?.PlanFingerprint is null?null:Convert.FromHexString(r.Cursor.PlanFingerprint))??DBNull.Value);c.Parameters.AddWithValue("after_run_id",(object?)r.Cursor?.CollectionRunId??DBNull.Value);c.Parameters.AddWithValue("after_metric",(object?)r.Cursor?.MetricValue??DBNull.Value);c.Parameters.AddWithValue("after_observation_key",(object?)(r.Cursor?.ObservationKey is null?null:Convert.FromHexString(r.Cursor.ObservationKey))??DBNull.Value);c.Parameters.AddWithValue("snapshot_utc",snapshot);}
    private static async Task<TopQueryPage> ReadTop(NpgsqlCommand c,TopQueryRequest request,DateTimeOffset snapshot,CancellationToken token){var rows=new List<TopQueryDto>();await using NpgsqlDataReader r=await c.ExecuteReaderAsync(token);while(await r.ReadAsync(token)){int db=r.GetInt32(0);string q=Convert.ToHexString(r.GetFieldValue<byte[]>(1)).ToLowerInvariant();var query=new QueryOpaqueIdentity(db,q);var semantics=ParseSemantics(r.GetString(6));PlanOpaqueIdentity? plan=r.IsDBNull(2)?null:new PlanOpaqueIdentity(query,Convert.ToHexString(r.GetFieldValue<byte[]>(2)).ToLowerInvariant());string observationKey=Convert.ToHexString(r.GetFieldValue<byte[]>(20)).ToLowerInvariant();rows.Add(new TopQueryDto(request.TargetId,query,plan,ParseSource(r.GetString(13))??throw new InvalidDataException("Invalid source."),ParseState(r.GetString(14)),request.Metric,MetricValue(r,request.Metric),semantics,PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,3),PostgreSqlRuntimeSupport.ReadUtcTimestamp(r,4),ParseCoverage(r.GetString(15)),r.GetBoolean(16),r.GetBoolean(17),false,r.GetGuid(19),observationKey));}bool more=rows.Count>request.Limit;if(more)rows.RemoveAt(rows.Count-1);return new TopQueryPage(rows,more,snapshot);}
    private static QueryPerformanceMetricSet ReadMetrics(NpgsqlDataReader r)=>new(r.IsDBNull(7)?null:r.GetInt64(7),r.IsDBNull(8)?null:r.GetInt64(8),r.IsDBNull(9)?null:r.GetInt64(9),r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetInt64(11),r.IsDBNull(12)?null:r.GetInt64(12));
    private static long? MetricValue(NpgsqlDataReader r,QueryPerformanceMetric m)=>m switch{QueryPerformanceMetric.CpuMilliseconds=>r.IsDBNull(7)?null:r.GetInt64(7),QueryPerformanceMetric.DurationMilliseconds=>r.IsDBNull(8)?null:r.GetInt64(8),QueryPerformanceMetric.Executions=>r.IsDBNull(9)?null:r.GetInt64(9),QueryPerformanceMetric.LogicalReads=>r.IsDBNull(10)?null:r.GetInt64(10),QueryPerformanceMetric.Writes=>r.IsDBNull(11)?null:r.GetInt64(11),_=>r.IsDBNull(12)?null:r.GetInt64(12)};
    private static string MetricName(QueryPerformanceMetric m)=>m switch{QueryPerformanceMetric.CpuMilliseconds=>"cpu",QueryPerformanceMetric.DurationMilliseconds=>"duration",QueryPerformanceMetric.Executions=>"executions",QueryPerformanceMetric.LogicalReads=>"logical_reads",QueryPerformanceMetric.Writes=>"writes",_=>"rows"};
    private static QueryPerformanceSource InferSource(string s)=>s.Contains("query_store",StringComparison.Ordinal)?QueryPerformanceSource.QueryStore:QueryPerformanceSource.PlanCache;
    private static QueryPerformanceSource? ParseSource(string s)=>s switch { "query_store"=>QueryPerformanceSource.QueryStore,"plan_cache"=>QueryPerformanceSource.PlanCache,"mixed"=>QueryPerformanceSource.Mixed,"unavailable"=>QueryPerformanceSource.Unavailable,_=>null };
    private static string ParseAggregateState(string s)=>QueryPerformanceAggregateState.Require(s);
    private static QueryStoreState ParseState(string s)=>s switch{"read_write"=>QueryStoreState.ReadWrite,"read_only"=>QueryStoreState.ReadOnly,"disabled"=>QueryStoreState.Disabled,"unsupported"=>QueryStoreState.Unsupported,"permission_denied"=>QueryStoreState.PermissionDenied,"read_failure"=>QueryStoreState.ReadFailure,"timed_out"=>QueryStoreState.TimedOut,_=>throw new InvalidDataException("Query performance source state was not allowlisted.")};
    private static QueryMetricSemantics ParseSemantics(string s)=>s switch{"query_store_interval"=>QueryMetricSemantics.QueryStoreInterval,"plan_cache_delta"=>QueryMetricSemantics.PlanCacheDelta,"plan_cache_baseline"=>QueryMetricSemantics.PlanCacheBaseline,"reset"=>QueryMetricSemantics.Reset,_=>QueryMetricSemantics.PlanCacheCumulative};
    private static QueryCoverage ParseCoverage(string s)=>s switch{"complete"=>QueryCoverage.Complete,"truncated"=>QueryCoverage.Truncated,"no_activity"=>QueryCoverage.NoActivity,_=>QueryCoverage.Unavailable};
    private static List<QueryPerformanceDatabaseStatusDto> ReadDatabaseStatuses(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return [];
        using JsonDocument document = JsonDocument.Parse(reader.GetString(ordinal));
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > QueryPerformanceBounds.MaximumDatabases) throw new InvalidDataException("Database status projection exceeded its bound.");
        var result = new List<QueryPerformanceDatabaseStatusDto>(document.RootElement.GetArrayLength());
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            int databaseId = item.GetProperty("databaseId").GetInt32(); string status = item.GetProperty("status").GetString() ?? throw new InvalidDataException("Database status was missing."); string sourceState = item.GetProperty("sourceState").GetString() ?? throw new InvalidDataException("Database source state was missing."); string reason = item.GetProperty("reason").GetString() ?? throw new InvalidDataException("Database status reason was missing."); string lossKind = item.GetProperty("lossKind").GetString() ?? throw new InvalidDataException("Database loss kind was missing.");
            int minimumLostItems = item.TryGetProperty("minimumLostItems", out JsonElement lostItems) ? lostItems.GetInt32() : 0; bool lossCountIsExact = !item.TryGetProperty("lossCountIsExact", out JsonElement exact) || exact.GetBoolean(); int minimumLostBytes = item.TryGetProperty("minimumLostBytes", out JsonElement lostBytes) ? lostBytes.GetInt32() : 0;
            if (databaseId is <= 0 or > 32767 || reason.Length is 0 or > 128 || item.GetProperty("sourceRowsRead").GetInt32() is < 0 or > QueryPerformanceBounds.ProbeRows || item.GetProperty("responseBytes").GetInt32() is < 0 or > QueryPerformanceBounds.ResponseBytes || minimumLostItems < 0 || minimumLostItems > QueryPerformanceBounds.MaximumObservationsPerDatabase || minimumLostBytes < 0 || minimumLostBytes > QueryPerformanceBounds.ResponseBytes) throw new InvalidDataException("Database status projection was outside its bound.");
            result.Add(new QueryPerformanceDatabaseStatusDto(databaseId, status, sourceState, reason, item.GetProperty("fallbackAttempted").GetBoolean(), item.GetProperty("truncated").GetBoolean(), lossKind, item.GetProperty("sourceRowsRead").GetInt32(), item.GetProperty("responseBytes").GetInt32(), minimumLostItems, lossCountIsExact, minimumLostBytes));
        }
        return result;
    }
    private static async Task<List<QueryPerformanceDatabaseCatalogDto>> ReadDatabaseCatalog(NpgsqlDataReader reader, CancellationToken token)
    {
        var result = new List<QueryPerformanceDatabaseCatalogDto>();
        var ids = new HashSet<int>();
        while (await reader.ReadAsync(token))
        {
            if (result.Count >= QueryPerformanceBounds.MaximumDatabases) throw new InvalidDataException("Database catalog projection exceeded its bound.");
            int databaseId = reader.GetInt32(0);
            if (!ids.Add(databaseId)) throw new InvalidDataException("Database catalog projection contained a duplicate identity.");
            result.Add(new QueryPerformanceDatabaseCatalogDto(databaseId,reader.GetString(1)));
        }
        return result;
    }
    private static async Task<DateTimeOffset> ReadRepositorySnapshotAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        object? value = await command.ExecuteScalarAsync(token);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidDataException("Repository snapshot timestamp was invalid.")
        };
    }
    private static async Task SetScope(NpgsqlConnection connection, Guid targetId, CancellationToken token) { await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection); command.Parameters.AddWithValue("scope", targetId.ToString()); await command.ExecuteNonQueryAsync(token); }
}
