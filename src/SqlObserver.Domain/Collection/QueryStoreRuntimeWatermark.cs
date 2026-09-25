using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>The identity of one Query Store runtime counter group, including database incarnation.</summary>
public sealed record QueryStoreRuntimeWatermarkKey
{
    public QueryStoreRuntimeWatermarkKey(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        Guid databaseIncarnation, QueryOpaqueIdentity query, PlanOpaqueIdentity plan,
        DateTimeOffset planInitialCompileUtc, long sourceIntervalId, DateTimeOffset intervalStartUtc,
        DateTimeOffset intervalEndUtc, int executionType)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (databaseIncarnation == Guid.Empty) throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        Query = query ?? throw new ArgumentNullException(nameof(query));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (plan.Query != query) throw new ArgumentException("The plan must belong to the query.", nameof(plan));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceIntervalId);
        ArgumentOutOfRangeException.ThrowIfNegative(executionType);
        if (planInitialCompileUtc.Offset != TimeSpan.Zero || intervalStartUtc.Offset != TimeSpan.Zero ||
            intervalEndUtc.Offset != TimeSpan.Zero || intervalEndUtc <= intervalStartUtc ||
            intervalEndUtc - intervalStartUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Query Store interval identity must be bounded UTC time.");
        DatabaseIncarnation = databaseIncarnation;
        PlanInitialCompileUtc = planInitialCompileUtc;
        SourceIntervalId = sourceIntervalId;
        IntervalStartUtc = intervalStartUtc;
        IntervalEndUtc = intervalEndUtc;
        ExecutionType = executionType;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public Guid DatabaseIncarnation { get; }
    public QueryOpaqueIdentity Query { get; }
    public PlanOpaqueIdentity Plan { get; }
    public DateTimeOffset PlanInitialCompileUtc { get; }
    public long SourceIntervalId { get; }
    public DateTimeOffset IntervalStartUtc { get; }
    public DateTimeOffset IntervalEndUtc { get; }
    public int ExecutionType { get; }
}

/// <summary>Complete cumulative runtime counters for one source group.</summary>
public sealed record QueryStoreRuntimeCounters
{
    public QueryStoreRuntimeCounters(long cpuMilliseconds, long durationMilliseconds, long executions,
        long logicalReads, long writes, long rows)
    {
        if (cpuMilliseconds < 0 || durationMilliseconds < 0 || executions < 0 ||
            logicalReads < 0 || writes < 0 || rows < 0)
            throw new ArgumentOutOfRangeException(nameof(cpuMilliseconds), "Query Store counters must be nonnegative.");
        CpuMilliseconds = cpuMilliseconds;
        DurationMilliseconds = durationMilliseconds;
        Executions = executions;
        LogicalReads = logicalReads;
        Writes = writes;
        Rows = rows;
    }

    public long CpuMilliseconds { get; }
    public long DurationMilliseconds { get; }
    public long Executions { get; }
    public long LogicalReads { get; }
    public long Writes { get; }
    public long Rows { get; }

    internal bool IsBelow(QueryStoreRuntimeCounters previous) =>
        CpuMilliseconds < previous.CpuMilliseconds || DurationMilliseconds < previous.DurationMilliseconds ||
        Executions < previous.Executions || LogicalReads < previous.LogicalReads ||
        Writes < previous.Writes || Rows < previous.Rows;

    internal QueryStoreRuntimeCounters Subtract(QueryStoreRuntimeCounters previous) =>
        new(CpuMilliseconds - previous.CpuMilliseconds, DurationMilliseconds - previous.DurationMilliseconds,
            Executions - previous.Executions, LogicalReads - previous.LogicalReads,
            Writes - previous.Writes, Rows - previous.Rows);
}

public sealed record QueryStoreRuntimeWatermark
{
    public QueryStoreRuntimeWatermark(QueryStoreRuntimeWatermarkKey key, QueryStoreRuntimeCounters counters,
        DateTimeOffset firstExecutionUtc, DateTimeOffset lastExecutionUtc, DateTimeOffset observedAtUtc)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
        if (firstExecutionUtc.Offset != TimeSpan.Zero || lastExecutionUtc.Offset != TimeSpan.Zero ||
            observedAtUtc.Offset != TimeSpan.Zero || lastExecutionUtc < firstExecutionUtc)
            throw new ArgumentException("Query Store execution and observation times must be ordered UTC values.");
        FirstExecutionUtc = firstExecutionUtc;
        LastExecutionUtc = lastExecutionUtc;
        ObservedAtUtc = observedAtUtc;
    }

    public QueryStoreRuntimeWatermarkKey Key { get; }
    public QueryStoreRuntimeCounters Counters { get; }
    public DateTimeOffset FirstExecutionUtc { get; }
    public DateTimeOffset LastExecutionUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public enum QueryStoreRuntimeTransitionKind { Incomplete = 1, BaselineUnavailable = 2, Comparable = 3, Reset = 4, Stale = 5 }

public sealed record QueryStoreRuntimeTransition(QueryStoreRuntimeTransitionKind Kind,
    QueryStoreRuntimeCounters? Delta, QueryStoreRuntimeWatermark? NextWatermark);

/// <summary>Pure comparison for the later fenced, atomic Query Store watermark writer.</summary>
public static class QueryStoreRuntimeWatermarkCalculator
{
    public static QueryStoreRuntimeTransition Compare(QueryStoreRuntimeWatermark? previous,
        QueryStoreRuntimeWatermark current, bool sourceComplete)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null && previous.Key != current.Key)
            throw new ArgumentException("Watermark identities do not match.", nameof(previous));
        if (!sourceComplete)
            return new(QueryStoreRuntimeTransitionKind.Incomplete, null, null);
        if (previous is null)
            return new(QueryStoreRuntimeTransitionKind.BaselineUnavailable, null, current);
        if (current.ObservedAtUtc <= previous.ObservedAtUtc)
            return new(QueryStoreRuntimeTransitionKind.Stale, null, null);
        if (current.Counters.IsBelow(previous.Counters) || current.FirstExecutionUtc > previous.LastExecutionUtc)
            return new(QueryStoreRuntimeTransitionKind.Reset, null, current);
        return new(QueryStoreRuntimeTransitionKind.Comparable, current.Counters.Subtract(previous.Counters), current);
    }
}
