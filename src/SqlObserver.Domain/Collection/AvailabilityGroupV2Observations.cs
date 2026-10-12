using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Location-aware identity shared by replica and database-replica evidence.</summary>
public sealed record AvailabilityReplicaEvidenceIdentity
{
    public AvailabilityReplicaEvidenceIdentity(string groupFingerprint, string replicaFingerprint,
        string? observerReplicaFingerprint, bool? replicaIsLocal, AvailabilityVisibilityScope visibilityScope)
    {
        if (!ValidFingerprint(groupFingerprint) || !ValidFingerprint(replicaFingerprint) ||
            !Enum.IsDefined(visibilityScope) ||
            observerReplicaFingerprint is null &&
                (replicaIsLocal is not null || visibilityScope != AvailabilityVisibilityScope.ResolvingLocalOnly) ||
            observerReplicaFingerprint is not null &&
                (!ValidFingerprint(observerReplicaFingerprint) || replicaIsLocal is null ||
                 replicaIsLocal != (observerReplicaFingerprint == replicaFingerprint)))
            throw new ArgumentException("AG observing-replica identity is invalid.");
        GroupFingerprint = groupFingerprint;
        ReplicaFingerprint = replicaFingerprint;
        ObserverReplicaFingerprint = observerReplicaFingerprint;
        ReplicaIsLocal = replicaIsLocal;
        VisibilityScope = visibilityScope;
    }

    public string GroupFingerprint { get; }
    public string ReplicaFingerprint { get; }
    public string? ObserverReplicaFingerprint { get; }
    public bool? ReplicaIsLocal { get; }
    public AvailabilityVisibilityScope VisibilityScope { get; }

    internal static bool ValidFingerprint(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record AvailabilityReplicaEvidence(AvailabilityReplicaEvidenceIdentity Identity,
    bool StateObserved, string? Role, string? OperationalState, string? ConnectedState)
{
    public int EstimatedSizeBytes => 512 + Identity.GroupFingerprint.Length +
        Identity.ReplicaFingerprint.Length + (Identity.ObserverReplicaFingerprint?.Length ?? 0) +
        (Role?.Length ?? 0) + (OperationalState?.Length ?? 0) + (ConnectedState?.Length ?? 0);
}

public sealed record AvailabilityDatabaseReplicaEvidence(AvailabilityReplicaEvidenceIdentity Identity,
    string DatabaseFingerprint, bool StateObserved, string? SynchronizationState,
    string? SynchronizationHealth, string? DatabaseState, bool? IsSuspended,
    long? LogSendQueueKb, long? LogSendRateKbPerSecond, long? RedoQueueKb,
    long? RedoRateKbPerSecond, DateTime? SourceLastCommitLocal)
{
    public int EstimatedSizeBytes => 768 + Identity.GroupFingerprint.Length +
        Identity.ReplicaFingerprint.Length + DatabaseFingerprint.Length +
        (Identity.ObserverReplicaFingerprint?.Length ?? 0) +
        (SynchronizationState?.Length ?? 0) + (SynchronizationHealth?.Length ?? 0) +
        (DatabaseState?.Length ?? 0);
}

/// <summary>Immutable AG v2 scan. Complete means observed coverage, never healthy state.</summary>
public sealed class AvailabilityGroupsV2Snapshot : IOperationalHealthSnapshot
{
    public AvailabilityGroupsV2Snapshot(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunId runId, DateTimeOffset observedAtUtc,
        IReadOnlyList<AvailabilityReplicaEvidence> replicas,
        IReadOnlyList<AvailabilityDatabaseReplicaEvidence> databases, int sourceRowsRead, bool truncated)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc, nameof(observedAtUtc));
        ArgumentNullException.ThrowIfNull(replicas);
        ArgumentNullException.ThrowIfNull(databases);
        if (sourceRowsRead is < 0 or > OperationalHealthBounds.AvailabilityMaximumRows + 1 ||
            replicas.Count + databases.Count > OperationalHealthBounds.AvailabilityMaximumRows ||
            replicas.Count + databases.Count > sourceRowsRead ||
            !truncated && replicas.Count + databases.Count != sourceRowsRead)
            throw new ArgumentException("AG v2 scan accounting is invalid.");

        var replicaKeys = new Dictionary<(string Group, string Replica), AvailabilityReplicaEvidenceIdentity>();
        var groupLocations = new Dictionary<string, (string? Observer, AvailabilityVisibilityScope Scope)>();
        foreach (AvailabilityReplicaEvidence replica in replicas)
        {
            if (replica is null || replica.Identity is null ||
                !replicaKeys.TryAdd((replica.Identity.GroupFingerprint, replica.Identity.ReplicaFingerprint), replica.Identity) ||
                replica.Role is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING") ||
                replica.OperationalState is not (null or "PENDING_FAILOVER" or "PENDING" or "ONLINE" or "OFFLINE" or "FAILED" or "FAILED_NO_QUORUM") ||
                replica.ConnectedState is not (null or "CONNECTED" or "DISCONNECTED") ||
                !replica.StateObserved && (replica.Role is not null || replica.OperationalState is not null || replica.ConnectedState is not null))
                throw new ArgumentException("AG replica evidence is invalid.");
            var location = (replica.Identity.ObserverReplicaFingerprint, replica.Identity.VisibilityScope);
            if (groupLocations.TryGetValue(replica.Identity.GroupFingerprint, out var previous) && previous != location)
                throw new ArgumentException("AG group observation locations are inconsistent.");
            groupLocations[replica.Identity.GroupFingerprint] = location;
        }

        var databaseKeys = new HashSet<(string Group, string Replica, string Database)>();
        foreach (AvailabilityDatabaseReplicaEvidence database in databases)
        {
            if (database is null || database.Identity is null ||
                !AvailabilityReplicaEvidenceIdentity.ValidFingerprint(database.DatabaseFingerprint) ||
                !databaseKeys.Add((database.Identity.GroupFingerprint, database.Identity.ReplicaFingerprint, database.DatabaseFingerprint)) ||
                !replicaKeys.TryGetValue((database.Identity.GroupFingerprint, database.Identity.ReplicaFingerprint), out var replicaIdentity) ||
                replicaIdentity != database.Identity ||
                database.SynchronizationState is not (null or "NOT SYNCHRONIZING" or "SYNCHRONIZING" or "SYNCHRONIZED" or "REVERTING" or "INITIALIZING") ||
                database.SynchronizationHealth is not (null or "NOT_HEALTHY" or "PARTIALLY_HEALTHY" or "HEALTHY") ||
                database.DatabaseState is not (null or "ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING" or "SUSPECT" or "EMERGENCY" or "OFFLINE") ||
                database.LogSendQueueKb < 0 || database.LogSendRateKbPerSecond < 0 ||
                database.RedoQueueKb < 0 || database.RedoRateKbPerSecond < 0 ||
                database.SourceLastCommitLocal is { } local &&
                    (local.Kind != DateTimeKind.Unspecified || local.Year < 1753 ||
                     local.Ticks % TimeSpan.TicksPerMicrosecond != 0) ||
                !database.StateObserved &&
                    (database.SynchronizationState is not null || database.SynchronizationHealth is not null ||
                     database.DatabaseState is not null || database.IsSuspended is not null ||
                     database.LogSendQueueKb is not null || database.LogSendRateKbPerSecond is not null ||
                     database.RedoQueueKb is not null || database.RedoRateKbPerSecond is not null ||
                     database.SourceLastCommitLocal is not null))
                throw new ArgumentException("AG database-replica evidence is invalid.");
        }
        if (!truncated && groupLocations.Any(group => group.Value.Observer is { } observer &&
                !replicaKeys.ContainsKey((group.Key, observer))))
            throw new ArgumentException("AG observing replica is absent from the configured replica stream.");

        Replicas = Array.AsReadOnly(replicas.OrderBy(static item => item.Identity.GroupFingerprint, StringComparer.Ordinal)
            .ThenBy(static item => item.Identity.ReplicaFingerprint, StringComparer.Ordinal).ToArray());
        Databases = Array.AsReadOnly(databases.OrderBy(static item => item.Identity.GroupFingerprint, StringComparer.Ordinal)
            .ThenBy(static item => item.Identity.ReplicaFingerprint, StringComparer.Ordinal)
            .ThenBy(static item => item.DatabaseFingerprint, StringComparer.Ordinal).ToArray());
        SourceRowsRead = sourceRowsRead;
        Truncated = truncated;
        State = truncated ? OperationalObservationState.Partial : sourceRowsRead == 0 ? OperationalObservationState.NoData :
            replicas.All(static item => item.Identity.VisibilityScope == AvailabilityVisibilityScope.PrimaryAllKnown &&
                item.StateObserved && item.ConnectedState is not null &&
                (item.Identity.ReplicaIsLocal == false || item.Role == "PRIMARY" && item.OperationalState is not null)) &&
            databases.All(static item => item.Identity.VisibilityScope == AvailabilityVisibilityScope.PrimaryAllKnown &&
                item.StateObserved && item.SynchronizationState is not null && item.SynchronizationHealth is not null &&
                (item.Identity.ReplicaIsLocal == false || item.DatabaseState is not null))
                ? OperationalObservationState.Complete : OperationalObservationState.Degraded;
        EstimatedSizeBytes = checked(256 + Replicas.Sum(static item => item.EstimatedSizeBytes) +
            Databases.Sum(static item => item.EstimatedSizeBytes));
        if (EstimatedSizeBytes > 2_097_152)
            throw new ArgumentException("AG v2 scan exceeds its output byte bound.");
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public IReadOnlyList<AvailabilityReplicaEvidence> Replicas { get; }
    public IReadOnlyList<AvailabilityDatabaseReplicaEvidence> Databases { get; }
    public int SourceRowsRead { get; }
    public bool Truncated { get; }
    public OperationalObservationState State { get; }
    public int EstimatedSizeBytes { get; }
}
