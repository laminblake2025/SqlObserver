using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Bounded identity comparisons from the connection that owns the CPU
/// bracket. Neither principal text nor expected/observed SID bytes are retained.</summary>
internal sealed record SqlCpuMonitorIdentityEvidence
{
    internal SqlCpuMonitorIdentityEvidence(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, Guid connectionSessionId, Guid clientConnectionId, int ordinal,
        DateTimeOffset observedAtUtc, Guid serverConnectionId, int serverSessionId,
        bool currentIdentityMatched, bool originalIdentityMatched, bool nonSysadmin,
        bool integrated, bool tcp, bool encrypted, bool noControlServer, bool noAlterAnyLogin)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        if (connectionSessionId == Guid.Empty || clientConnectionId == Guid.Empty || serverConnectionId == Guid.Empty ||
            ordinal is not (1 or 2) || serverSessionId is < 1 or > 32767 || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("SQL CPU monitor identity receipt is invalid.");
        ConnectionSessionId = connectionSessionId; ClientConnectionId = clientConnectionId;
        Ordinal = ordinal; ObservedAtUtc = observedAtUtc;
        ServerConnectionId = serverConnectionId; ServerSessionId = serverSessionId;
        CurrentIdentityMatched = currentIdentityMatched; OriginalIdentityMatched = originalIdentityMatched;
        NonSysadmin = nonSysadmin; Integrated = integrated; Tcp = tcp; Encrypted = encrypted;
        NoControlServer = noControlServer; NoAlterAnyLogin = noAlterAnyLogin;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public Guid ConnectionSessionId { get; }
    public Guid ClientConnectionId { get; }
    public int Ordinal { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public Guid ServerConnectionId { get; }
    public int ServerSessionId { get; }
    public bool CurrentIdentityMatched { get; }
    public bool OriginalIdentityMatched { get; }
    public bool NonSysadmin { get; }
    public bool Integrated { get; }
    public bool Tcp { get; }
    public bool Encrypted { get; }
    public bool NoControlServer { get; }
    public bool NoAlterAnyLogin { get; }
    public bool ChecksPassed => CurrentIdentityMatched && OriginalIdentityMatched && NonSysadmin &&
        Integrated && Tcp && Encrypted && NoControlServer && NoAlterAnyLogin;
}

internal sealed record SqlCpuAttestedWitnessBracket<T>(SqlCpuWitnessBracket<T> Bracket,
    SqlCpuMonitorIdentityEvidence BeforeIdentity, SqlCpuMonitorIdentityEvidence AfterIdentity)
{
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public bool HardwareIdentityVerified { get; }
}

internal static class SqlCpuMonitorIdentityValidation
{
    internal const int MaximumSidBytes = 68;

    internal static byte[] CopyExpectedSid(ReadOnlySpan<byte> value)
    {
        // Native revision-1 SID: six-byte authority and 1..15 DWORD subauthorities.
        // The exact binary length is checked before any SQL connection opens.
        if (value.Length is < 12 or > MaximumSidBytes || value[0] != 1 ||
            value[1] is < 1 or > 15 || value.Length != 8 + value[1] * 4)
            throw new ArgumentException("A bounded canonical monitor SID is required.", nameof(value));
        return value.ToArray();
    }

    internal static void RequireAllowed(SqlCpuMonitorIdentityEvidence receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.ChecksPassed)
            throw new UnauthorizedAccessException("The owned SQL CPU connection did not attest the configured monitoring identity and bounded security context.");
    }

    internal static void RequireWitness(SqlCpuMonitorIdentityEvidence receipt, SqlCpuTargetWitness witness)
    {
        RequireAllowed(receipt);
        if (receipt.TargetId != witness.TargetId || receipt.TargetRevision != witness.TargetRevision ||
            receipt.RunId != witness.RunId || receipt.ConnectionSessionId != witness.ConnectionSessionId ||
            receipt.ClientConnectionId != witness.ClientConnectionId || receipt.Ordinal != witness.Ordinal ||
            witness.PhysicalTransport != SqlCpuPhysicalTransport.Tcp || witness.SourceSchemaVersion != 2 ||
            witness.TcpConnection is null || receipt.ServerConnectionId != witness.TcpConnection.ServerConnectionId ||
            receipt.ServerSessionId != witness.TcpConnection.ServerSessionId)
            throw new InvalidDataException("The SQL CPU monitor identity does not belong to its owned witness connection.");
    }

    internal static void RequirePair(SqlCpuMonitorIdentityEvidence before, SqlCpuMonitorIdentityEvidence after,
        SqlCpuTargetWitness beforeWitness, SqlCpuTargetWitness afterWitness)
    {
        RequireWitness(before, beforeWitness); RequireWitness(after, afterWitness);
        if (before.ConnectionSessionId != after.ConnectionSessionId || before.ClientConnectionId != after.ClientConnectionId ||
            before.ServerConnectionId != after.ServerConnectionId || before.ServerSessionId != after.ServerSessionId ||
            before.TargetId != after.TargetId || before.TargetRevision != after.TargetRevision || before.RunId != after.RunId ||
            before.Ordinal != 1 || after.Ordinal != 2 || before.ObservedAtUtc > beforeWitness.SourceObservedAtUtc ||
            after.ObservedAtUtc < afterWitness.SourceObservedAtUtc || before.ObservedAtUtc >= after.ObservedAtUtc)
            throw new InvalidDataException("The SQL CPU monitor identity bracket changed or its source clocks are inconsistent.");
    }
}
