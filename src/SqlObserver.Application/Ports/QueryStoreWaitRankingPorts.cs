using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class QueryStoreWaitRankingRequest
{
    public QueryStoreWaitRankingRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit,
        RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) ||
            limit is < 1 or > 100)
            throw new ArgumentException("Query wait ranking requires a UTC window of at most seven days and at most 100 plans.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Limit = limit;
    }

    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record QueryStoreWaitRankedPlan(
    PlanOpaqueIdentity Plan, string WaitMilliseconds,
    long ComparableRows, long SourceRows, DateTimeOffset LastObservedAtUtc);

public sealed record QueryStoreWaitRankingResult(
    MonitoredInstanceId TargetId, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, IReadOnlyList<QueryStoreWaitRankedPlan> Items);

public interface IQueryStoreWaitRankingRepositoryPort
{
    ValueTask<QueryStoreWaitRankingResult> ReadAsync(
        QueryStoreWaitRankingRequest request, CancellationToken cancellationToken);
}
