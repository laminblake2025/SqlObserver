using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum SqlCpuPhysicalTransport { Unknown, Tcp, NamedPipe, SharedMemory }
public enum SqlCpuWitnessGap
{
    None, TargetBoundary, RevisionBoundary, RunBoundary, ConnectionBoundary,
    SourceIncomplete, EngineBoundary, ClockBoundary, CounterReset, TopologyBoundary,
}

/// <summary>A SQL-owned source receipt. SQL node names are not hardware UUIDs,
/// and this receipt does not associate a local Windows PID with this endpoint.</summary>
public sealed record SqlCpuTargetWitness
{
    public SqlCpuTargetWitness(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, Guid connectionSessionId, Guid clientConnectionId, int ordinal,
        DateTimeOffset sourceObservedAtUtc, ServerLifecycleEngineEvidence engine,
        SqlCpuPhysicalTransport physicalTransport, long? processUserTimeMs,
        long? processKernelTimeMs, int? sqlReportedLogicalProcessors)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        Engine = engine ?? throw new ArgumentNullException(nameof(engine));
        if (connectionSessionId == Guid.Empty || clientConnectionId == Guid.Empty || ordinal is not (1 or 2) ||
            !Enum.IsDefined(physicalTransport) || processUserTimeMs < 0 || processKernelTimeMs < 0 ||
            sqlReportedLogicalProcessors is <= 0 or > 4096)
            throw new ArgumentException("SQL CPU source witness is invalid.");
        ConnectionSessionId = connectionSessionId;
        ClientConnectionId = clientConnectionId;
        Ordinal = ordinal;
        SourceObservedAtUtc = DomainValidation.RequireUtc(sourceObservedAtUtc, nameof(sourceObservedAtUtc));
        PhysicalTransport = physicalTransport;
        ProcessUserTimeMs = processUserTimeMs;
        ProcessKernelTimeMs = processKernelTimeMs;
        SqlReportedLogicalProcessors = sqlReportedLogicalProcessors;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public Guid ConnectionSessionId { get; }
    public Guid ClientConnectionId { get; }
    public int Ordinal { get; }
    public DateTimeOffset SourceObservedAtUtc { get; }
    public ServerLifecycleEngineEvidence Engine { get; }
    public SqlCpuPhysicalTransport PhysicalTransport { get; }
    public long? ProcessUserTimeMs { get; }
    public long? ProcessKernelTimeMs { get; }
    /// <summary>SQL-reported topology, never a substitute for the PDH all-group denominator.</summary>
    public int? SqlReportedLogicalProcessors { get; }
    public bool LocalWindowsProcessAssociationQualified { get; }
    public bool HardwareIdentityVerified { get; }
}

/// <summary>Checks SQL receipt continuity only. Even shared-memory transport
/// requires native association qualification before admitting a target comparison.</summary>
public static class SqlCpuWitnessContinuity
{
    public static SqlCpuWitnessGap Evaluate(SqlCpuTargetWitness before, SqlCpuTargetWitness after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.TargetId != after.TargetId) return SqlCpuWitnessGap.TargetBoundary;
        if (before.TargetRevision != after.TargetRevision) return SqlCpuWitnessGap.RevisionBoundary;
        if (before.RunId != after.RunId) return SqlCpuWitnessGap.RunBoundary;
        if (before.ConnectionSessionId != after.ConnectionSessionId ||
            before.ClientConnectionId != after.ClientConnectionId || before.Ordinal != 1 || after.Ordinal != 2)
            return SqlCpuWitnessGap.ConnectionBoundary;
        if (before.PhysicalTransport == SqlCpuPhysicalTransport.Unknown || after.PhysicalTransport == SqlCpuPhysicalTransport.Unknown)
            return SqlCpuWitnessGap.SourceIncomplete;
        if (before.PhysicalTransport != after.PhysicalTransport) return SqlCpuWitnessGap.ConnectionBoundary;
        if (before.Engine.Coverage != ServerLifecycleCoverage.Complete || after.Engine.Coverage != ServerLifecycleCoverage.Complete ||
            before.ProcessUserTimeMs is null ||
            after.ProcessUserTimeMs is null || before.ProcessKernelTimeMs is null || after.ProcessKernelTimeMs is null ||
            before.SqlReportedLogicalProcessors is null || after.SqlReportedLogicalProcessors is null)
            return SqlCpuWitnessGap.SourceIncomplete;
        if (before.Engine.StartupIncarnationFingerprint != after.Engine.StartupIncarnationFingerprint)
            return SqlCpuWitnessGap.EngineBoundary;
        if (after.SourceObservedAtUtc <= before.SourceObservedAtUtc || after.Engine.CurrentMsTicks <= before.Engine.CurrentMsTicks)
            return SqlCpuWitnessGap.ClockBoundary;
        if (after.ProcessUserTimeMs < before.ProcessUserTimeMs || after.ProcessKernelTimeMs < before.ProcessKernelTimeMs)
            return SqlCpuWitnessGap.CounterReset;
        return before.SqlReportedLogicalProcessors != after.SqlReportedLogicalProcessors
            ? SqlCpuWitnessGap.TopologyBoundary : SqlCpuWitnessGap.None;
    }
}
