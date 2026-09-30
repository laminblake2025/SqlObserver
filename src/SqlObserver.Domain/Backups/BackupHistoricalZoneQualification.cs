using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Backups;

public enum BackupFinishTimeState
{
    Resolved = 1, FinishMissing = 2, StartMissing = 3, QualificationMissing = 4,
    RecordedOffsetUnknown = 5, SourceZoneMismatch = 6, InvalidLocalTime = 7,
    AmbiguousLocalTime = 8, RecordedOffsetMismatch = 9, OutsideQualifiedWindow = 10,
    FinishBeforeStart = 11,
}

public sealed record BackupFinishTimeResolution(BackupFinishTimeState State,
    DateTimeOffset? FinishUtc, long? QualificationRevision);

/// <summary>
/// Explicit target/revision qualification of historical zone configuration and rules.
/// A current SQL zone ID alone is insufficient to construct this evidence.
/// </summary>
public sealed class BackupHistoricalZoneQualification
{
    public BackupHistoricalZoneQualification(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, long revision, TimeZoneInfo zone,
        string sourceZoneId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        Zone = zone ?? throw new ArgumentNullException(nameof(zone));
        if (string.IsNullOrWhiteSpace(sourceZoneId) || sourceZoneId.Length > 128 || sourceZoneId.Any(char.IsControl))
            throw new ArgumentException("A bounded qualified SQL source zone ID is required.", nameof(sourceZoneId));
        DomainValidation.RequireUtcMicrosecondAligned(fromUtc, nameof(fromUtc));
        DomainValidation.RequireUtcMicrosecondAligned(toUtc, nameof(toUtc));
        if (toUtc <= fromUtc) throw new ArgumentException("The qualified interval must be nonempty.", nameof(toUtc));
        Revision = revision; SourceZoneId = sourceZoneId; FromUtc = fromUtc; ToUtc = toUtc;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public long Revision { get; }
    public TimeZoneInfo Zone { get; }
    public string SourceZoneId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
}
