using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;

namespace SqlObserver.Domain.Collection;

/// <summary>Closed, privacy-minimized replication topology states.</summary>
public enum ReplicationTopologyState
{
    Publisher = 1,
    Subscriber = 2,
    LocalDistributor = 3,
    RemoteDistributor = 4,
    Transactional = 5,
    Merge = 6,
    Unsupported = 7,
}

public enum ReplicationRole { Unknown = 1, Publisher = 2, Subscriber = 3, Distributor = 4 }
public enum ReplicationStatus { Unknown = 1, Healthy = 2, Warning = 3, Failed = 4, Initializing = 5, Disabled = 6 }
public enum ReplicationCoverage { Complete = 1, LocalSummary = 2, VisibilityGap = 3, Unknown = 4 }

/// <summary>
/// Explicit registration of the distribution database used for replication
/// evidence. The value is configuration/control-plane data, never inferred
/// from a target endpoint or a discovered server name.
/// </summary>
public sealed record ReplicationDistributionBinding
{
    public ReplicationDistributionBinding(string databaseName, int? tcpPort = null)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || databaseName.Length > 128 || databaseName.Trim() != databaseName ||
            databaseName is "." or ".." || databaseName.Any(static c => !(char.IsLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("The distribution database must be an explicit safe identifier.", nameof(databaseName));
        if (tcpPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        DatabaseName = databaseName;
        TcpPort = tcpPort;
    }

    public string DatabaseName { get; }
    public int? TcpPort { get; }
}

/// <summary>A bounded age is deliberately relative; local SQL timestamps are never persisted.</summary>
public sealed record ReplicationLastSuccessAge(TimeSpan? Age, bool Unknown)
{
    public ReplicationLastSuccessAge(TimeSpan? age) : this(age, age is null) { }
    public static ReplicationLastSuccessAge UnknownAge { get; } = new(null, true);
    public bool IsKnown => !Unknown && Age is not null;
}

/// <summary>One replication observation. It contains no server, endpoint, command, message, LSN, or name.</summary>
public sealed record ReplicationObservation(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    ReplicationTopologyState Topology,
    ReplicationRole Role,
    string? PublicationFingerprint,
    string? SubscriptionFingerprint,
    ReplicationStatus Status,
    int? PendingCommands,
    decimal? LatencySeconds,
    decimal? RatePerSecond,
    ReplicationLastSuccessAge LastSuccess,
    ReplicationCoverage Coverage)
{
    public bool IsDetailed => Coverage == ReplicationCoverage.Complete;
    public bool HasVisibilityGap => Coverage == ReplicationCoverage.VisibilityGap;
    /// <summary>Opaque deterministic identity retained when topology names are unavailable.</summary>
    public string? VisibilityGapFingerprint { get; init; }
}

public static class ReplicationIdentityFingerprint
{
    public static string FromTypedIdentity(string domain, ReadOnlySpan<byte> identity, IdentityFingerprintKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("Fingerprint domain is required.", nameof(domain));
        byte[] prefix = System.Text.Encoding.UTF8.GetBytes($"sqlobserver-{domain}-v1\0");
        byte[] input = new byte[prefix.Length + identity.Length];
        prefix.CopyTo(input, 0);
        identity.CopyTo(input.AsSpan(prefix.Length));
        return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(key.Span, input)).ToLowerInvariant();
    }

    public static string Gap(Guid targetId, long targetRevision, int topology, int role, int ordinal, IdentityFingerprintKey key) =>
        FromTypedIdentity("replication-gap", System.Text.Encoding.UTF8.GetBytes($"{targetId:D}|{targetRevision}|{topology}|{role}|{ordinal}"), key);

}

public sealed partial record ReplicationHealthSnapshot(
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    CollectorRunId? RunId,
    DateTimeOffset ObservedAtUtc,
    OperationalObservationState State,
    IReadOnlyList<ReplicationObservation> Items,
    ReplicationCoverage Coverage,
    int SourceRowsRead,
    bool Truncated) : IOperationalHealthSnapshot;

public sealed partial record ReplicationHealthSnapshot { public string? NextCursor { get; init; } }

public static class ReplicationBounds
{
    public const int MaximumRows = 2_048;
    public const int MaximumResponseBytes = 2_097_152;
    public const int MaximumPendingCommands = 2_000_000_000;
    public const decimal MaximumLatencySeconds = 86_400m;
    public const decimal MaximumRatePerSecond = 2_000_000_000m;
}
