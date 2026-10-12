using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed class PlanCacheIntervalRankingRequest
{
    public PlanCacheIntervalRankingRequest(MonitoredInstanceId targetId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, QueryPerformanceMetric metric, int limit, RepositoryCallTimeout timeout)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || fromUtc.Ticks % 10 != 0 ||
            toUtc.Ticks % 10 != 0 || toUtc <= fromUtc ||
            toUtc - fromUtc > TimeSpan.FromDays(7) || limit is < 1 or > 100 || !Enum.IsDefined(metric))
            throw new ArgumentException("Cache rankings require UTC bounds at whole microseconds, an increasing window of at most seven days, a valid metric and 1–100 results.");
        FromUtc = fromUtc; ToUtc = toUtc; Metric = metric; Limit = limit;
    }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public QueryPerformanceMetric Metric { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }

    public string MetricName => Metric switch
    {
        QueryPerformanceMetric.CpuMilliseconds => "cpu",
        QueryPerformanceMetric.DurationMilliseconds => "duration",
        QueryPerformanceMetric.Executions => "executions",
        QueryPerformanceMetric.LogicalReads => "logical_reads",
        QueryPerformanceMetric.Writes => "writes",
        QueryPerformanceMetric.Rows => "rows",
        _ => throw new InvalidDataException("Cache ranking metric is invalid."),
    };
}

public sealed record PlanCacheIntervalRankedQuery(int DatabaseId, string DatabaseIncarnation,
    string QueryFingerprint, string PlanFingerprint, string CpuMilliseconds, string DurationMilliseconds,
    string Executions, string LogicalReads, string Writes, string Rows, long ComparableRows,
    DateTimeOffset FirstObservedAtUtc, DateTimeOffset LastObservedAtUtc);

public sealed record PlanCacheIntervalRankingResult(Guid TargetId, DateTimeOffset FromUtc,
    DateTimeOffset ToUtc, string Semantics, string Metric, string Coverage, long SourceFrames,
    long IncompleteFrames, long ComparableRows, long UnavailableRows, long BoundaryRows,
    bool HasMore, IReadOnlyList<PlanCacheIntervalRankedQuery> Items);

public interface IPlanCacheIntervalRankingRepositoryPort
{
    ValueTask<PlanCacheIntervalRankingResult> ReadAsync(PlanCacheIntervalRankingRequest request,
        CancellationToken cancellationToken);
}
