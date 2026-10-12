using System.Globalization;

namespace SqlObserver.Domain.Collection;

/// <summary>Completeness counts bounded source frames, never native qualification or health.</summary>
public sealed record CpuCaptureWindowHistoryCoverage(int RunCount, int RetainedReceiptCount,
    int CompleteRunCount, int GapRunCount, int FailedRunCount, int LostRunCount,
    int MissingPayloadRunCount, bool HasVisibilityGap);
public sealed record CpuCaptureWindowHistoryReceipt(Guid RunId, long SampleTargetRevision,
    DateTimeOffset CompletedAtUtc, string Outcome, string LossKind, int MinimumLostItems,
    bool CapturePresent, int? SourceFrames, string State, string Reason);
public sealed record CpuCaptureWindowHistoryCounter(uint CounterType, uint CStatus, string FirstValue,
    string SecondValue, string CollectedLocalFileTime100ns, uint MultiCount, uint ApiStatus, string TimeBase);
public sealed record CpuCaptureWindowHistoryProcess(string? CreationBefore100ns, string? CreationAfter100ns,
    string? ExitAfter100ns, uint ApiStatus, uint? WaitResultAfter);
public sealed record CpuCaptureWindowHistorySql(string SourceObservedAtUtc, int Ordinal, int SourceSchemaVersion,
    int? ProcessId, string? SourceStartLocal, string? StartMsTicks, string? CurrentMsTicks,
    string? ProcessUserTimeMs, string? ProcessKernelTimeMs, int? SqlReportedLogicalProcessors);
public sealed record CpuCaptureWindowHistoryTcp(string ObservedAtUtc, string Monotonic100ns,
    uint Af4Status, uint Af6Status, int RowCount4, int RowCount6, int MatchingRowCount,
    CpuCaptureWindowHistoryProcess SqlProcess, CpuCaptureWindowHistoryProcess CollectorProcess);
public sealed record CpuCaptureWindowHistoryCpu(string ObservedAtUtc, string MonotonicTicks, string MonotonicFrequency,
    string? SqlProcessCreationFileTime100ns, uint? LogicalProcessorCount,
    CpuCaptureWindowHistoryCounter? ProcessCounter, CpuCaptureWindowHistoryCounter? OsCounter);
public sealed record CpuCaptureWindowHistoryFrame(string? ProcessCreationBefore100ns, string? ProcessCreationAfter100ns,
    string? ProcessExitAfter100ns, uint LogicalProcessorsBefore, uint LogicalProcessorsAfter,
    int ProcessInstances, bool ProcessMappingAmbiguous, uint? ProcessWaitResultAfter);
public sealed record CpuCaptureWindowHistoryEligibility(bool SqlProcessEligible, bool OperatingSystemEligible,
    bool SameNativeInterval, string SqlProcessReason, string OperatingSystemReason,
    string? SqlFromNative100ns, string? SqlToNative100ns, string? OsFromNative100ns, string? OsToNative100ns);
public sealed record CpuCaptureWindowHistoryBytes(int? TcpBeforeHelperBytes, int? CpuPairHelperBytes,
    int? TcpAfterHelperBytes, string SqlLogicalResponseUpperBoundBytes);
/// <summary>Whitelisted diagnostic copy. Discarded socket/identity data cannot be re-attested by this receipt.</summary>
public sealed record CpuCaptureWindowHistoryCapture(int SourceSchemaVersion, string State, string Reason,
    string CandidateReason, bool CandidateLocalAssociation, int SourceFrames,
    CpuCaptureWindowHistorySql? SqlBefore, CpuCaptureWindowHistorySql? SqlAfter,
    CpuCaptureWindowHistoryTcp? TcpBefore, CpuCaptureWindowHistoryTcp? TcpAfter,
    CpuCaptureWindowHistoryCpu? CpuBefore, CpuCaptureWindowHistoryCpu? CpuAfter,
    CpuCaptureWindowHistoryCounter? IdProcessBefore, CpuCaptureWindowHistoryCounter? IdProcessAfter,
    CpuCaptureWindowHistoryFrame? FrameBefore, CpuCaptureWindowHistoryFrame? FrameAfter,
    uint ProcessStatusBefore, uint ProcessStatusAfter, uint OsStatusBefore, uint OsStatusAfter,
    string? TcpBeforeReadReason, string? CpuPairReadReason, string? TcpAfterReadReason, string? ProcessReadReason,
    CpuCaptureWindowHistoryEligibility? NumericalEligibility, CpuCaptureWindowHistoryBytes Bytes)
{
    public string NativeQualification { get; init; } = "absent";
    public bool TargetComparisonAdmitted { get; init; }
    public bool HardwareIdentityVerified { get; init; }
    public string? SqlProcessPercent { get; init; }
    public string? OperatingSystemPercent { get; init; }
}

public static class CpuCaptureWindowHistoryValidation
{
    public const int MaximumPageSize = 100;
    public const int MaximumRuns = 10_000;
    public const long MaximumRevision = 9_007_199_254_740_991;
    public static TimeSpan CursorLifetime => TimeSpan.FromMinutes(15);
    private static readonly HashSet<string> Outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
    private static readonly HashSet<string> Losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "visibility_incomplete"];
    private static readonly HashSet<string> CaptureReasons = ["source_qualification_absent", "host_binding_unavailable", "host_binding_denied", "host_binding_boundary", "host_cpu_capability_unavailable", "native_capture_incomplete", "candidate_association_rejected", "numerical_evidence_incomplete"];
    private static readonly HashSet<string> ReadReasons = ["source_qualification_absent", "timed_out", "host_binding_unavailable", "cpu_capability_unavailable", "permission_denied", "unsupported", "invalid_source_output", "provider_failure", "host_identity_unavailable", "wrong_host", "sql_source_schema_unqualified", "tcp_source_unavailable", "unsupported_address", "sql_process_unavailable", "native_table_unavailable", "native_process_denied", "process_unavailable", "process_exited", "process_epoch_unavailable", "process_epoch_changed", "process_lifetime_unknown", "socket_not_observed", "process_mapping_ambiguous", "process_race", "process_missing", "process_counter_unavailable"];
    public static void Id(Guid value) { if (value == Guid.Empty) throw new ArgumentException("A nonempty UUID is required."); }
    public static void Revision(long value) { if (value is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(value)); }
    public static void Utc(DateTimeOffset value) => DomainValidation.RequireUtcMicrosecondAligned(value, nameof(value));
    public static void Window(DateTimeOffset from, DateTimeOffset to)
    { Utc(from); Utc(to); if (from >= to || to-from > TimeSpan.FromDays(31)) throw new ArgumentException("A UTC window of at most 31 days is required."); }
    public static void Digest(string value)
    { if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Invalid source epoch."); }
    public static long Counter(string value, bool allowNegative = false)
    {
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long result) ||
            result.ToString(CultureInfo.InvariantCulture) != value || !allowNegative && result < 0) throw new ArgumentException("An exact Int64 decimal is required.");
        return result;
    }
    public static void SourceUtc(string value)
    { if (!DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)) throw new ArgumentException("Exact seven-digit UTC source time is required."); }
    public static void Coverage(CpuCaptureWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.RunCount is < 0 or > MaximumRuns || value.RetainedReceiptCount < 0 || value.RetainedReceiptCount > value.RunCount ||
            value.CompleteRunCount < 0 || value.CompleteRunCount > value.RetainedReceiptCount || value.GapRunCount < 0 || value.CompleteRunCount + value.GapRunCount != value.RunCount ||
            value.FailedRunCount < 0 || value.FailedRunCount > value.GapRunCount || value.LostRunCount < 0 || value.LostRunCount > value.RunCount ||
            value.MissingPayloadRunCount < 0 || value.MissingPayloadRunCount > value.GapRunCount || value.GapRunCount > 0 && !value.HasVisibilityGap)
            throw new ArgumentException("CPU capture coverage is inconsistent.");
    }
    public static void Receipt(CpuCaptureWindowHistoryReceipt value)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        if (!Outcomes.Contains(value.Outcome) || !Losses.Contains(value.LossKind) || value.MinimumLostItems is < 0 or > 6 ||
            value.LossKind == "none" && value.MinimumLostItems != 0 || value.SourceFrames is < 0 or > 6 ||
            value.CapturePresent != (value.SourceFrames is not null) ||
            value.CapturePresent && (value.Outcome != "succeeded" || value.LossKind != "none" || !CaptureReasons.Contains(value.Reason) || value.State != (value.SourceFrames == 6 ? "partial" : "unavailable")) ||
            !value.CapturePresent && (value.State != "unavailable" || value.Reason != (value.Outcome == "succeeded" ? "payload_missing" : "collection_failed")))
            throw new ArgumentException("CPU capture receipt overstates retained evidence.");
    }
    public static void Capture(CpuCaptureWindowHistoryCapture value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Bytes);
        int frames = (value.SqlBefore is null ? 0 : 1)+(value.SqlAfter is null ? 0 : 1)+(value.TcpBefore is null ? 0 : 1)+(value.TcpAfter is null ? 0 : 1)+(value.CpuBefore is null ? 0 : 1)+(value.CpuAfter is null ? 0 : 1);
        if (value.SourceSchemaVersion != 1 || value.SourceFrames != frames || value.State != (frames==6 ? "partial" : "unavailable") || !CaptureReasons.Contains(value.Reason) ||
            !Enum.TryParse<CpuTcpAssociationGap>(value.CandidateReason, out var candidate) || !Enum.IsDefined(candidate) || candidate.ToString()!=value.CandidateReason ||
            value.CandidateLocalAssociation != (candidate==CpuTcpAssociationGap.None) || candidate==CpuTcpAssociationGap.None && frames!=6 ||
            value.NativeQualification!="absent" || value.TargetComparisonAdmitted || value.HardwareIdentityVerified || value.SqlProcessPercent is not null || value.OperatingSystemPercent is not null)
            throw new ArgumentException("CPU retained diagnostics cannot admit source qualification.");
        foreach (string? reason in new[] { value.TcpBeforeReadReason,value.CpuPairReadReason,value.TcpAfterReadReason,value.ProcessReadReason })
            if (reason is not null && !ReadReasons.Contains(reason)) throw new ArgumentException("Unknown fixed source reason.");
        Sql(value.SqlBefore,1); Sql(value.SqlAfter,2); Tcp(value.TcpBefore); Tcp(value.TcpAfter); Cpu(value.CpuBefore); Cpu(value.CpuAfter);
        Raw(value.IdProcessBefore); Raw(value.IdProcessAfter); Frame(value.FrameBefore); Frame(value.FrameAfter);
        if (value.Bytes.TcpBeforeHelperBytes is < 0 or > 65536 || value.Bytes.CpuPairHelperBytes is < 0 or > 65536 || value.Bytes.TcpAfterHelperBytes is < 0 or > 65536 || Counter(value.Bytes.SqlLogicalResponseUpperBoundBytes)>16384)
            throw new ArgumentException("Invalid source byte accounting.");
        if (value.NumericalEligibility is { } eligibility)
        {
            if (!value.CandidateLocalAssociation) throw new ArgumentException("Numerical diagnostics require a source-derived candidate.");
            Component(eligibility.SqlProcessEligible,eligibility.SqlProcessReason,eligibility.SqlFromNative100ns,eligibility.SqlToNative100ns);
            Component(eligibility.OperatingSystemEligible,eligibility.OperatingSystemReason,eligibility.OsFromNative100ns,eligibility.OsToNative100ns);
            if (eligibility.SameNativeInterval != (eligibility.SqlProcessEligible && eligibility.OperatingSystemEligible && eligibility.SqlFromNative100ns==eligibility.OsFromNative100ns && eligibility.SqlToNative100ns==eligibility.OsToNative100ns)) throw new ArgumentException("Unaligned native anchors.");
        }
    }
    private static void Optional(string? value, bool allowNegative=false) { if (value is not null) _=Counter(value,allowNegative); }
    private static void Raw(CpuCaptureWindowHistoryCounter? value)
    { if (value is not null) { Counter(value.FirstValue,true); Counter(value.SecondValue,true); Counter(value.CollectedLocalFileTime100ns,true); Counter(value.TimeBase,true); } }
    private static void Process(CpuCaptureWindowHistoryProcess value)
    { ArgumentNullException.ThrowIfNull(value); Optional(value.CreationBefore100ns); Optional(value.CreationAfter100ns); Optional(value.ExitAfter100ns,true); }
    private static void Sql(CpuCaptureWindowHistorySql? value,int ordinal)
    {
        if (value is null) return; SourceUtc(value.SourceObservedAtUtc);
        if (value.Ordinal!=ordinal || value.SourceSchemaVersion is not (1 or 2) || value.ProcessId is <=0 || value.SqlReportedLogicalProcessors is <=0 or >4096 ||
            value.SourceStartLocal is { } local && !DateTime.TryParseExact(local,"yyyy-MM-dd'T'HH:mm:ss.fffffff",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) throw new ArgumentException("Invalid SQL source receipt.");
        Optional(value.StartMsTicks); Optional(value.CurrentMsTicks); Optional(value.ProcessUserTimeMs); Optional(value.ProcessKernelTimeMs);
    }
    private static void Tcp(CpuCaptureWindowHistoryTcp? value)
    { if (value is null) return; SourceUtc(value.ObservedAtUtc); Counter(value.Monotonic100ns); Process(value.SqlProcess); Process(value.CollectorProcess);
        if (value.RowCount4<0 || value.RowCount6<0 || (long)value.RowCount4+value.RowCount6>4096 || value.MatchingRowCount is <0 or >16 || value.MatchingRowCount>(long)value.RowCount4+value.RowCount6) throw new ArgumentException("Invalid native enumeration accounting."); }
    private static void Cpu(CpuCaptureWindowHistoryCpu? value)
    { if (value is null) return; SourceUtc(value.ObservedAtUtc); Counter(value.MonotonicTicks); if (Counter(value.MonotonicFrequency)<=0 || value.LogicalProcessorCount==0) throw new ArgumentException("Invalid CPU clock/topology evidence."); Optional(value.SqlProcessCreationFileTime100ns); Raw(value.ProcessCounter); Raw(value.OsCounter); }
    private static void Frame(CpuCaptureWindowHistoryFrame? value)
    { if (value is null) return; Optional(value.ProcessCreationBefore100ns); Optional(value.ProcessCreationAfter100ns); Optional(value.ProcessExitAfter100ns,true); if (value.ProcessInstances is <0 or >1024) throw new ArgumentException("Invalid process enumeration count."); }
    private static void Component(bool eligible,string reason,string? from,string? to)
    { if (!Enum.TryParse<PairedCpuGapReason>(reason,out var parsed) || !Enum.IsDefined(parsed) || parsed.ToString()!=reason ||
            (eligible ? parsed!=PairedCpuGapReason.None || from is null || to is null || Counter(from)<=0 || Counter(to)<=Counter(from) : parsed==PairedCpuGapReason.None || from is not null || to is not null)) throw new ArgumentException("Contradictory arithmetic eligibility."); }
}
