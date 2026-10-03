using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum ServerLifecycleCoverage { Complete = 1, Partial = 2, Unknown = 3, Unsupported = 4 }
public enum ServerLifecycleEventKind { RestartDetected = 1, RespondingNodeChanged = 2, FailoverDetected = 3 }

/// <summary>SQL-reported responding identities, not hardware UUIDs or configured endpoint identities.</summary>
public static class ServerLifecycleIdentityFingerprint
{
    public static string PhysicalNode(string value, IdentityFingerprintKey key) => Name("lifecycle-physical-node", value, key);
    public static string LogicalInstance(string value, IdentityFingerprintKey key) => Name("lifecycle-logical-instance", value, key);
    public static string AvailabilityGroup(Guid value, IdentityFingerprintKey key) => Identifier("lifecycle-ag-group", value, key);
    public static string AvailabilityReplica(Guid value, IdentityFingerprintKey key) => Identifier("lifecycle-ag-replica", value, key);

    private static string Name(string domain, string value, IdentityFingerprintKey key)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Trim() != value || value.Any(char.IsControl))
            throw new ArgumentException("SQL-reported lifecycle identity is invalid.", nameof(value));
        return ReplicationIdentityFingerprint.FromTypedIdentity(domain, Encoding.UTF8.GetBytes(value.ToUpperInvariant()), key);
    }

    private static string Identifier(string domain, Guid value, IdentityFingerprintKey key)
    {
        if (value == Guid.Empty) throw new ArgumentException("Lifecycle identifier is empty.", nameof(value));
        return ReplicationIdentityFingerprint.FromTypedIdentity(domain, value.ToByteArray(bigEndian: true), key);
    }
}

/// <summary>Startup remains an offset-free source value. No physical occurrence UTC is inferred from uptime.</summary>
public sealed record ServerLifecycleEngineEvidence
{
    public ServerLifecycleEngineEvidence(string? physicalNodeFingerprint, string? logicalInstanceFingerprint,
        int? processId, DateTime? sourceStartLocal, long? startMsTicks, long? currentMsTicks)
    {
        if (physicalNodeFingerprint is not null && !AvailabilityReplicaEvidenceIdentity.ValidFingerprint(physicalNodeFingerprint) ||
            logicalInstanceFingerprint is not null && !AvailabilityReplicaEvidenceIdentity.ValidFingerprint(logicalInstanceFingerprint) ||
            processId is <= 0 || startMsTicks < 0 || currentMsTicks < 0 ||
            sourceStartLocal is { } local && (local.Kind != DateTimeKind.Unspecified || local.Year < 1753))
            throw new ArgumentException("Lifecycle engine evidence is invalid.");
        PhysicalNodeFingerprint = physicalNodeFingerprint;
        LogicalInstanceFingerprint = logicalInstanceFingerprint;
        ProcessId = processId;
        SourceStartLocal = sourceStartLocal;
        StartMsTicks = startMsTicks;
        CurrentMsTicks = currentMsTicks;
        Coverage = physicalNodeFingerprint is null || logicalInstanceFingerprint is null || processId is null ||
            sourceStartLocal is null || startMsTicks is null || currentMsTicks is null
            ? ServerLifecycleCoverage.Unknown : startMsTicks > currentMsTicks
                ? ServerLifecycleCoverage.Partial : ServerLifecycleCoverage.Complete;
        if (physicalNodeFingerprint is not null && logicalInstanceFingerprint is not null &&
            processId is not null && sourceStartLocal is { } start && startMsTicks is not null)
        {
            string input = FormattableString.Invariant($"sqlobserver-lifecycle-startup-v1\0{physicalNodeFingerprint}|{logicalInstanceFingerprint}|{processId}|{start.Ticks}|{startMsTicks}");
            StartupIncarnationFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        }
    }

    public string? PhysicalNodeFingerprint { get; }
    public string? LogicalInstanceFingerprint { get; }
    public int? ProcessId { get; }
    public DateTime? SourceStartLocal { get; }
    public long? StartMsTicks { get; }
    public long? CurrentMsTicks { get; }
    public string? StartupIncarnationFingerprint { get; }
    public ServerLifecycleCoverage Coverage { get; }
    public bool HardwareIdentityVerified { get; }
    public DateTimeOffset? OccurredAtUtc { get; }
}

public sealed record ServerLifecycleAgMemberEvidence
{
    public ServerLifecycleAgMemberEvidence(string groupFingerprint, string replicaFingerprint,
        string? observerReplicaFingerprint, string? role, string? observerRole, bool stateObserved,
        int? expectedGroupMembers)
    {
        if (!AvailabilityReplicaEvidenceIdentity.ValidFingerprint(groupFingerprint) ||
            !AvailabilityReplicaEvidenceIdentity.ValidFingerprint(replicaFingerprint) ||
            observerReplicaFingerprint is not null && !AvailabilityReplicaEvidenceIdentity.ValidFingerprint(observerReplicaFingerprint) ||
            role is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING") ||
            observerRole is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING") ||
            !stateObserved && role is not null || expectedGroupMembers is <= 0)
            throw new ArgumentException("Lifecycle AG membership evidence is invalid.");
        GroupFingerprint = groupFingerprint;
        ReplicaFingerprint = replicaFingerprint;
        ObserverReplicaFingerprint = observerReplicaFingerprint;
        Role = role;
        ObserverRole = observerRole;
        StateObserved = stateObserved;
        ExpectedGroupMembers = expectedGroupMembers;
    }

    public string GroupFingerprint { get; }
    public string ReplicaFingerprint { get; }
    public string? ObserverReplicaFingerprint { get; }
    public string? Role { get; }
    public string? ObserverRole { get; }
    public bool StateObserved { get; }
    public int? ExpectedGroupMembers { get; }
}

/// <summary>Bounded immutable source scan. Component completeness denotes coverage, never health.</summary>
public sealed class ServerLifecycleSnapshot : IOperationalHealthSnapshot
{
    public const int CurrentSourceSchemaVersion = 1;
    public const int IdentityVersion = 1;
    public const int MaximumMembers = 2048;
    public const int MaximumRows = MaximumMembers + 1;
    public const int MaximumBytes = 2_097_152;

    public ServerLifecycleSnapshot(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, DateTimeOffset sourceObservedAtUtc, ServerLifecycleEngineEvidence engine,
        bool? isHadrEnabled, IReadOnlyList<ServerLifecycleAgMemberEvidence> members, int sourceRowsRead,
        bool truncated, int sourceSchemaVersion = CurrentSourceSchemaVersion)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        SourceObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(sourceObservedAtUtc, nameof(sourceObservedAtUtc));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(members);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSchemaVersion);
        if (members.Count > MaximumMembers || sourceRowsRead is < 1 or > MaximumRows + 1 ||
            members.Count + 1 > sourceRowsRead || !truncated && members.Count + 1 != sourceRowsRead ||
            isHadrEnabled == false && members.Count != 0)
            throw new ArgumentException("Lifecycle source accounting is invalid.");
        var keys = new HashSet<(string Group, string Replica)>();
        foreach (ServerLifecycleAgMemberEvidence member in members)
            if (member is null || !keys.Add((member.GroupFingerprint, member.ReplicaFingerprint)))
                throw new ArgumentException("Lifecycle source membership is duplicated or missing.", nameof(members));
        Members = Array.AsReadOnly(members.OrderBy(static row => row.GroupFingerprint, StringComparer.Ordinal)
            .ThenBy(static row => row.ReplicaFingerprint, StringComparer.Ordinal).ToArray());
        IsHadrEnabled = isHadrEnabled;
        SourceRowsRead = sourceRowsRead;
        Truncated = truncated;
        SourceSchemaVersion = sourceSchemaVersion;
        EngineCoverage = engine.Coverage;
        AvailabilityCoverage = isHadrEnabled == false ? ServerLifecycleCoverage.Unsupported :
            isHadrEnabled is null ? ServerLifecycleCoverage.Unknown : truncated ? ServerLifecycleCoverage.Partial :
            Members.Count == 0 ? ServerLifecycleCoverage.Unknown :
            Members.GroupBy(static row => row.GroupFingerprint).All(IsCompleteGroup)
                ? ServerLifecycleCoverage.Complete : ServerLifecycleCoverage.Partial;
        EstimatedSizeBytes = checked(512 + Members.Count * 512);
        if (EstimatedSizeBytes > MaximumBytes) throw new ArgumentException("Lifecycle evidence exceeds its byte bound.");
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public DateTimeOffset SourceObservedAtUtc { get; }
    public ServerLifecycleEngineEvidence Engine { get; }
    public bool? IsHadrEnabled { get; }
    public IReadOnlyList<ServerLifecycleAgMemberEvidence> Members { get; }
    public int SourceRowsRead { get; }
    public bool Truncated { get; }
    public int SourceSchemaVersion { get; }
    public ServerLifecycleCoverage EngineCoverage { get; }
    public ServerLifecycleCoverage AvailabilityCoverage { get; }
    public int EstimatedSizeBytes { get; }

    internal static bool IsCompleteGroup(IEnumerable<ServerLifecycleAgMemberEvidence> source)
    {
        ServerLifecycleAgMemberEvidence[] rows = source.ToArray();
        ServerLifecycleAgMemberEvidence first = rows[0];
        return first.ObserverReplicaFingerprint is { } observer && first.ExpectedGroupMembers == rows.Length &&
            first.ObserverRole == "PRIMARY" && rows.All(row => row.ObserverReplicaFingerprint == observer &&
                row.ObserverRole == first.ObserverRole && row.ExpectedGroupMembers == first.ExpectedGroupMembers &&
                row.StateObserved && row.Role is "PRIMARY" or "SECONDARY") &&
            rows.Count(static row => row.Role == "PRIMARY") == 1 &&
            rows.Any(row => row.ReplicaFingerprint == observer && row.Role == "PRIMARY");
    }
}

/// <summary>Trusted commit proof supplied by the sealed writer, not a source-row or UI assertion.
/// PreviousRunId must include every attempted source read, including failed, lost and retired reads.</summary>
public sealed record ServerLifecycleReceipt
{
    public ServerLifecycleReceipt(ServerLifecycleSnapshot snapshot, DateTimeOffset completedAtUtc,
        CollectorRunId? previousRunId, bool succeededWithoutLoss, bool sourceConsistencyVerified = false)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        CompletedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(completedAtUtc, nameof(completedAtUtc));
        if (previousRunId == snapshot.RunId) throw new ArgumentException("Lifecycle receipt cannot precede itself.", nameof(previousRunId));
        PreviousRunId = previousRunId;
        SucceededWithoutLoss = succeededWithoutLoss;
        SourceConsistencyVerified = sourceConsistencyVerified;
    }

    public ServerLifecycleSnapshot Snapshot { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public CollectorRunId? PreviousRunId { get; }
    public bool SucceededWithoutLoss { get; }
    public bool SourceConsistencyVerified { get; }
}

/// <summary>A sampled detection interval. Occurrence time, cause and number of intervening transitions are unknown.</summary>
public sealed record ServerLifecycleDetectedEvent(Guid EventId, ServerLifecycleEventKind Kind,
    string AffectedIdentityFingerprint, CollectorRunId BeforeRunId, CollectorRunId AfterRunId,
    DateTimeOffset BeforeObservedAtUtc, DateTimeOffset AfterObservedAtUtc, DateTimeOffset DetectedAtUtc)
{
    public DateTimeOffset? OccurredAtUtc { get; }
}

public static class ServerLifecycleClassifier
{
    public static IReadOnlyList<ServerLifecycleDetectedEvent> Compare(ServerLifecycleReceipt? before, ServerLifecycleReceipt after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null || !Comparable(before, after)) return Array.Empty<ServerLifecycleDetectedEvent>();
        var events = new List<ServerLifecycleDetectedEvent>();
        ServerLifecycleEngineEvidence oldEngine = before.Snapshot.Engine;
        ServerLifecycleEngineEvidence newEngine = after.Snapshot.Engine;
        if (before.Snapshot.EngineCoverage == ServerLifecycleCoverage.Complete &&
            after.Snapshot.EngineCoverage == ServerLifecycleCoverage.Complete &&
            oldEngine.LogicalInstanceFingerprint == newEngine.LogicalInstanceFingerprint)
        {
            if (oldEngine.PhysicalNodeFingerprint != newEngine.PhysicalNodeFingerprint)
                events.Add(Create(ServerLifecycleEventKind.RespondingNodeChanged, newEngine.LogicalInstanceFingerprint!, before, after));
            else if (oldEngine.StartupIncarnationFingerprint != newEngine.StartupIncarnationFingerprint &&
                (oldEngine.ProcessId != newEngine.ProcessId || oldEngine.StartMsTicks != newEngine.StartMsTicks) &&
                oldEngine.SourceStartLocal != newEngine.SourceStartLocal)
                events.Add(Create(ServerLifecycleEventKind.RestartDetected, newEngine.PhysicalNodeFingerprint!, before, after));
        }
        if (before.SourceConsistencyVerified && after.SourceConsistencyVerified &&
            before.Snapshot.AvailabilityCoverage == ServerLifecycleCoverage.Complete &&
            after.Snapshot.AvailabilityCoverage == ServerLifecycleCoverage.Complete)
        {
            var oldGroups = before.Snapshot.Members.GroupBy(static row => row.GroupFingerprint)
                .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
            foreach (var group in after.Snapshot.Members.GroupBy(static row => row.GroupFingerprint))
            {
                ServerLifecycleAgMemberEvidence[] newRows = group.ToArray();
                if (!oldGroups.TryGetValue(group.Key, out ServerLifecycleAgMemberEvidence[]? oldRows) ||
                    !oldRows.Select(static row => row.ReplicaFingerprint).SequenceEqual(newRows.Select(static row => row.ReplicaFingerprint))) continue;
                if (oldRows.Single(static row => row.Role == "PRIMARY").ReplicaFingerprint !=
                    newRows.Single(static row => row.Role == "PRIMARY").ReplicaFingerprint)
                    events.Add(Create(ServerLifecycleEventKind.FailoverDetected, group.Key, before, after));
            }
        }
        return events.AsReadOnly();
    }

    private static bool Comparable(ServerLifecycleReceipt before, ServerLifecycleReceipt after) =>
        before.SucceededWithoutLoss && after.SucceededWithoutLoss && !before.Snapshot.Truncated && !after.Snapshot.Truncated &&
        after.PreviousRunId == before.Snapshot.RunId && before.Snapshot.TargetId == after.Snapshot.TargetId &&
        before.Snapshot.TargetRevision == after.Snapshot.TargetRevision &&
        before.Snapshot.SourceSchemaVersion == ServerLifecycleSnapshot.CurrentSourceSchemaVersion &&
        after.Snapshot.SourceSchemaVersion == ServerLifecycleSnapshot.CurrentSourceSchemaVersion &&
        before.CompletedAtUtc < after.CompletedAtUtc && before.Snapshot.SourceObservedAtUtc < after.Snapshot.SourceObservedAtUtc &&
        before.Snapshot.SourceObservedAtUtc <= before.CompletedAtUtc && after.Snapshot.SourceObservedAtUtc <= after.CompletedAtUtc;

    private static ServerLifecycleDetectedEvent Create(ServerLifecycleEventKind kind, string identity,
        ServerLifecycleReceipt before, ServerLifecycleReceipt after)
    {
        string input = string.Join('|', "sqlobserver-lifecycle-event-v1", after.Snapshot.TargetId.Value.ToString("D"),
            after.Snapshot.TargetRevision.Value.ToString(CultureInfo.InvariantCulture),
            after.Snapshot.SourceSchemaVersion.ToString(CultureInfo.InvariantCulture),
            ((int)kind).ToString(CultureInfo.InvariantCulture), before.Snapshot.RunId.Value.ToString("D"),
            after.Snapshot.RunId.Value.ToString("D"), identity);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80); // UUID v8: application-defined SHA-256 layout.
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new(new Guid(hash.AsSpan(0, 16), bigEndian: true), kind, identity, before.Snapshot.RunId,
            after.Snapshot.RunId, before.Snapshot.SourceObservedAtUtc, after.Snapshot.SourceObservedAtUtc, after.CompletedAtUtc);
    }
}
