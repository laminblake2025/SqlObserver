using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class QueryStorePlanRegressionRequest
{
    public QueryStorePlanRegressionRequest(MonitoredInstanceId targetId,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit,
        RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero ||
            toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) ||
            limit is < 1 or > 100)
            throw new ArgumentException("Plan regressions require a UTC window of at most seven days and at most 100 results.");
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

public sealed record QueryStorePlanRegression(
    QueryOpaqueIdentity Query, string PreviousPlanFingerprint,
    string CurrentPlanFingerprint, string PreviousAverageMilliseconds,
    string CurrentAverageMilliseconds, string SlowdownRatio,
    string PreviousExecutions, string CurrentExecutions,
    long PreviousComparableRows, long PreviousSourceRows,
    long CurrentComparableRows, long CurrentSourceRows,
    DateTimeOffset LastObservedAtUtc);

public sealed record QueryStorePlanRegressionResult(
    MonitoredInstanceId TargetId, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, IReadOnlyList<QueryStorePlanRegression> Items);

public interface IQueryStorePlanRegressionRepositoryPort
{
    ValueTask<QueryStorePlanRegressionResult> ReadAsync(
        QueryStorePlanRegressionRequest request, CancellationToken cancellationToken);
}
