using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Opaque, typed read boundary for the staged source; no policy is assigned by the parser.</summary>
public sealed class BackupV2DatabaseRead
{
    internal BackupV2DatabaseRead(BackupV2DatabaseContext context, IReadOnlyList<BackupV2SampleRead> samples,
        CollectorExecutionRequestIdentity run, BackupFreshnessCoverage coverage)
    {
        DatabaseFingerprint = context.IdentityKnown ? context.Database : null;
        FamilyFingerprint = context.Family; RecoveryForkFingerprint = context.Fork;
        GroupFingerprint = context.Group; AgDatabaseFingerprint = context.AgDatabase;
        LocalReplicaFingerprint = context.Replica; RecoveryModel = context.RecoveryModel;
        CatalogDatabaseState = context.DatabaseState; Scope = context.Scope;
        LocalReplicaRole = context.Role; LocalReplicaPreferred = context.Preferred;
        UnclassifiedBackupSeen = context.Unclassified; Coverage = coverage;
        Samples = Array.AsReadOnly(samples.ToArray());
        FreshnessEvidence = DatabaseFingerprint is null ? null : new BackupFreshnessEvidence(
            run.Target, run.Revision, DatabaseFingerprint, run.RunId, context.SourceObservedAtUtc,
            RecoveryModel, Scope, coverage, LocalReplicaPreferred, Samples.Select(item => item.Freshness).ToArray());
    }

    public string? DatabaseFingerprint { get; }
    public string? FamilyFingerprint { get; }
    public string? RecoveryForkFingerprint { get; }
    public string? GroupFingerprint { get; }
    public string? AgDatabaseFingerprint { get; }
    public string? LocalReplicaFingerprint { get; }
    public BackupRecoveryModel RecoveryModel { get; }
    public string? CatalogDatabaseState { get; }
    public BackupFreshnessScope Scope { get; }
    public string? LocalReplicaRole { get; }
    public bool? LocalReplicaPreferred { get; }
    public bool UnclassifiedBackupSeen { get; }
    public BackupFreshnessCoverage Coverage { get; }
    public IReadOnlyList<BackupV2SampleRead> Samples { get; }
    public BackupFreshnessEvidence? FreshnessEvidence { get; }
}

public sealed class BackupV2SampleRead
{
    internal BackupV2SampleRead(BackupFreshnessSample freshness, DateTime? startLocal,
        short? recordedStartOffsetMinutes, long? sizeBytes, string? firstFork,
        string lastFork, BackupFinishTimeResolution resolution)
    {
        Freshness = freshness; SourceLocalStart = startLocal;
        RecordedStartOffsetMinutes = recordedStartOffsetMinutes; SizeBytes = sizeBytes;
        FirstRecoveryForkFingerprint = firstFork; LastRecoveryForkFingerprint = lastFork;
        TimeResolution = resolution;
    }

    public BackupFreshnessSample Freshness { get; }
    public DateTime? SourceLocalStart { get; }
    public short? RecordedStartOffsetMinutes { get; }
    public long? SizeBytes { get; }
    public string? FirstRecoveryForkFingerprint { get; }
    public string LastRecoveryForkFingerprint { get; }
    public BackupFinishTimeResolution TimeResolution { get; }
}

public sealed class BackupV2ReadSnapshot
{
    internal const int EnvelopeEstimatedBytes = 2048;
    internal const int DatabaseEstimatedBytes = 2048;
    internal const int SampleEstimatedBytes = 1536;
    internal BackupV2ReadSnapshot(CollectorExecutionRequestIdentity run, DateTimeOffset receivedAtUtc,
        DateTimeOffset? sourceObservedAtUtc, string? sourceZoneId, DateTime? historyLowerBoundLocal,
        IReadOnlyList<BackupV2DatabaseRead> databases, int rows, BackupFreshnessCoverage coverage)
    {
        TargetId = run.Target; TargetRevision = run.Revision; RunId = run.RunId;
        ReceivedAtUtc = receivedAtUtc; SourceObservedAtUtc = sourceObservedAtUtc;
        SourceZoneId = sourceZoneId; HistoryLowerBoundLocal = historyLowerBoundLocal;
        Databases = Array.AsReadOnly(databases.ToArray()); SourceRowsRead = rows; Coverage = coverage;
        // Reserve space for expanded keyed identities, escaped zone text and the derived assessment inputs.
        EstimatedSizeBytes = EnvelopeEstimatedBytes + Databases.Sum(database =>
            DatabaseEstimatedBytes + database.Samples.Count * SampleEstimatedBytes);
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public DateTimeOffset? SourceObservedAtUtc { get; }
    public string? SourceZoneId { get; }
    public DateTime? HistoryLowerBoundLocal { get; }
    public IReadOnlyList<BackupV2DatabaseRead> Databases { get; }
    public int SourceRowsRead { get; }
    public BackupFreshnessCoverage Coverage { get; }
    public int EstimatedSizeBytes { get; }
}

public sealed record BackupV2ParseResult(BackupV2ReadSnapshot Snapshot, int ResponseBytes, CollectorLossEvidence Loss);

internal sealed record CollectorExecutionRequestIdentity(MonitoredInstanceId Target, ObservationTargetRevision Revision, CollectorRunId RunId);

internal sealed record BackupV2DatabaseContext(string? Database, string? Family, string? Fork, string? Group,
    string? AgDatabase, string? Replica, bool IdentityKnown, BackupRecoveryModel RecoveryModel,
    string? DatabaseState, BackupFreshnessScope Scope, string? Role, bool? Preferred,
    DateTimeOffset SourceObservedAtUtc, string? SourceZoneId, DateTime HistoryLowerBoundLocal, bool Unclassified)
{
    public bool Complete => IdentityKnown && Database is not null && Family is not null && Fork is not null
        && RecoveryModel != BackupRecoveryModel.Unknown && DatabaseState == "ONLINE" && !Unclassified
        && (Scope == BackupFreshnessScope.Standalone || Scope == BackupFreshnessScope.AvailabilityGroupLocalOnly
            && Group is not null && AgDatabase is not null && Replica is not null && Role is "PRIMARY" or "SECONDARY"
            && Preferred.HasValue);
}
