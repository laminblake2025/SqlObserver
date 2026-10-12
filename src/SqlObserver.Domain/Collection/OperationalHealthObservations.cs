using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Closed, non-sensitive M9 operational-health states.</summary>
public enum OperationalObservationState { Complete = 1, Partial = 2, Degraded = 3, Unsupported = 4, PermissionDenied = 5, NoData = 6 }
public enum BackupKind { Full = 1, Differential = 2, Log = 3 }
public enum BackupCoverage { Complete = 1, NotSeenWithin35Days = 2, Truncated = 3, Unknown = 4 }
public enum AgentFailureKind { Failed = 1, Retry = 2, Cancelled = 3 }
public enum TempDbComponentState { Healthy = 1, Warning = 2, Unknown = 3, Invalid = 4 }
public enum AvailabilityVisibilityScope { PrimaryAllKnown = 1, SecondaryLocalOnly = 2, ResolvingLocalOnly = 3 }
public interface IOperationalHealthSnapshot { }

public sealed record BackupStatusObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    string DatabaseFingerprint,
    BackupKind Kind,
    DateTimeOffset? LastFinishUtc,
    DateTime? SourceLocalFinish,
    bool SourceTimeUnknown,
    long? SizeBytes,
    bool? CopyOnly,
    bool? HasChecksum,
    bool? IsDamaged,
    BackupCoverage Coverage)
{
    public long? BackupSetId { get; init; }
    public bool ContentAvailable => TargetId is not null && false;
}

public sealed partial record BackupStatusSnapshot(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    CollectorRunId? RunId,
    DateTimeOffset ObservedAtUtc,
    OperationalObservationState State,
    IReadOnlyList<BackupStatusObservation> Items,
    int SourceRowsRead,
    bool Truncated) : IOperationalHealthSnapshot;
public sealed partial record BackupStatusSnapshot { public string? NextCursor { get; init; } }

public sealed record SqlAgentFailureObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    Guid JobId,
    long HistoryInstanceId,
    int StepId,
    int RunStatus,
    AgentFailureKind FailureKind,
    int? MessageId,
    int? Severity,
    int RetryAttempt,
    int DurationSeconds,
    DateTimeOffset DetectedAtUtc,
    string FailureFingerprint)
{
    private DateTime? sourceLocalStart;

    /// <summary>Source wall-clock execution start; no UTC offset or time zone is implied.</summary>
    public DateTime? SourceLocalStart
    {
        get => sourceLocalStart;
        init
        {
            if (value is { } local && (local.Kind != DateTimeKind.Unspecified || local.Ticks % TimeSpan.TicksPerSecond != 0))
                throw new ArgumentException("Agent source start must be an offset-free whole-second timestamp.", nameof(value));
            sourceLocalStart = value;
        }
    }

    // Source-compatible adapter for pre-privacy test fixtures; the two local
    // wall-clock arguments are intentionally discarded and never become part
    // of the domain state.
    public SqlAgentFailureObservation(
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, Guid jobId,
        long historyInstanceId, int stepId, int runStatus, AgentFailureKind failureKind,
        int? messageId, int? severity, int retryAttempt, int durationSeconds,
        DateTime _, TimeSpan __, DateTimeOffset detectedAtUtc, string failureFingerprint)
        : this(targetId, targetRevision, jobId, historyInstanceId, stepId, runStatus, failureKind,
            messageId, severity, retryAttempt, durationSeconds, detectedAtUtc, failureFingerprint) { }

    /// <summary>Repository UTC first-observed timestamp.</summary>
    public DateTimeOffset FirstObservedAtUtc => DetectedAtUtc;
    public bool ContentAvailable => TargetId is not null && false;
}

public sealed partial record SqlAgentFailureSnapshot(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    CollectorRunId? RunId,
    DateTimeOffset ObservedAtUtc,
    OperationalObservationState State,
    IReadOnlyList<SqlAgentFailureObservation> Items,
    int SourceRowsRead,
    bool Truncated,
    DateTimeOffset? CoverageFromUtc,
    DateTimeOffset? CoverageToUtc) : IOperationalHealthSnapshot;
public sealed partial record SqlAgentFailureSnapshot { public string? NextCursor { get; init; } }

public sealed record TempDbFileObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    int FileId,
    long SizeBytes,
    long UsedBytes,
    long FreeBytes,
    TempDbComponentState State);

public sealed partial record TempDbSnapshot(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    CollectorRunId? RunId,
    DateTimeOffset ObservedAtUtc,
    OperationalObservationState State,
    long? TotalBytes,
    long? UsedBytes,
    long? LogTotalBytes,
    long? LogUsedBytes,
    IReadOnlyList<TempDbFileObservation> Files,
    bool Truncated) : IOperationalHealthSnapshot;
public sealed partial record TempDbSnapshot { public string? NextCursor { get; init; } }

public sealed record AvailabilityReplicaObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    string GroupFingerprint,
    string ReplicaFingerprint,
    string Role,
    string OperationalState,
    string ConnectedState,
    AvailabilityVisibilityScope VisibilityScope,
    bool StateAvailable);

public sealed record AvailabilityDatabaseObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    string GroupFingerprint,
    string DatabaseFingerprint,
    string SynchronizationState,
    string DatabaseState,
    AvailabilityVisibilityScope VisibilityScope,
    bool StateAvailable);

public sealed partial record AvailabilityGroupsSnapshot(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    CollectorRunId? RunId,
    DateTimeOffset ObservedAtUtc,
    OperationalObservationState State,
    AvailabilityVisibilityScope VisibilityScope,
    IReadOnlyList<AvailabilityReplicaObservation> Replicas,
    IReadOnlyList<AvailabilityDatabaseObservation> Databases,
    bool Truncated) : IOperationalHealthSnapshot;
public sealed partial record AvailabilityGroupsSnapshot
{
    // The combined summary has independent child streams.  A replica cursor
    // must never be replayed against the database stream (or vice versa).
    public string? NextCursor { get; init; }
    public string? ReplicasNextCursor { get; init; }
    public string? DatabasesNextCursor { get; init; }
}
