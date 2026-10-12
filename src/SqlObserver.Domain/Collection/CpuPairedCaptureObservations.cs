using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.Domain.Collection;

public enum CpuPairedCaptureGap
{
    SourceQualificationAbsent, HostBindingUnavailable, HostBindingDenied,
    HostBindingBoundary, HostCpuCapabilityUnavailable, NativeCaptureIncomplete,
    CandidateAssociationRejected, NumericalEvidenceIncomplete,
}

/// <summary>Privacy-safe copy of the actual SQL receipt. Socket addresses and
/// endpoint routing metadata are intentionally not retained.</summary>
public sealed record CpuPairedSqlEvidence(MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision, CollectorRunId RunId,
    Guid ConnectionSessionId, Guid ClientConnectionId, int Ordinal,
    DateTimeOffset SourceObservedAtUtc, ServerLifecycleEngineEvidence Engine,
    SqlCpuPhysicalTransport PhysicalTransport, long? ProcessUserTimeMs,
    long? ProcessKernelTimeMs, int? SqlReportedLogicalProcessors,
    int SourceSchemaVersion, Guid? ServerConnectionId, int? ServerSessionId,
    int? CollectorProcessId, CpuTcpSocketCompleteness? SocketCompleteness);

public enum CpuPairedSocketDirection { Other, ServerToClient, ClientToServer }
public sealed record CpuPairedSocketRow(CpuPairedSocketDirection Direction, uint State, uint OwnerProcessId);

/// <summary>Exact native accounting plus source-derived tuple direction. These
/// rows cannot replay the discarded endpoint match or independently admit it.</summary>
public sealed record CpuPairedTcpEvidence
{
    public CpuPairedTcpEvidence(PairedCpuBinding binding, DateTimeOffset observedAtUtc,
        long monotonic100ns, PairedCpuHostEvidence host, CpuTcpProcessEpoch sqlProcess,
        CpuTcpProcessEpoch collectorProcess, uint af4Status, uint af6Status,
        int rowCount4, int rowCount6, IReadOnlyList<CpuPairedSocketRow> matchingRows)
    {
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sqlProcess); ArgumentNullException.ThrowIfNull(collectorProcess);
        ArgumentNullException.ThrowIfNull(matchingRows);
        if (monotonic100ns < 0 || rowCount4 < 0 || rowCount6 < 0 ||
            (long)rowCount4 + rowCount6 > CpuTcpNativeFrame.MaximumTableRows ||
            matchingRows.Count > CpuTcpNativeFrame.MaximumMatchingRows ||
            matchingRows.Count > (long)rowCount4 + rowCount6 ||
            matchingRows.Any(static row => row is null || !Enum.IsDefined(row.Direction)))
            throw new ArgumentException("CPU capture TCP accounting is invalid.");
        Binding = binding; ObservedAtUtc = DomainValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        Monotonic100ns = monotonic100ns; Host = host; SqlProcess = sqlProcess; CollectorProcess = collectorProcess;
        Af4Status = af4Status; Af6Status = af6Status; RowCount4 = rowCount4; RowCount6 = rowCount6;
        MatchingRows = Array.AsReadOnly(matchingRows.ToArray());
    }
    public PairedCpuBinding Binding { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long Monotonic100ns { get; }
    public PairedCpuHostEvidence Host { get; }
    public CpuTcpProcessEpoch SqlProcess { get; }
    public CpuTcpProcessEpoch CollectorProcess { get; }
    public uint Af4Status { get; }
    public uint Af6Status { get; }
    public int RowCount4 { get; }
    public int RowCount6 { get; }
    public IReadOnlyList<CpuPairedSocketRow> MatchingRows { get; }
}

public sealed record CpuPairedFrameWitness(long? ProcessCreationBefore100ns,
    long? ProcessCreationAfter100ns, long? ProcessExitAfter100ns,
    uint LogicalProcessorsBefore, uint LogicalProcessorsAfter, int ProcessInstances,
    bool ProcessMappingAmbiguous, uint? ProcessWaitResultAfter);

/// <summary>Numerical eligibility only. Derived diagnostic arithmetic is not
/// retained as an admitted target percentage.</summary>
public sealed record CpuPairedNumericalEligibility(bool SqlProcessEligible, bool OperatingSystemEligible,
    bool SameNativeInterval, PairedCpuGapReason SqlProcessReason, PairedCpuGapReason OperatingSystemReason,
    long? SqlFromNative100ns, long? SqlToNative100ns, long? OsFromNative100ns, long? OsToNative100ns)
{
    public decimal? SqlProcessPercent { get; }
    public decimal? OperatingSystemPercent { get; }
}

/// <summary>Actual helper UTF-8 lengths are optional when no envelope was
/// received. SQL logical response is a declared upper bound, not wire bytes.</summary>
public sealed record CpuPairedCaptureByteAccounting(int? TcpBeforeHelperBytes,
    int? CpuPairHelperBytes, int? TcpAfterHelperBytes, long SqlLogicalResponseUpperBoundBytes);

/// <summary>One source-owned staged capture. Positive candidate evidence and
/// complete arithmetic never confer production admission or metric health.</summary>
public sealed record CpuPairedCaptureObservation
{
    private static readonly JsonSerializerOptions SizeOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ReadReasons = new(StringComparer.Ordinal)
    {
        "source_qualification_absent", "timed_out", "host_binding_unavailable", "cpu_capability_unavailable",
        "permission_denied", "unsupported", "invalid_source_output", "provider_failure", "host_identity_unavailable",
        "wrong_host", "sql_source_schema_unqualified", "tcp_source_unavailable", "unsupported_address", "sql_process_unavailable",
        "native_table_unavailable", "native_process_denied", "process_unavailable", "process_exited",
        "process_epoch_unavailable", "process_epoch_changed", "process_lifetime_unknown", "socket_not_observed",
        "process_mapping_ambiguous", "process_race", "process_missing", "process_counter_unavailable",
    };
    public const int CurrentSourceSchemaVersion = 1;
    public const int MaximumCaptureBytes = 65_536;
    public const int MaximumSourceFrames = 6;
    public const int MaximumHelperBytes = 65_536;
    public const long SqlLogicalResponseUpperBoundBytes = 16_384;

    public CpuPairedCaptureObservation(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, long? hostBindingRevision, long? hostProfileRevision,
        CpuPairedCaptureGap reason, CpuTcpAssociationGap candidateReason,
        CpuPairedSqlEvidence? sqlBefore, CpuPairedSqlEvidence? sqlAfter,
        CpuPairedTcpEvidence? tcpBefore, CpuPairedTcpEvidence? tcpAfter,
        PairedCpuEndpoint? cpuBefore, PairedCpuEndpoint? cpuAfter,
        PdhCpuRawCounter? idProcessBefore, PdhCpuRawCounter? idProcessAfter,
        CpuPairedFrameWitness? frameBefore, CpuPairedFrameWitness? frameAfter,
        string? executableFingerprintBefore, string? executableFingerprintAfter,
        uint processStatusBefore, uint processStatusAfter, uint osStatusBefore, uint osStatusAfter,
        CpuPairedNumericalEligibility? numericalEligibility, CpuPairedCaptureByteAccounting bytes,
        string? tcpBeforeReadReason = null, string? cpuPairReadReason = null,
        string? tcpAfterReadReason = null, string? processReadReason = null)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(runId); ArgumentNullException.ThrowIfNull(bytes);
        if (!Enum.IsDefined(reason) || !Enum.IsDefined(candidateReason) ||
            hostBindingRevision is <= 0 || hostProfileRevision is <= 0 || hostBindingRevision != hostProfileRevision ||
            bytes.TcpBeforeHelperBytes is < 0 or > MaximumHelperBytes ||
            bytes.CpuPairHelperBytes is < 0 or > MaximumHelperBytes || bytes.TcpAfterHelperBytes is < 0 or > MaximumHelperBytes ||
            bytes.SqlLogicalResponseUpperBoundBytes is < 0 or > SqlLogicalResponseUpperBoundBytes)
            throw new ArgumentException("CPU capture envelope is invalid.");
        PairedCpuBinding.ValidateFingerprint(executableFingerprintBefore);
        PairedCpuBinding.ValidateFingerprint(executableFingerprintAfter);
        foreach (string? readReason in new[] { tcpBeforeReadReason, cpuPairReadReason, tcpAfterReadReason, processReadReason })
            if (readReason is not null && !ReadReasons.Contains(readReason))
                throw new ArgumentException("CPU source refusal reason is not a fixed source token.");
        foreach (var sql in new[] { sqlBefore, sqlAfter })
            if (sql is not null)
            {
                if (sql.TargetId != targetId || sql.TargetRevision != targetRevision || sql.RunId != runId)
                    throw new ArgumentException("CPU SQL receipt belongs to another execution.");
                ValidateSql(sql);
            }
        if (sqlBefore is not null && sqlBefore.Ordinal != 1 || sqlAfter is not null && sqlAfter.Ordinal != 2)
            throw new ArgumentException("CPU SQL receipt ordinal is invalid.");
        foreach (var binding in new[] { tcpBefore?.Binding, tcpAfter?.Binding, cpuBefore?.Binding, cpuAfter?.Binding })
            if (binding is not null && (binding.TargetId != targetId || binding.TargetRevision != targetRevision ||
                binding.RunId != runId || binding.SqlProcessWitness != PairedCpuSqlProcessWitness.Unavailable))
                throw new ArgumentException("CPU raw receipt belongs to another execution or has promoted provenance.");
        bool complete = sqlBefore is not null && sqlAfter is not null && tcpBefore is not null && tcpAfter is not null &&
            cpuBefore is not null && cpuAfter is not null;
        if (candidateReason == CpuTcpAssociationGap.None && !complete || numericalEligibility is not null && candidateReason != CpuTcpAssociationGap.None)
            throw new ArgumentException("CPU diagnostic eligibility requires actual complete candidate evidence.");
        if (numericalEligibility is not null) ValidateEligibility(numericalEligibility);
        foreach (var frame in new[] { frameBefore, frameAfter })
            if (frame is not null && (frame.ProcessInstances is < 0 or > 1024))
                throw new ArgumentException("CPU frame accounting is invalid.");
        TargetId = targetId; TargetRevision = targetRevision; RunId = runId;
        HostBindingRevision = hostBindingRevision; HostProfileRevision = hostProfileRevision;
        Reason = reason; CandidateReason = candidateReason; State = complete ? PairedCpuState.Partial : PairedCpuState.Unavailable;
        SqlBefore = sqlBefore; SqlAfter = sqlAfter; TcpBefore = tcpBefore; TcpAfter = tcpAfter;
        CpuBefore = cpuBefore; CpuAfter = cpuAfter; IdProcessBefore = idProcessBefore; IdProcessAfter = idProcessAfter;
        FrameBefore = frameBefore; FrameAfter = frameAfter; ExecutableFingerprintBefore = executableFingerprintBefore;
        ExecutableFingerprintAfter = executableFingerprintAfter; ProcessStatusBefore = processStatusBefore;
        ProcessStatusAfter = processStatusAfter; OsStatusBefore = osStatusBefore; OsStatusAfter = osStatusAfter;
        NumericalEligibility = numericalEligibility; Bytes = bytes;
        TcpBeforeReadReason = tcpBeforeReadReason; CpuPairReadReason = cpuPairReadReason;
        TcpAfterReadReason = tcpAfterReadReason; ProcessReadReason = processReadReason;
    }
    public int SourceSchemaVersion { get; } = CurrentSourceSchemaVersion;
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public long? HostBindingRevision { get; }
    public long? HostProfileRevision { get; }
    public PairedCpuState State { get; }
    public CpuPairedCaptureGap Reason { get; }
    public CpuTcpAssociationGap CandidateReason { get; }
    public bool CandidateLocalAssociation => CandidateReason == CpuTcpAssociationGap.None;
    public CpuPairedSqlEvidence? SqlBefore { get; }
    public CpuPairedSqlEvidence? SqlAfter { get; }
    public CpuPairedTcpEvidence? TcpBefore { get; }
    public CpuPairedTcpEvidence? TcpAfter { get; }
    public PairedCpuEndpoint? CpuBefore { get; }
    public PairedCpuEndpoint? CpuAfter { get; }
    public PdhCpuRawCounter? IdProcessBefore { get; }
    public PdhCpuRawCounter? IdProcessAfter { get; }
    public CpuPairedFrameWitness? FrameBefore { get; }
    public CpuPairedFrameWitness? FrameAfter { get; }
    public string? ExecutableFingerprintBefore { get; }
    public string? ExecutableFingerprintAfter { get; }
    public uint ProcessStatusBefore { get; }
    public uint ProcessStatusAfter { get; }
    public uint OsStatusBefore { get; }
    public uint OsStatusAfter { get; }
    public CpuPairedNumericalEligibility? NumericalEligibility { get; }
    public CpuPairedCaptureByteAccounting Bytes { get; }
    public string? TcpBeforeReadReason { get; }
    public string? CpuPairReadReason { get; }
    public string? TcpAfterReadReason { get; }
    public string? ProcessReadReason { get; }
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public bool HardwareIdentityVerified { get; }
    public decimal? SqlProcessPercent { get; }
    public decimal? OperatingSystemPercent { get; }
    public int SourceFrames => (SqlBefore is null ? 0 : 1) + (SqlAfter is null ? 0 : 1) +
        (TcpBefore is null ? 0 : 1) + (TcpAfter is null ? 0 : 1) +
        (CpuBefore is null ? 0 : 1) + (CpuAfter is null ? 0 : 1);
    [JsonIgnore]
    public int EstimatedSizeBytes => JsonSerializer.SerializeToUtf8Bytes(this, SizeOptions).Length;

    private static void ValidateSql(CpuPairedSqlEvidence sql)
    {
        ArgumentNullException.ThrowIfNull(sql.Engine);
        DomainValidation.RequireUtc(sql.SourceObservedAtUtc, nameof(sql.SourceObservedAtUtc));
        if (sql.ConnectionSessionId == Guid.Empty || sql.ClientConnectionId == Guid.Empty || sql.Ordinal is not (1 or 2) ||
            sql.SourceSchemaVersion is not (1 or 2) || !Enum.IsDefined(sql.PhysicalTransport) ||
            sql.ProcessUserTimeMs < 0 || sql.ProcessKernelTimeMs < 0 || sql.SqlReportedLogicalProcessors is <= 0 or > 4096 ||
            sql.ServerConnectionId == Guid.Empty || sql.ServerSessionId is < 1 or > 32767 || sql.CollectorProcessId is <= 0 ||
            sql.SocketCompleteness is { } completeness && !Enum.IsDefined(completeness) ||
            (sql.ServerConnectionId is null) != (sql.ServerSessionId is null) ||
            (sql.ServerConnectionId is null) != (sql.CollectorProcessId is null) ||
            (sql.ServerConnectionId is null) != (sql.SocketCompleteness is null))
            throw new ArgumentException("CPU SQL receipt is invalid.");
    }

    private static void ValidateEligibility(CpuPairedNumericalEligibility value)
    {
        static bool Component(bool eligible, PairedCpuGapReason reason, long? from, long? to) =>
            Enum.IsDefined(reason) && (eligible
                ? reason == PairedCpuGapReason.None && from > 0 && to > from
                : reason != PairedCpuGapReason.None && from is null && to is null);
        if (!Component(value.SqlProcessEligible, value.SqlProcessReason, value.SqlFromNative100ns, value.SqlToNative100ns) ||
            !Component(value.OperatingSystemEligible, value.OperatingSystemReason, value.OsFromNative100ns, value.OsToNative100ns) ||
            value.SameNativeInterval != (value.SqlProcessEligible && value.OperatingSystemEligible &&
                value.SqlFromNative100ns == value.OsFromNative100ns && value.SqlToNative100ns == value.OsToNative100ns))
            throw new ArgumentException("CPU numerical eligibility is inconsistent with its exact anchors.");
    }
}
