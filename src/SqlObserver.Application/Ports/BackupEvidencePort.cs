using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum BackupEvidenceStatus
{
    NotCollected = 1, CollectionFailed = 2, EvidenceNotRetained = 3,
    WithheldByLimit = 4, NoData = 5, Retained = 6,
}

public sealed record BackupEvidenceCursor(MonitoredInstanceId TargetId, DateTimeOffset AtUtc,
    long TargetRevision, Guid RunId, int DatabaseOrdinal, long PolicyHistoryCount = 0, long QualificationCommandRevision = 0);

public sealed class BackupEvidenceRequest
{
    public BackupEvidenceRequest(MonitoredInstanceId targetId, DateTimeOffset atUtc, int limit,
        RepositoryCallTimeout timeout, long? targetRevision = null, BackupEvidenceCursor? cursor = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        Timeout = timeout ?? throw new ArgumentNullException(nameof(timeout));
        if (atUtc.Offset != TimeSpan.Zero || atUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0 || atUtc < DateTimeOffset.MinValue.AddDays(7))
            throw new ArgumentException("Backup cutoff must be microsecond-aligned UTC with a seven-day lookback.", nameof(atUtc));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (targetRevision is <= 0) throw new ArgumentOutOfRangeException(nameof(targetRevision));
        if (cursor is not null && (cursor.TargetId != targetId || cursor.AtUtc != atUtc || cursor.TargetRevision <= 0 ||
            cursor.RunId == Guid.Empty || cursor.DatabaseOrdinal is < 1 or > 512 || cursor.PolicyHistoryCount < 0 || cursor.QualificationCommandRevision < 0 ||
            targetRevision.HasValue && targetRevision != cursor.TargetRevision))
            throw new ArgumentException("Backup cursor does not match the target, cutoff or revision.", nameof(cursor));
        AtUtc = atUtc; Limit = limit; TargetRevision = targetRevision ?? cursor?.TargetRevision; Cursor = cursor;
    }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset AtUtc { get; }
    public int Limit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public long? TargetRevision { get; }
    public BackupEvidenceCursor? Cursor { get; }
}

public sealed record BackupEvidenceHeader(MonitoredInstanceId TargetId, DateTimeOffset AtUtc,
    long TargetRevision, Guid? RunId, DateTimeOffset? SourceObservedAtUtc, DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ReceivedAtUtc, string? SourceZoneId, DateTime? HistoryLowerBoundLocal,
    BackupFreshnessCoverage Coverage, BackupEvidenceStatus EvidenceStatus,
    int SourceRows, int OutputItems, long RetainedItems, int DatabaseCount, bool CollectorEnabled,
    long PolicyHistoryCount = 0, long QualificationCommandRevision = 0);

/// <summary>Immutable recorded evidence remains visible after qualification revocation; verdicts use only trusted times.</summary>
public sealed record BackupDatabaseEvidence(int DatabaseOrdinal, BackupV2DatabaseRead Evidence,
    BackupFreshnessPolicy? Policy, IReadOnlyList<bool> QualificationTrusted);
public sealed record BackupEvidencePage(BackupEvidenceHeader Header, IReadOnlyList<BackupDatabaseEvidence> Items,
    BackupEvidenceCursor? NextCursor);
public sealed record BackupDatabaseAssessment(BackupDatabaseEvidence Source, long? PolicyRevision,
    BackupFreshnessCheck Full, BackupFreshnessCheck Log, IReadOnlyList<BackupFreshnessIssue> ObservedIssues);
public sealed record BackupAssessmentPage(BackupEvidenceHeader Header, IReadOnlyList<BackupDatabaseAssessment> Items,
    BackupEvidenceCursor? NextCursor);
public sealed class BackupEvidenceCursorStaleException(Exception? innerException = null)
    : Exception("Backup evidence changed or expired; restart from the first page.", innerException);
public interface IBackupEvidencePort
{
    ValueTask<BackupEvidencePage> ReadAsync(BackupEvidenceRequest request, CancellationToken cancellationToken);
}
