using System.Globalization;

namespace SqlObserver.Domain.Collection;

/// <summary>A sampled change, not an exact occurrence time or proof of physical failover.</summary>
public sealed record ServerLifecycleHistoryEvent(Guid EventId, Guid TargetId, long SampleTargetRevision,
    Guid BeforeRunId, Guid AfterRunId, string Kind, string AffectedIdentityFingerprint,
    DateTimeOffset BeforeObservedAtUtc, DateTimeOffset AfterObservedAtUtc, DateTimeOffset DetectedAtUtc,
    DateTimeOffset? OccurredAtUtc, int SourceSchemaVersion);

/// <summary>Immutable evidence captured for the event's own two source runs. Tick counters remain exact decimal strings.</summary>
public sealed record ServerLifecycleHistoryEngineProof(string PhysicalNodeFingerprint, string LogicalInstanceFingerprint,
    string StartupIncarnationFingerprint, int ProcessId, DateTime SourceStartLocal,
    string StartMsTicks, string CurrentMsTicks, string Coverage);

/// <summary>Completed source attempts and retained evidence. Complete/Gap partition engine-qualified source reads,
/// not AG qualification or health. LostRunCount counts canonical losses and can overlap CompleteRunCount for
/// AG-only visibility loss. ComparisonBoundaryCount includes the initial baseline; it is not a sum of failures.</summary>
public sealed record ServerLifecycleHistoryCoverage(int RunCount, int RetainedSnapshotCount,
    int CompleteRunCount, int GapRunCount, int FailedRunCount, int LostRunCount, int RetiredRunCount,
    int ComparisonBoundaryCount, int EventCount, DateTimeOffset? CaptureBoundaryUtc, bool HasVisibilityGap,
    string AvailabilityQualification, bool SourceConsistencyVerified);

public static class ServerLifecycleHistoryValidation
{
    public const int MaximumPageSize = 100;
    public const int MaximumRuns = 10_000;
    public const int MaximumEvents = 10_000;
    public const long MaximumRevision = 9_007_199_254_740_991;
    public static TimeSpan CursorLifetime => TimeSpan.FromMinutes(15);

    public static void Id(Guid value)
    { if (value == Guid.Empty) throw new ArgumentException("A nonempty lifecycle UUID is required.", nameof(value)); }
    public static void Revision(long value)
    { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value)
    { _ = DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value)); }
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    {
        Utc(from); Utc(to);
        if (from >= to || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("A nonempty UTC window of at most 31 days is required.");
    }
    public static void Digest(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase opaque SHA-256 fingerprint is required.", nameof(value));
    }
    public static void Event(ServerLifecycleHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Id(value.EventId); Id(value.TargetId); Id(value.BeforeRunId); Id(value.AfterRunId);
        Revision(value.SampleTargetRevision); Digest(value.AffectedIdentityFingerprint);
        Utc(value.BeforeObservedAtUtc); Utc(value.AfterObservedAtUtc); Utc(value.DetectedAtUtc);
        if (value.BeforeRunId == value.AfterRunId || value.BeforeObservedAtUtc >= value.AfterObservedAtUtc ||
            value.AfterObservedAtUtc > value.DetectedAtUtc || value.OccurredAtUtc is not null ||
            value.SourceSchemaVersion != ServerLifecycleSnapshot.CurrentSourceSchemaVersion ||
            value.Kind is not ("restart_detected" or "responding_node_changed"))
            throw new ArgumentException("The lifecycle event does not represent qualified sampled engine evidence.");
        ServerLifecycleEventKind kind = value.Kind == "restart_detected"
            ? ServerLifecycleEventKind.RestartDetected : ServerLifecycleEventKind.RespondingNodeChanged;
        if (value.EventId != ServerLifecycleEventIdentity.Compute(value.TargetId, value.SampleTargetRevision,
                value.SourceSchemaVersion, kind, value.BeforeRunId, value.AfterRunId, value.AffectedIdentityFingerprint))
            throw new ArgumentException("The lifecycle event UUID does not match its immutable source identity.");
    }
    public static ServerLifecycleEngineEvidence Proof(ServerLifecycleHistoryEngineProof value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Digest(value.PhysicalNodeFingerprint); Digest(value.LogicalInstanceFingerprint); Digest(value.StartupIncarnationFingerprint);
        long start = Counter(value.StartMsTicks), current = Counter(value.CurrentMsTicks);
        var evidence = new ServerLifecycleEngineEvidence(value.PhysicalNodeFingerprint, value.LogicalInstanceFingerprint,
            value.ProcessId, value.SourceStartLocal, start, current);
        if (value.Coverage != "complete" || evidence.Coverage != ServerLifecycleCoverage.Complete ||
            evidence.StartupIncarnationFingerprint != value.StartupIncarnationFingerprint)
            throw new ArgumentException("The immutable engine proof has inconsistent coverage or startup identity.");
        return evidence;
    }
    private static long Counter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result < 0 ||
            result.ToString(CultureInfo.InvariantCulture) != value)
            throw new ArgumentException("An exact nonnegative Int64 decimal counter is required.", nameof(value));
        return result;
    }
    public static void Coverage(ServerLifecycleHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.RetainedSnapshotCount < 0 || value.RetainedSnapshotCount > value.RunCount ||
            value.CompleteRunCount < 0 || value.CompleteRunCount > value.RetainedSnapshotCount ||
            value.GapRunCount < 0 || value.CompleteRunCount + value.GapRunCount != value.RunCount ||
            value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount ||
            value.LostRunCount < 0 || value.LostRunCount > value.RunCount ||
            value.RetiredRunCount < 0 || value.RetiredRunCount > value.GapRunCount ||
            value.ComparisonBoundaryCount < 0 || value.ComparisonBoundaryCount > value.RunCount ||
            value.EventCount is < 0 or > MaximumEvents || value.EventCount > value.RunCount || value.GapRunCount > 0 && !value.HasVisibilityGap ||
            value.AvailabilityQualification != "unqualified" || value.SourceConsistencyVerified)
            throw new ArgumentException("The lifecycle coverage is inconsistent or overstates source qualification.");
        if (value.CaptureBoundaryUtc is { } boundary) Utc(boundary);
    }
}
