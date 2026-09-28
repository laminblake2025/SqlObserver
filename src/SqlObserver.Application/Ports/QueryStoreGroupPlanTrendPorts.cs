using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class QueryStoreGroupPlanTrendRequest
{
    public QueryStoreGroupPlanTrendRequest(MonitoredInstanceId targetId,
        PlanOpaqueIdentity plan, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Group trend requires a UTC window of at most seven days.");
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Timeout = timeout;
    }

    public MonitoredInstanceId TargetId { get; }
    public PlanOpaqueIdentity Plan { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}

public sealed record QueryStoreGroupPlanTrendPoint(
    DateTimeOffset BucketStartUtc, long SourceRows,
    long RuntimeComparableRows, long RuntimeBaselineRows,
    long RuntimeResetRows, long RuntimeAmbiguousRows,
    string? CpuMilliseconds, string? DurationMilliseconds,
    string? Executions, string? LogicalReads,
    string? Writes, string? Rows,
    long WaitComparableRows, long WaitBaselineRows,
    long WaitResetRows, long WaitUnavailableRows,
    long WaitStaleRows, IReadOnlyDictionary<int, string> WaitCategories);

public sealed record QueryStoreGroupPlanTrendResult(
    MonitoredInstanceId TargetId, PlanOpaqueIdentity Plan,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    IReadOnlyList<QueryStoreGroupPlanTrendPoint> Points);

public interface IQueryStoreGroupPlanTrendRepositoryPort
{
    ValueTask<QueryStoreGroupPlanTrendResult> ReadAsync(
        QueryStoreGroupPlanTrendRequest request, CancellationToken cancellationToken);
}
