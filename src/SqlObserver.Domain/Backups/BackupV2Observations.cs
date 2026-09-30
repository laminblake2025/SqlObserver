using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Backups;

/// <summary>Opaque, typed read boundary for the staged source; no policy is assigned by the parser.</summary>
public sealed class BackupV2DatabaseRead
{
    private readonly BackupV2DatabaseContext context;
    private readonly BackupV2RunIdentity run;
    public BackupV2DatabaseRead(BackupV2DatabaseContext context, IReadOnlyList<BackupV2SampleRead> samples,
        BackupV2RunIdentity run, BackupFreshnessCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(run);
        context.Validate();
        if (run.Target is null || run.Revision is null || run.RunId is null ||
            coverage is not (BackupFreshnessCoverage.Complete or BackupFreshnessCoverage.Partial) ||
            (coverage == BackupFreshnessCoverage.Complete) != context.Complete)
            throw new ArgumentException("Backup database identity or coverage is invalid.");
        BackupV2SampleRead[] owned = samples.ToArray();
        if (owned.Length > BackupFreshnessEvidence.MaximumSamples || owned.Any(item => item is null) ||
            owned.Select(item => (item.Freshness.Kind, item.Freshness.CopyOnly)).Distinct().Count() != owned.Length ||
            owned.Length > 0 && (!context.IdentityKnown || context.Family is null || context.Fork is null) ||
            owned.Any(item => item.LastRecoveryForkFingerprint != context.Fork ||
                item.Freshness.SourceLocalFinish < context.HistoryLowerBoundLocal ||
                !item.Freshness.SourceLocalFinish.HasValue && item.SourceLocalStart < context.HistoryLowerBoundLocal))
            throw new ArgumentException("Backup database samples or lineage are invalid.");
        this.context = context; this.run = run;
        DatabaseFingerprint = context.IdentityKnown ? context.Database : null;
        FamilyFingerprint = context.Family; RecoveryForkFingerprint = context.Fork;
        GroupFingerprint = context.Group; AgDatabaseFingerprint = context.AgDatabase;
        LocalReplicaFingerprint = context.Replica; RecoveryModel = context.RecoveryModel;
        CatalogDatabaseState = context.DatabaseState; Scope = context.Scope;
        LocalReplicaRole = context.Role; LocalReplicaPreferred = context.Preferred;
        UnclassifiedBackupSeen = context.Unclassified; Coverage = coverage;
        Samples = Array.AsReadOnly(owned);
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
    internal bool MatchesSource(DateTimeOffset? observedAt, string? zoneId, DateTime? lowerBound) =>
        context.SourceObservedAtUtc == observedAt && context.SourceZoneId == zoneId && context.HistoryLowerBoundLocal == lowerBound;
    internal bool MatchesRun(BackupV2RunIdentity identity) => run == identity;
}

public sealed class BackupV2SampleRead
{
    public BackupV2SampleRead(BackupFreshnessSample freshness, DateTime? startLocal,
        short? recordedStartOffsetMinutes, long? sizeBytes, string? firstFork,
        string lastFork, BackupFinishTimeResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(freshness); ArgumentNullException.ThrowIfNull(resolution);
        BackupFreshnessPolicy.ValidateFingerprint(lastFork);
        if (firstFork is not null) BackupFreshnessPolicy.ValidateFingerprint(firstFork);
        if (startLocal is { } start && (start.Kind != DateTimeKind.Unspecified || start.Year < 1753 ||
                start.Ticks % TimeSpan.TicksPerMicrosecond != 0) || sizeBytes < 0 ||
            freshness.SourceLocalFinish is { } finish && (finish.Year < 1753 || finish.Ticks % TimeSpan.TicksPerMicrosecond != 0) ||
            recordedStartOffsetMinutes is { } offset && (offset is < -720 or > 720 || offset % 15 != 0) ||
            freshness.MetadataComplete == true && firstFork is null || !Enum.IsDefined(resolution.State) ||
            resolution.QualificationRevision is <= 0 || resolution.FinishUtc != freshness.FinishUtc ||
            resolution.State == BackupFinishTimeState.QualificationMissing && resolution.QualificationRevision.HasValue ||
            (resolution.State == BackupFinishTimeState.Resolved) != resolution.FinishUtc.HasValue ||
            resolution.State == BackupFinishTimeState.Resolved && (!resolution.QualificationRevision.HasValue ||
                !startLocal.HasValue || !freshness.SourceLocalFinish.HasValue || !recordedStartOffsetMinutes.HasValue))
            throw new ArgumentException("Backup sample metadata or time provenance is invalid.");
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

public sealed class BackupV2ReadSnapshot : IOperationalHealthSnapshot
{
    public const int MaximumRows = 3072;
    public const int MaximumDatabases = 512;
    public const int EnvelopeEstimatedBytes = 2048;
    public const int DatabaseEstimatedBytes = 2048;
    public const int SampleEstimatedBytes = 1536;
    public BackupV2ReadSnapshot(BackupV2RunIdentity run, DateTimeOffset receivedAtUtc,
        DateTimeOffset? sourceObservedAtUtc, string? sourceZoneId, DateTime? historyLowerBoundLocal,
        IReadOnlyList<BackupV2DatabaseRead> databases, int rows, BackupFreshnessCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(run.Target); ArgumentNullException.ThrowIfNull(run.Revision); ArgumentNullException.ThrowIfNull(run.RunId);
        DomainValidation.RequireUtcMicrosecondAligned(receivedAtUtc, nameof(receivedAtUtc));
        if (sourceObservedAtUtc.HasValue) DomainValidation.RequireUtcMicrosecondAligned(sourceObservedAtUtc.Value, nameof(sourceObservedAtUtc));
        if (sourceZoneId is not null && (sourceZoneId.Length is < 1 or > 128 || sourceZoneId.Any(char.IsControl)) ||
            historyLowerBoundLocal is { } bound && (bound.Kind != DateTimeKind.Unspecified || bound.Year < 1753 ||
                bound.Ticks % TimeSpan.TicksPerMicrosecond != 0))
            throw new ArgumentException("Backup source context is invalid.");
        BackupV2DatabaseRead[] owned = databases.ToArray();
        if (owned.Length > MaximumDatabases || owned.Any(item => item is null) || rows is < 0 or > MaximumRows + 1 ||
            coverage is not (BackupFreshnessCoverage.Complete or BackupFreshnessCoverage.Partial or BackupFreshnessCoverage.Truncated))
            throw new ArgumentException("Backup snapshot bounds or coverage are invalid.");
        int items = owned.Sum(item => 1 + item.Samples.Count);
        if (items > MaximumRows || items > rows ||
            coverage == BackupFreshnessCoverage.Truncated && (rows == 0 || items != 0) ||
            coverage != BackupFreshnessCoverage.Truncated && rows != items ||
            coverage == BackupFreshnessCoverage.Complete && (items == 0 || owned.Any(item => item.Coverage != BackupFreshnessCoverage.Complete)) ||
            coverage == BackupFreshnessCoverage.Partial && items > 0 && owned.All(item => item.Coverage == BackupFreshnessCoverage.Complete) ||
            (rows > 0) != (sourceObservedAtUtc.HasValue && historyLowerBoundLocal.HasValue) ||
            rows == 0 && (sourceObservedAtUtc.HasValue || historyLowerBoundLocal.HasValue || sourceZoneId is not null) ||
            owned.Where(item => item.DatabaseFingerprint is not null).Select(item => item.DatabaseFingerprint).Distinct().Count() !=
                owned.Count(item => item.DatabaseFingerprint is not null) ||
            owned.Any(item => !item.MatchesRun(run) || !item.MatchesSource(sourceObservedAtUtc, sourceZoneId, historyLowerBoundLocal) ||
                item.FreshnessEvidence is { } evidence && (evidence.TargetId != run.Target || evidence.TargetRevision != run.Revision || evidence.RunId != run.RunId)))
            throw new ArgumentException("Backup scan accounting, identities or source context are inconsistent.");
        TargetId = run.Target; TargetRevision = run.Revision; RunId = run.RunId;
        ReceivedAtUtc = receivedAtUtc; SourceObservedAtUtc = sourceObservedAtUtc;
        SourceZoneId = sourceZoneId; HistoryLowerBoundLocal = historyLowerBoundLocal;
        Databases = Array.AsReadOnly(owned); SourceRowsRead = rows; Coverage = coverage;
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
    public int ItemCount => Databases.Sum(database => 1 + database.Samples.Count);
}

public sealed record BackupV2RunIdentity(MonitoredInstanceId Target, ObservationTargetRevision Revision, CollectorRunId RunId);

public sealed record BackupV2DatabaseContext(string? Database, string? Family, string? Fork, string? Group,
    string? AgDatabase, string? Replica, bool IdentityKnown, BackupRecoveryModel RecoveryModel,
    string? DatabaseState, BackupFreshnessScope Scope, string? Role, bool? Preferred,
    DateTimeOffset SourceObservedAtUtc, string? SourceZoneId, DateTime HistoryLowerBoundLocal, bool Unclassified)
{
    internal void Validate()
    {
        foreach (string? fingerprint in new[] { Database, Family, Fork, Group, AgDatabase, Replica })
            if (fingerprint is not null) BackupFreshnessPolicy.ValidateFingerprint(fingerprint);
        DomainValidation.RequireUtcMicrosecondAligned(SourceObservedAtUtc, nameof(SourceObservedAtUtc));
        bool hasAg = Group is not null || AgDatabase is not null || Replica is not null;
        if (!Enum.IsDefined(RecoveryModel) || Scope is not (BackupFreshnessScope.Unknown or BackupFreshnessScope.Standalone or BackupFreshnessScope.AvailabilityGroupLocalOnly) ||
            DatabaseState is not (null or "ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING" or "SUSPECT" or "EMERGENCY" or "OFFLINE") ||
            Role is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING") || IdentityKnown && Database is null ||
            Scope != BackupFreshnessScope.AvailabilityGroupLocalOnly && (hasAg || Role is not null || Preferred.HasValue) ||
            Scope == BackupFreshnessScope.AvailabilityGroupLocalOnly && !hasAg || Preferred.HasValue && (Group is null || AgDatabase is null || Replica is null) ||
            SourceZoneId is not null && (SourceZoneId.Length is < 1 or > 128 || SourceZoneId.Any(char.IsControl)) ||
            HistoryLowerBoundLocal.Kind != DateTimeKind.Unspecified || HistoryLowerBoundLocal.Year < 1753 ||
            HistoryLowerBoundLocal.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ArgumentException("Backup database source context is invalid.");
    }
    public bool Complete => IdentityKnown && Database is not null && Family is not null && Fork is not null
        && RecoveryModel != BackupRecoveryModel.Unknown && DatabaseState == "ONLINE" && !Unclassified
        && (Scope == BackupFreshnessScope.Standalone || Scope == BackupFreshnessScope.AvailabilityGroupLocalOnly
            && Group is not null && AgDatabase is not null && Replica is not null && Role is "PRIMARY" or "SECONDARY"
            && Preferred.HasValue);
}
