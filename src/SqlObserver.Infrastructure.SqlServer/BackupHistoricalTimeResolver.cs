using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

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
        if (fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero
            || fromUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0 || toUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ArgumentException("The qualified interval must be UTC and microsecond aligned.");
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

public static class BackupHistoricalTimeResolver
{
    public static BackupFinishTimeResolution Resolve(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, DateTime? startLocal, DateTime? finishLocal,
        short? recordedStartOffsetMinutes, string? sourceZoneId,
        BackupHistoricalZoneQualification? qualification)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (startLocal is { Kind: not DateTimeKind.Unspecified } || finishLocal is { Kind: not DateTimeKind.Unspecified })
            throw new ArgumentException("Backup source timestamps must be offset-free.");
        if (qualification is not null && (qualification.TargetId != targetId || qualification.TargetRevision != targetRevision))
            throw new ArgumentException("Historical zone qualification belongs to a different target/revision.", nameof(qualification));
        if (recordedStartOffsetMinutes is { } recorded && (recorded is < -720 or > 720 || recorded % 15 != 0))
            throw new ArgumentOutOfRangeException(nameof(recordedStartOffsetMinutes));
        if (!finishLocal.HasValue) return Unknown(BackupFinishTimeState.FinishMissing);
        if (!startLocal.HasValue) return Unknown(BackupFinishTimeState.StartMissing);
        if (qualification is null) return Unknown(BackupFinishTimeState.QualificationMissing);
        if (!recordedStartOffsetMinutes.HasValue) return Unknown(BackupFinishTimeState.RecordedOffsetUnknown);
        if (sourceZoneId is not null && !string.Equals(sourceZoneId, qualification.SourceZoneId, StringComparison.Ordinal))
            return Unknown(BackupFinishTimeState.SourceZoneMismatch);

        TimeZoneInfo zone = qualification.Zone;
        DateTime start = startLocal.Value, finish = finishLocal.Value;
        if (zone.IsInvalidTime(start) || zone.IsInvalidTime(finish)) return Unknown(BackupFinishTimeState.InvalidLocalTime);
        // Do not let the framework silently choose standard time for an ambiguous endpoint.
        if (zone.IsAmbiguousTime(start) || zone.IsAmbiguousTime(finish)) return Unknown(BackupFinishTimeState.AmbiguousLocalTime);
        TimeSpan startOffset = zone.GetUtcOffset(start);
        if (startOffset != TimeSpan.FromMinutes(recordedStartOffsetMinutes.Value))
            return Unknown(BackupFinishTimeState.RecordedOffsetMismatch);
        DateTimeOffset startUtc, finishUtc;
        try
        {
            // DateTimeOffset rejects out-of-range instants instead of clamping at the calendar boundary.
            startUtc = new DateTimeOffset(start, startOffset).ToUniversalTime();
            finishUtc = new DateTimeOffset(finish, zone.GetUtcOffset(finish)).ToUniversalTime();
        }
        catch (ArgumentException) { return Unknown(BackupFinishTimeState.InvalidLocalTime); }
        if (finishUtc < startUtc) return Unknown(BackupFinishTimeState.FinishBeforeStart);
        if (startUtc < qualification.FromUtc || startUtc >= qualification.ToUtc
            || finishUtc < qualification.FromUtc || finishUtc >= qualification.ToUtc)
            return Unknown(BackupFinishTimeState.OutsideQualifiedWindow);
        finishUtc = finishUtc.AddTicks(-finishUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        return new(BackupFinishTimeState.Resolved, finishUtc, qualification.Revision);

        BackupFinishTimeResolution Unknown(BackupFinishTimeState state) => new(state, null, qualification?.Revision);
    }
}
