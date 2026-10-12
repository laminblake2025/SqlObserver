using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>One statement's cache incarnation; all source handles remain opaque.</summary>
public sealed record PlanCacheRuntimeWatermarkKey
{
    public PlanCacheRuntimeWatermarkKey(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, QueryOpaqueIdentity query,
        PlanOpaqueIdentity plan, string databaseIncarnation, string sourceEpoch,
        string cacheEntryFingerprint)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        Query = query ?? throw new ArgumentNullException(nameof(query));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (plan.Query != query)
            throw new ArgumentException("The plan must belong to the sampled query.", nameof(plan));
        DatabaseIncarnation = QueryOpaqueIdentity.RequireDigest(databaseIncarnation, nameof(databaseIncarnation));
        SourceEpoch = QueryOpaqueIdentity.RequireDigest(sourceEpoch, nameof(sourceEpoch));
        CacheEntryFingerprint = QueryOpaqueIdentity.RequireDigest(cacheEntryFingerprint, nameof(cacheEntryFingerprint));
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public QueryOpaqueIdentity Query { get; }
    public PlanOpaqueIdentity Plan { get; }
    public string DatabaseIncarnation { get; }
    public string SourceEpoch { get; }
    public string CacheEntryFingerprint { get; }
}

/// <summary>Unrounded cumulative DMV counters for a single cached statement.</summary>
public sealed record PlanCacheRuntimeCounters
{
    public PlanCacheRuntimeCounters(long cpuMicroseconds, long durationMicroseconds,
        long executions, long logicalReads, long writes, long rows)
    {
        if (cpuMicroseconds < 0 || durationMicroseconds < 0 || executions < 0 ||
            logicalReads < 0 || writes < 0 || rows < 0)
            throw new ArgumentOutOfRangeException(nameof(cpuMicroseconds), "Cache counters must be nonnegative.");
        CpuMicroseconds = cpuMicroseconds;
        DurationMicroseconds = durationMicroseconds;
        Executions = executions;
        LogicalReads = logicalReads;
        Writes = writes;
        Rows = rows;
    }

    public long CpuMicroseconds { get; }
    public long DurationMicroseconds { get; }
    public long Executions { get; }
    public long LogicalReads { get; }
    public long Writes { get; }
    public long Rows { get; }

    public QueryPerformanceMetricSet ToMilliseconds() => new(
        CpuMicroseconds / 1000, DurationMicroseconds / 1000, Executions, LogicalReads, Writes, Rows);

    internal bool IsBelow(PlanCacheRuntimeCounters previous) =>
        CpuMicroseconds < previous.CpuMicroseconds || DurationMicroseconds < previous.DurationMicroseconds ||
        Executions < previous.Executions || LogicalReads < previous.LogicalReads ||
        Writes < previous.Writes || Rows < previous.Rows;

    internal PlanCacheRuntimeCounters Subtract(PlanCacheRuntimeCounters previous) => new(
        CpuMicroseconds - previous.CpuMicroseconds, DurationMicroseconds - previous.DurationMicroseconds,
        Executions - previous.Executions, LogicalReads - previous.LogicalReads,
        Writes - previous.Writes, Rows - previous.Rows);
}

public sealed record PlanCacheRuntimeWatermark
{
    public PlanCacheRuntimeWatermark(PlanCacheRuntimeWatermarkKey key,
        PlanCacheRuntimeCounters counters, DateTimeOffset observedAtUtc)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Counters = counters ?? throw new ArgumentNullException(nameof(counters));
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The cache observation time must be UTC.", nameof(observedAtUtc));
        ObservedAtUtc = observedAtUtc;
    }

    public PlanCacheRuntimeWatermarkKey Key { get; }
    public PlanCacheRuntimeCounters Counters { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public enum PlanCacheRuntimeTransitionKind
{
    Incomplete = 1,
    BaselineUnavailable = 2,
    Comparable = 3,
    Reset = 4,
    Stale = 5,
    ComparisonWindowExpired = 6,
}

public sealed record PlanCacheRuntimeTransition(
    PlanCacheRuntimeTransitionKind Kind, PlanCacheRuntimeCounters? Delta,
    PlanCacheRuntimeWatermark? NextWatermark, DateTimeOffset? DeltaFromUtc,
    DateTimeOffset? DeltaToUtc);

/// <summary>Comparison contract for the forthcoming fenced cache delta writer.</summary>
public static class PlanCacheRuntimeWatermarkCalculator
{
    // Matches the public query observation's maximum interval. An expired
    // baseline cannot produce a delta that the existing API would misrepresent.
    public static readonly TimeSpan MaximumComparisonWindow = TimeSpan.FromDays(7);

    public static PlanCacheRuntimeTransition Compare(PlanCacheRuntimeWatermark? previous,
        PlanCacheRuntimeWatermark current, bool sourceComplete)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null && (previous.Key.TargetId != current.Key.TargetId ||
            previous.Key.Query != current.Key.Query))
            throw new ArgumentException("Cache watermarks must belong to the same target and query.", nameof(previous));
        if (!sourceComplete)
            return new(PlanCacheRuntimeTransitionKind.Incomplete, null, null, null, null);
        if (previous is null)
            return new(PlanCacheRuntimeTransitionKind.BaselineUnavailable, null, current, null, null);
        if (current.ObservedAtUtc <= previous.ObservedAtUtc ||
            current.Key.TargetRevision.Value < previous.Key.TargetRevision.Value)
            return new(PlanCacheRuntimeTransitionKind.Stale, null, null, null, null);
        if (previous.Key != current.Key)
            return new(PlanCacheRuntimeTransitionKind.BaselineUnavailable, null, current, null, null);
        if (current.ObservedAtUtc - previous.ObservedAtUtc > MaximumComparisonWindow)
            return new(PlanCacheRuntimeTransitionKind.ComparisonWindowExpired, null, current, null, null);
        if (current.Counters.IsBelow(previous.Counters))
            return new(PlanCacheRuntimeTransitionKind.Reset, null, current, null, null);
        return new(PlanCacheRuntimeTransitionKind.Comparable, current.Counters.Subtract(previous.Counters),
            current, previous.ObservedAtUtc, current.ObservedAtUtc);
    }
}
