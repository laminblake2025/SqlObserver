namespace SqlObserver.Domain.Collection;

/// <summary>All six opaque JSON inputs charged by the atomic cache-content writer.</summary>
public static class QueryPerformanceV3PersistenceBudget
{
    public static int Measure(IReadOnlyList<QueryPerformanceObservation> observations,
        IReadOnlyList<QueryPerformanceDatabaseStatus> statuses, PlanCacheRuntimeOutputBatch? cache,
        PlanCacheContentOutputBatch? content, QueryPerformanceTargetStatus? targetStatus = null) =>
        checked(QueryPerformanceV2PersistenceBudget.Measure(observations, statuses, cache, targetStatus)
            + (content is null ? 0 : PlanCacheContentPersistencePayload.Measure(content)));
}
