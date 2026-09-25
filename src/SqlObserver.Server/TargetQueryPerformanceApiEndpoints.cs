using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class TargetQueryPerformanceApiEndpoints
{
    public static IEndpointRouteBuilder MapTargetQueryPerformanceApiEndpoints(this IEndpointRouteBuilder e)
    {
        var group = e.MapGroup("/api/v1/observation-targets/{instanceId:guid}/query-performance")
            .RequireAuthorization();
        group.MapGet("/status", StatusAsync);
        group.MapGet("/top", TopAsync);
        group.MapGet("/databases/{databaseId:int}/history/{queryFingerprint}", HistoryAsync);
        group.MapGet("/databases/{databaseId:int}/history/{queryFingerprint}/runs/{collectionRunId:guid}/text", TextAsync);
        group.MapGet("/databases/{databaseId:int}/history/{queryFingerprint}/runs/{collectionRunId:guid}/plans/{planFingerprint}/content", PlanContentAsync);
        group.MapGet("/databases/{databaseId:int}/plans/{planFingerprint}", PlanAsync);
        return e;
    }
    private static async Task<IResult> StatusAsync(HttpContext h, Guid instanceId, IQueryPerformanceApiQueryService s, WindowsGroupRoleResolver r, string? fromUtc=null, string? toUtc=null, CancellationToken c=default)
    { try { DateTimeOffset to=Parse(toUtc)??DateTimeOffset.UtcNow, from=Parse(fromUtc)??to.AddHours(-24); var row=await s.GetStatusAsync(r.Resolve(h.User),new QueryPerformanceStatusRequest(new MonitoredInstanceId(instanceId),from,to,new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),c); return row is null?Results.NotFound():Results.Ok(new { targetId=instanceId,snapshotUtc=row.SnapshotUtc,source=row.Source is null?null:SourceName(row.Source.Value),sourceState=row.SourceState,coverage=CoverageName(row.Coverage),fresh=row.Fresh,truncated=row.Truncated,contentAvailable=false,reason=row.Reason,targetStatus=row.TargetStatus,targetReason=row.TargetReason,databaseStatuses=row.DatabaseStatuses,databaseCatalog=row.DatabaseCatalog }); } catch(UnauthorizedAccessException){return Results.Forbid();} catch(ArgumentException){return Results.BadRequest();} }
    private static async Task<IResult> TopAsync(HttpContext h, Guid instanceId, IQueryPerformanceApiQueryService s, WindowsGroupRoleResolver r, string? metric=null, int limit=25, string? fromUtc=null, string? toUtc=null, string? cursor=null, CancellationToken c=default)
    { try { if (limit is <= 0 or > 200) throw new ArgumentException("Page limit is outside its bounds."); RequireExplicitCursorWindow(cursor,fromUtc,toUtc); DateTimeOffset to=Parse(toUtc)??DateTimeOffset.UtcNow, from=Parse(fromUtc)??to.AddHours(-24); QueryPerformanceMetric m=ParseMetric(metric); var decoded=DecodeCursor(cursor,instanceId,m,from,to,true); var page=await s.GetTopAsync(r.Resolve(h.User),new TopQueryRequest(new MonitoredInstanceId(instanceId),from,to,m,limit,decoded,new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),c); string? next=page.HasMore&&page.Items.Count>0?EncodeCursor(new QueryPerformanceCursorEnvelope(new MonitoredInstanceId(instanceId),page.Items[^1].Query.DatabaseId,from,to,m,page.SnapshotUtc,page.Items[^1].IntervalEndUtc,page.Items[^1].Query.QueryFingerprint,page.Items[^1].Value,page.Items[^1].CollectionRunId,page.Items[^1].Plan?.PlanFingerprint,page.Items[^1].ObservationKey)):null; return Results.Ok(new {targetId=instanceId,metric=MetricName(m),items=page.Items.Select(MapTopItem),nextCursor=next,snapshotUtc=page.SnapshotUtc,fromUtc=from,toUtc=to}); } catch(UnauthorizedAccessException){return Results.Forbid();} catch(ArgumentException){return Results.BadRequest();} }
    private static async Task<IResult> HistoryAsync(HttpContext h, Guid instanceId, int databaseId, string queryFingerprint, IQueryPerformanceApiQueryService s, WindowsGroupRoleResolver r, int limit=25, string? fromUtc=null, string? toUtc=null, string? cursor=null, CancellationToken c=default)
    { try { if (limit is <= 0 or > 200 || databaseId is <= 0 or > 32767) throw new ArgumentException("Page limit or database is outside its bounds."); RequireExplicitCursorWindow(cursor,fromUtc,toUtc); DateTimeOffset to=Parse(toUtc)??DateTimeOffset.UtcNow, from=Parse(fromUtc)??to.AddHours(-24); var q=new QueryOpaqueIdentity(databaseId,queryFingerprint); var decoded=DecodeCursor(cursor,instanceId,QueryPerformanceMetric.CpuMilliseconds,from,to); if(decoded is not null&&(decoded.DatabaseId!=databaseId||decoded.QueryFingerprint!=q.QueryFingerprint))throw new ArgumentException("Cursor query binding mismatch."); var page=await s.GetHistoryAsync(r.Resolve(h.User),new QueryHistoryRequest(new MonitoredInstanceId(instanceId),q,from,to,limit,decoded,new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),c); string? next=page.HasMore&&page.Items.Count>0?EncodeCursor(new QueryPerformanceCursorEnvelope(new MonitoredInstanceId(instanceId),databaseId,from,to,QueryPerformanceMetric.CpuMilliseconds,page.SnapshotUtc,page.Items[^1].IntervalEndUtc,q.QueryFingerprint,page.Items[^1].Metrics.CpuMilliseconds,page.Items[^1].CollectionRunId,page.Items[^1].PlanFingerprint,page.Items[^1].ObservationKey), requireMetricValue:false):null; return Results.Ok(new {targetId=instanceId,databaseId,queryFingerprint=q.QueryFingerprint,items=page.Items.Select(MapHistoryItem),nextCursor=next,snapshotUtc=page.SnapshotUtc,fromUtc=from,toUtc=to}); } catch(UnauthorizedAccessException){return Results.Forbid();} catch(ArgumentException){return Results.BadRequest();} }
    private static async Task<IResult> PlanAsync(HttpContext h, Guid instanceId, int databaseId, string planFingerprint, IQueryPerformanceApiQueryService s, WindowsGroupRoleResolver r, string? queryFingerprint=null, CancellationToken c=default)
    { try { if(queryFingerprint is null || databaseId is <= 0 or > 32767) throw new ArgumentException("Query fingerprint or database is invalid."); var q=new QueryOpaqueIdentity(databaseId,queryFingerprint); var p=new PlanOpaqueIdentity(q,planFingerprint); var row=await s.GetPlanAsync(r.Resolve(h.User),new QueryPlanMetadataRequest(new MonitoredInstanceId(instanceId),p,new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),c); return row is null?Results.NotFound():Results.Ok(new {targetId=instanceId,databaseId,queryFingerprint=q.QueryFingerprint,planFingerprint=p.PlanFingerprint,source=SourceName(row.Source),observedAtUtc=row.ObservedAtUtc,coverage=CoverageName(row.Coverage),contentAvailable=row.ContentAvailable}); } catch(UnauthorizedAccessException){return Results.Forbid();} catch(ArgumentException){return Results.BadRequest();} }
    private static async Task<IResult> TextAsync(HttpContext h, Guid instanceId, int databaseId, string queryFingerprint, Guid collectionRunId, IQueryTextReadService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        h.Response.Headers.CacheControl = "no-store";
        try
        {
            var request = new QueryTextReadRequest(new MonitoredInstanceId(instanceId), collectionRunId,
                new QueryOpaqueIdentity(databaseId, queryFingerprint), new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            QueryTextReadResult result = await service.ReadAsync(roles.Resolve(h.User), request, cancellationToken);
            return Results.Ok(new { targetId = instanceId, databaseId, queryFingerprint = request.Query.QueryFingerprint,
                collectionRunId, status = result.Status, text = result.Text });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> PlanContentAsync(HttpContext h, Guid instanceId,
        int databaseId, string queryFingerprint, Guid collectionRunId,
        string planFingerprint, IQueryPlanReadService service,
        WindowsGroupRoleResolver roles, CancellationToken cancellationToken)
    {
        h.Response.Headers.CacheControl = "no-store";
        try
        {
            var query = new QueryOpaqueIdentity(databaseId, queryFingerprint);
            var request = new QueryPlanReadRequest(new MonitoredInstanceId(instanceId),
                collectionRunId, new PlanOpaqueIdentity(query, planFingerprint),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
            QueryPlanReadResult result = await service.ReadAsync(
                roles.Resolve(h.User), request, cancellationToken);
            return Results.Ok(new { targetId = instanceId, databaseId,
                queryFingerprint = query.QueryFingerprint,
                planFingerprint = request.Plan.PlanFingerprint,
                collectionRunId, status = result.Status, xml = result.Xml });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static DateTimeOffset? Parse(string? x)=>x is null?null:DateTimeOffset.TryParse(x,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var d)&&d.Offset==TimeSpan.Zero?d:throw new ArgumentException("UTC timestamp required.");
    private static void RequireExplicitCursorWindow(string? cursor,string? fromUtc,string? toUtc){if(cursor is not null&&(fromUtc is null||toUtc is null))throw new ArgumentException("fromUtc and toUtc are required when cursor is supplied.");}
    private static QueryPerformanceMetric ParseMetric(string? x)=>x?.ToLowerInvariant() switch { "cpu" or "cpumilliseconds"=>QueryPerformanceMetric.CpuMilliseconds,"duration" or "durationmilliseconds"=>QueryPerformanceMetric.DurationMilliseconds,"executions"=>QueryPerformanceMetric.Executions,"logicalreads" or "logical_reads"=>QueryPerformanceMetric.LogicalReads,"writes"=>QueryPerformanceMetric.Writes,"rows"=>QueryPerformanceMetric.Rows,_=>throw new ArgumentException("Metric is not allowlisted.") };
    private static string MetricName(QueryPerformanceMetric value)=>value switch { QueryPerformanceMetric.CpuMilliseconds=>"cpu",QueryPerformanceMetric.DurationMilliseconds=>"duration",QueryPerformanceMetric.Executions=>"executions",QueryPerformanceMetric.LogicalReads=>"logical_reads",QueryPerformanceMetric.Writes=>"writes",_=>"rows" };
    private static string SourceName(QueryPerformanceSource value)=>value switch { QueryPerformanceSource.QueryStore=>"query_store",QueryPerformanceSource.PlanCache=>"plan_cache",QueryPerformanceSource.Unavailable=>"unavailable",_=>"mixed" };
    private static string StateName(QueryStoreState value)=>value switch { QueryStoreState.ReadWrite=>"read_write",QueryStoreState.ReadOnly=>"read_only",QueryStoreState.Disabled=>"disabled",QueryStoreState.PermissionDenied=>"permission_denied",QueryStoreState.ReadFailure=>"read_failure",QueryStoreState.TimedOut=>"timed_out",_=>"unsupported" };
    private static string SemanticsName(QueryMetricSemantics value)=>value switch { QueryMetricSemantics.QueryStoreInterval=>"query_store_interval",QueryMetricSemantics.PlanCacheCumulative=>"plan_cache_cumulative",QueryMetricSemantics.PlanCacheDelta=>"plan_cache_delta",QueryMetricSemantics.PlanCacheBaseline=>"plan_cache_baseline",_=>"reset" };
    private static string CoverageName(QueryCoverage value)=>value switch { QueryCoverage.Complete=>"complete",QueryCoverage.Truncated=>"truncated",QueryCoverage.NoActivity=>"no_activity",_=>"unavailable" };
    private static object MapTopItem(TopQueryDto item)=>new { databaseId=item.Query.DatabaseId,queryFingerprint=item.Query.QueryFingerprint,planFingerprint=item.Plan?.PlanFingerprint,source=SourceName(item.Source),sourceState=StateName(item.SourceState),metric=MetricName(item.Metric),value=item.Value,semantics=SemanticsName(item.Semantics),intervalStartUtc=item.IntervalStartUtc,intervalEndUtc=item.IntervalEndUtc,coverage=CoverageName(item.Coverage),fresh=item.Fresh,truncated=item.Truncated,contentAvailable=false,collectionRunId=item.CollectionRunId,observationKey=item.ObservationKey };
    private static object MapHistoryItem(QueryHistoryDto item)=>new { databaseId=item.Query.DatabaseId,queryFingerprint=item.Query.QueryFingerprint,planFingerprint=item.PlanFingerprint,source=SourceName(item.Source),sourceState=StateName(item.SourceState),semantics=SemanticsName(item.Semantics),cpuMilliseconds=item.Metrics.CpuMilliseconds,durationMilliseconds=item.Metrics.DurationMilliseconds,executions=item.Metrics.Executions,logicalReads=item.Metrics.LogicalReads,writes=item.Metrics.Writes,rows=item.Metrics.Rows,intervalStartUtc=item.IntervalStartUtc,intervalEndUtc=item.IntervalEndUtc,coverage=CoverageName(item.Coverage),fresh=item.Fresh,truncated=item.Truncated,contentAvailable=false,collectionRunId=item.CollectionRunId,observationKey=item.ObservationKey };
     private static string EncodeCursor(QueryPerformanceCursorEnvelope c, bool requireMetricValue = true)
    {
         if (c.CollectionRunId is null || c.ObservationKey is null || (requireMetricValue && c.MetricValue is null)) throw new ArgumentException("Cursor tie identity and metric value are required.");
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {targetId=c.TargetId.Value,databaseId=c.DatabaseId,fromUtc=c.FromUtc,toUtc=c.ToUtc,metric=(int)c.Metric,metricValue=c.MetricValue,planFingerprint=c.PlanFingerprint,observationKey=c.ObservationKey,collectionRunId=c.CollectionRunId,snapshotUtc=c.SnapshotUtc,intervalEndUtc=c.IntervalEndUtc,queryFingerprint=c.QueryFingerprint}));
        return encoded.Length <= 1024 ? encoded : throw new ArgumentException("Cursor too large.");
    }
    private static QueryPerformanceCursorEnvelope? DecodeCursor(string? x, Guid target, QueryPerformanceMetric metric, DateTimeOffset from, DateTimeOffset to, bool requireMetricValue = false)
    {
        if (x is null) return null;
        if (x.Length is 0 or > 1024 || x.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '+' or '/' or '=')) || x.Length % 4 != 0) throw new ArgumentException("Cursor too large or malformed.");
        try
        {
            using var d=JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(x)));
            var a=d.RootElement;
            if (a.ValueKind != JsonValueKind.Object) throw new ArgumentException("Cursor object required.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "targetId","databaseId","fromUtc","toUtc","metric","metricValue","planFingerprint","observationKey","collectionRunId","snapshotUtc","intervalEndUtc","queryFingerprint" };
            if (a.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Cursor contains an unknown field.");
            var required = new[] { "targetId","databaseId","fromUtc","toUtc","metric","metricValue","planFingerprint","observationKey","collectionRunId","snapshotUtc","intervalEndUtc","queryFingerprint" };
            if (required.Any(name => !a.TryGetProperty(name, out _))) throw new ArgumentException("Cursor is missing a tie field.");
            var runValue=a.GetProperty("collectionRunId");
            if (runValue.ValueKind != JsonValueKind.String || !runValue.TryGetGuid(out var run)) throw new ArgumentException("Cursor run identity is required.");
            var observationValue=a.GetProperty("observationKey");
            if (observationValue.ValueKind != JsonValueKind.String) throw new ArgumentException("Cursor observation identity is required.");
            var planValue=a.GetProperty("planFingerprint");
            string? plan=planValue.ValueKind == JsonValueKind.Null ? null : planValue.GetString();
            var value=a.GetProperty("metricValue");
            long? metricValue=value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
            if (requireMetricValue && metricValue is null) throw new ArgumentException("Top cursor metric value is required.");
            var c=new QueryPerformanceCursorEnvelope(new MonitoredInstanceId(a.GetProperty("targetId").GetGuid()),a.GetProperty("databaseId").GetInt32(),a.GetProperty("fromUtc").GetDateTimeOffset(),a.GetProperty("toUtc").GetDateTimeOffset(),(QueryPerformanceMetric)a.GetProperty("metric").GetInt32(),a.GetProperty("snapshotUtc").GetDateTimeOffset(),a.GetProperty("intervalEndUtc").GetDateTimeOffset(),a.GetProperty("queryFingerprint").GetString()??"",metricValue,run,plan,observationValue.GetString());
            if(c.TargetId.Value!=target||c.DatabaseId is <=0 or >32767||c.Metric!=metric||c.FromUtc!=from||c.ToUtc!=to||c.IntervalEndUtc<=c.FromUtc||c.IntervalEndUtc>c.ToUtc) throw new ArgumentException("Cursor binding or interval bounds mismatch.");
            return c;
        }
        catch (ArgumentException) { throw; }
        catch(Exception ex) when(ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException) { throw new ArgumentException("Cursor invalid.",ex); }
    }
}
