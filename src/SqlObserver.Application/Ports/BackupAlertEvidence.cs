using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

/// <summary>Current backup aggregate captured under the alert evaluation coordinator lease.</summary>
public sealed record BackupAlertEvidence(MonitoredInstanceId TargetId, long TargetRevision,
    DateTimeOffset ReadAtUtc, BackupFleetSummary? Summary, string? SourceDigest);

public interface IBackupAlertEvidencePort
{
    ValueTask<BackupAlertEvidence> ReadAsync(MonitoredInstanceId targetId, long targetRevision,
        WorkerLeaseIdentity lease, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}

public enum BackupAlertSignalKind { FullPolicy = 1, LogPolicy = 2, ObservedFaults = 3 }
public enum BackupAlertSignalState { Unknown = 0, Violated = 1, Clear = 2, Disabled = 3, NotRequired = 4 }
public enum BackupAlertSignalReason
{
    ViolationsObserved = 1, CompleteChecksWithinPolicy = 2, AllLogsNotRequired = 3,
    ObservedBackupFaults = 4, FaultAbsenceUnproven = 5,
    NoCurrentAggregate = 6, CollectorDisabled = 7, SourceUnavailable = 8,
    IncompleteCoverage = 9, UnknownChecks = 10, DisabledChecks = 11,
}

/// <summary>Null counts cannot be compared with zero or used to resolve an alert.</summary>
public sealed record BackupAlertSignal(BackupAlertSignalKind Kind, BackupAlertSignalState State,
    BackupAlertSignalReason Reason, int? AffectedDatabases);
