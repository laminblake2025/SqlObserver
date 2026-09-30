using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Backups;

public enum BackupRecoveryModel { Unknown = 0, Simple = 1, Full = 2, BulkLogged = 3 }
public enum BackupFreshnessScope { Unknown = 0, Standalone = 1, AvailabilityGroupLocalOnly = 2, AvailabilityGroupAllReplicas = 3 }
public enum BackupFreshnessCoverage { NotCollected = 0, Complete = 1, Partial = 2, Truncated = 3, PermissionDenied = 4, Failed = 5, Unsupported = 6 }
public enum FullBackupRequirement { FullImage = 1, DifferentialBase = 2 }
public enum BackupFreshnessStatus { Indeterminate = 0, WithinThreshold = 1, PolicyViolated = 2, NotRequired = 3, Disabled = 4 }
public enum BackupFreshnessReason
{
    NotCollected = 0, RecentBackup = 1, AgeExceeded = 2, BackupNotObserved = 3,
    SourceIncomplete = 4, SourceTruncated = 5, PermissionDenied = 6, CollectionFailed = 7,
    Unsupported = 8, StaleObservation = 9, FutureSourceTime = 10, TopologyUnknown = 11,
    OtherReplicaEvidenceRequired = 12, RecoveryModelUnknown = 13, LogThresholdNotConfigured = 14,
    FinishTimeUnknown = 15, BackupMetadataUnknown = 16, BackupDamaged = 17,
    ChecksumMissing = 18, ChecksumUnknown = 19, SimpleRecovery = 20, PolicyDisabled = 21,
}

/// <summary>Explicit policy for a current target/database identity. No shipped RPO is assumed.</summary>
public sealed class BackupFreshnessPolicy
{
    public static readonly TimeSpan MaximumBackupAge = TimeSpan.FromDays(30);

    public BackupFreshnessPolicy(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        string databaseFingerprint, long policyRevision, bool enabled,
        TimeSpan maximumFullAge, TimeSpan? maximumLogAge, TimeSpan maximumObservationAge,
        FullBackupRequirement fullRequirement, bool requireChecksums)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        DatabaseFingerprint = ValidateFingerprint(databaseFingerprint);
        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);
        RequireAge(maximumFullAge, nameof(maximumFullAge));
        if (maximumLogAge.HasValue) RequireAge(maximumLogAge.Value, nameof(maximumLogAge));
        if (maximumObservationAge < TimeSpan.FromSeconds(1) || maximumObservationAge > TimeSpan.FromHours(1)
            || maximumObservationAge.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumObservationAge));
        if (!Enum.IsDefined(fullRequirement)) throw new ArgumentOutOfRangeException(nameof(fullRequirement));
        PolicyRevision = policyRevision; Enabled = enabled; MaximumFullAge = maximumFullAge;
        MaximumLogAge = maximumLogAge; MaximumObservationAge = maximumObservationAge;
        FullRequirement = fullRequirement; RequireChecksums = requireChecksums;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string DatabaseFingerprint { get; }
    public long PolicyRevision { get; }
    public bool Enabled { get; }
    public TimeSpan MaximumFullAge { get; }
    public TimeSpan? MaximumLogAge { get; }
    public TimeSpan MaximumObservationAge { get; }
    public FullBackupRequirement FullRequirement { get; }
    public bool RequireChecksums { get; }

    internal static string ValidateFingerprint(string value)
    {
        if (value is not { Length: 64 } || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Backup database identity must be a lowercase opaque fingerprint.", nameof(value));
        return value;
    }

    private static void RequireAge(TimeSpan value, string parameter)
    {
        if (value < TimeSpan.FromSeconds(1) || value > MaximumBackupAge || value.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(parameter);
    }
}

/// <summary>One latest backup for a kind/copy-only slot, without names, devices or source IDs.</summary>
public sealed record BackupFreshnessSample
{
    public BackupFreshnessSample(BackupKind kind, bool copyOnly, DateTimeOffset? finishUtc,
        DateTime? sourceLocalFinish, bool? hasChecksum, bool? isDamaged, bool? metadataComplete)
    {
        if (!Enum.IsDefined(kind) || kind == BackupKind.Differential && copyOnly)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (finishUtc.HasValue) DomainValidation.RequireUtcMicrosecondAligned(finishUtc.Value, nameof(finishUtc));
        if (sourceLocalFinish is { Kind: not DateTimeKind.Unspecified })
            throw new ArgumentException("Source backup finish is an offset-free SQL Server local time.", nameof(sourceLocalFinish));
        Kind = kind; CopyOnly = copyOnly; FinishUtc = finishUtc; SourceLocalFinish = sourceLocalFinish;
        HasChecksum = hasChecksum; IsDamaged = isDamaged; MetadataComplete = metadataComplete;
    }

    public BackupKind Kind { get; }
    public bool CopyOnly { get; }
    public DateTimeOffset? FinishUtc { get; }
    public DateTime? SourceLocalFinish { get; }
    public bool? HasChecksum { get; }
    public bool? IsDamaged { get; }
    public bool? MetadataComplete { get; }
}

/// <summary>Bounded assessment input. All-replica coverage must be proved by its producer.</summary>
public sealed class BackupFreshnessEvidence
{
    public const int MaximumSamples = 5; // full normal/copy-only, differential, log normal/copy-only

    public BackupFreshnessEvidence(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        string databaseFingerprint, CollectorRunId? runId, DateTimeOffset observedAtUtc,
        BackupRecoveryModel recoveryModel, BackupFreshnessScope scope, BackupFreshnessCoverage coverage,
        bool? localReplicaPreferred, IReadOnlyList<BackupFreshnessSample> samples)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        DatabaseFingerprint = BackupFreshnessPolicy.ValidateFingerprint(databaseFingerprint);
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        if (!Enum.IsDefined(recoveryModel) || !Enum.IsDefined(scope) || !Enum.IsDefined(coverage))
            throw new ArgumentException("Backup freshness evidence has an invalid closed state.");
        if (scope is BackupFreshnessScope.Standalone or BackupFreshnessScope.Unknown && localReplicaPreferred.HasValue)
            throw new ArgumentException("Replica preference requires observed AG membership.", nameof(localReplicaPreferred));
        ArgumentNullException.ThrowIfNull(samples);
        BackupFreshnessSample[] owned = samples.ToArray();
        if (owned.Length > MaximumSamples || owned.Any(item => item is null)
            || owned.Select(item => (item.Kind, item.CopyOnly)).Distinct().Count() != owned.Length)
            throw new ArgumentException("Backup freshness samples must have unique bounded kind/copy-only slots.", nameof(samples));
        if ((coverage == BackupFreshnessCoverage.Complete || owned.Length != 0) && runId is null)
            throw new ArgumentException("Published backup evidence requires a run identity.", nameof(runId));
        if (coverage is BackupFreshnessCoverage.NotCollected or BackupFreshnessCoverage.PermissionDenied
            or BackupFreshnessCoverage.Failed or BackupFreshnessCoverage.Unsupported && owned.Length != 0)
            throw new ArgumentException("Unpublished or failed backup reads cannot carry a source prefix.", nameof(samples));
        RunId = runId; RecoveryModel = recoveryModel; Scope = scope; Coverage = coverage;
        LocalReplicaPreferred = localReplicaPreferred; Samples = Array.AsReadOnly(owned);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string DatabaseFingerprint { get; }
    public CollectorRunId? RunId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public BackupRecoveryModel RecoveryModel { get; }
    public BackupFreshnessScope Scope { get; }
    public BackupFreshnessCoverage Coverage { get; }
    public bool? LocalReplicaPreferred { get; }
    public IReadOnlyList<BackupFreshnessSample> Samples { get; }
}

public sealed record BackupFreshnessCheck(BackupFreshnessStatus Status, BackupFreshnessReason Reason,
    TimeSpan? MaximumAge, TimeSpan? ObservedAge, BackupFreshnessSample? SelectedBackup);

public sealed record BackupFreshnessIssue(BackupKind Kind, bool CopyOnly, BackupFreshnessReason Reason);

public sealed record BackupFreshnessAssessment(DateTimeOffset AssessedAtUtc, long PolicyRevision,
    BackupFreshnessCheck Full, BackupFreshnessCheck Log, IReadOnlyList<BackupFreshnessIssue> ObservedIssues);
