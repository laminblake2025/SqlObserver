using SqlObserver.Domain.Collection;
using Microsoft.Data.SqlClient;

namespace SqlObserver.Infrastructure.SqlServer;

public interface IQueryPerformanceSourceReader
{
    ValueTask<QueryPerformanceReadResult> ReadQueryStoreAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken);
    ValueTask<QueryPerformanceReadResult> ReadPlanCacheAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken);
}

/// <summary>Fallback is bounded to one plan-cache attempt; cancellation/fence loss is propagated.</summary>
public sealed class QueryPerformanceFallbackCoordinator
{
    public static async ValueTask<QueryPerformanceReadResult> ReadAsync(SqlServerDatabaseIdentity database, IQueryPerformanceSourceReader reader, bool planCachePermission, CancellationToken cancellationToken, bool fenceLost = false)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested(); if (fenceLost) throw new OperationCanceledException("Collector fence was lost.");
        QueryPerformanceReadResult queryStore;
        try { queryStore = await reader.ReadQueryStoreAsync(database, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (SqlException ex) when (ex.Number == -2) { queryStore = new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreTimedOut, [], "query_store_timeout", false, false, 0, 0); }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300) { queryStore = new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStorePermissionDenied, [], "query_store_permission_denied", false, false, 0, 0); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Data.Common.DbException) { queryStore = new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreReadFailure, [], ex is TimeoutException ? "query_store_timeout" : "query_store_read_failure", false, false, 0, 0); }
        if (queryStore.Status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty) return queryStore;
        if (!planCachePermission) return queryStore;
        QueryPerformanceReadResult cache;
        try { cache = await reader.ReadPlanCacheAsync(database, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.PlanCacheTimedOut, [], "plan_cache_timeout", true, false, 0, 0, queryStore.SourceState); }
        catch (SqlException ex) when (ex.Number == -2) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.PlanCacheTimedOut, [], "plan_cache_timeout", true, false, 0, 0, queryStore.SourceState); }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.PlanCachePermissionDenied, [], "plan_cache_permission_denied", true, false, 0, 0, queryStore.SourceState); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.PlanCacheReadFailure, [], "plan_cache_read_failure", true, false, 0, 0, queryStore.SourceState); }
        // Preserve the Query Store probe/read state when reporting fallback rows;
        // the fallback source is explicit, but its trigger must remain visible.
        return new QueryPerformanceReadResult(database, cache.Status, cache.Observations, cache.Reason, true, cache.Truncated, cache.SourceRowsRead, cache.ResponseBytes, queryStore.SourceState ?? cache.SourceState, cache.LossKind, cache.MinimumLostItems, cache.LossCountIsExact, cache.MinimumLostBytes);
    }
}
