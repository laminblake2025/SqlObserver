using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Declared capture provenance only; this enum never admits a target-to-native-process association.</summary>
public enum PairedCpuSqlProcessWitness { Unavailable = 0, TargetEngineProcessId = 1 }
public enum PairedCpuHostWitness { Unavailable = 0, NativeMachineAndBoot = 1 }
public enum PairedCpuNativeQualification { Absent = 0 }
public enum PairedCpuState { Unavailable = 0, Partial = 1, Available = 2 }
public enum PairedCpuGapReason
{
    None, Baseline, TargetBoundary, RevisionBoundary, RunBoundary, QueryBoundary,
    SourceQualificationAbsent, HostBindingMismatch, MachineBoundary, BootBoundary,
    ProcessRestart, ProcessorTopologyChanged, ClockBoundary, ClockSkew,
    SourceUnavailable, CounterStatusInvalid, CounterTypeMismatch, CounterTimeBaseMismatch,
    CounterReset, DenominatorInvalid, IntervalMismatch, CounterRangeInvalid,
}

/// <summary>Exact native PDH data, including failed status. FILETIME collection
/// time remains an opaque local provider value, not an inferred UTC timestamp.</summary>
public sealed record PdhCpuRawCounter(uint CounterType, uint CStatus, long FirstValue,
    long SecondValue, long CollectedLocalFileTime100ns, uint MultiCount = 0,
    uint ApiStatus = 0, long TimeBase = PdhCpuRawCounter.HundredNanosecondTimeBase)
{
    // Microsoft winperf.h / Timer Algorithm Counter Types. These are not cooked percentages.
    public const uint ProcessTimerType = 0x20510500;
    public const uint OsIdleInverseTimerType = 0x21510500;
    public const long HundredNanosecondTimeBase = 10_000_000;
    public const uint ValidData = 0;
    public const uint NewData = 1;
}

/// <summary>Candidate capture identity for numerical consistency checks. A supplied
/// PID, hostname fingerprint or provenance enum does not prove target association.</summary>
public sealed record PairedCpuBinding
{
    public PairedCpuBinding(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, string? expectedHostFingerprint, int? sqlProcessId,
        PairedCpuSqlProcessWitness sqlProcessWitness = PairedCpuSqlProcessWitness.Unavailable)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ValidateFingerprint(expectedHostFingerprint);
        if (sqlProcessId is <= 0 || !Enum.IsDefined(sqlProcessWitness))
            throw new ArgumentException("Paired CPU target process binding is invalid.");
        ExpectedHostFingerprint = expectedHostFingerprint;
        SqlProcessId = sqlProcessId;
        SqlProcessWitness = sqlProcessWitness;
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public string? ExpectedHostFingerprint { get; }
    public int? SqlProcessId { get; }
    public PairedCpuSqlProcessWitness SqlProcessWitness { get; }

    internal static void ValidateFingerprint(string? value)
    {
        if (value is not null && (value.Length != 64 || value.Any(static c => !char.IsAsciiHexDigit(c)) ||
            value.Any(static c => char.IsAsciiLetterUpper(c))))
            throw new ArgumentException("Paired CPU identity must be a canonical lowercase fingerprint.", nameof(value));
    }
}

/// <summary>Independently observed host identity and boot epoch. This is explicit
/// source provenance; it does not certify a global physical hardware incarnation.</summary>
public sealed record PairedCpuHostEvidence
{
    public PairedCpuHostEvidence(string? actualHostFingerprint, string? machineFingerprint,
        long? bootFileTime100ns, PairedCpuHostWitness witness = PairedCpuHostWitness.Unavailable)
    {
        PairedCpuBinding.ValidateFingerprint(actualHostFingerprint);
        PairedCpuBinding.ValidateFingerprint(machineFingerprint);
        if (bootFileTime100ns is <= 0 || !Enum.IsDefined(witness))
            throw new ArgumentException("Paired CPU host evidence is invalid.");
        ActualHostFingerprint = actualHostFingerprint;
        MachineFingerprint = machineFingerprint;
        BootFileTime100ns = bootFileTime100ns;
        Witness = witness;
    }

    public string? ActualHostFingerprint { get; }
    public string? MachineFingerprint { get; }
    public long? BootFileTime100ns { get; }
    public PairedCpuHostWitness Witness { get; }
    public bool HardwareIdentityVerified { get; }
}

/// <summary>One collection from an owned native PDH query. Counters are independently
/// nullable. Neither a successful query nor its UTC timestamp proves counter validity.</summary>
public sealed record PairedCpuEndpoint
{
    public PairedCpuEndpoint(PairedCpuBinding binding, Guid queryId, DateTimeOffset observedAtUtc,
        long monotonicTicks, long monotonicFrequency, PairedCpuHostEvidence host,
        long? sqlProcessCreationFileTime100ns, uint? logicalProcessorCount,
        PdhCpuRawCounter? processCounter, PdhCpuRawCounter? osCounter)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        if (queryId == Guid.Empty || monotonicTicks < 0 || monotonicFrequency <= 0 ||
            sqlProcessCreationFileTime100ns is <= 0 || logicalProcessorCount is 0)
            throw new ArgumentException("Paired CPU capture identity is invalid.");
        QueryId = queryId;
        ObservedAtUtc = DomainValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        MonotonicTicks = monotonicTicks;
        MonotonicFrequency = monotonicFrequency;
        SqlProcessCreationFileTime100ns = sqlProcessCreationFileTime100ns;
        LogicalProcessorCount = logicalProcessorCount;
        ProcessCounter = processCounter;
        OsCounter = osCounter;
    }

    public PairedCpuBinding Binding { get; }
    public Guid QueryId { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTicks { get; }
    public long MonotonicFrequency { get; }
    public PairedCpuHostEvidence Host { get; }
    public long? SqlProcessCreationFileTime100ns { get; }
    /// <summary>Observed active logical processors across ALL_PROCESSOR_GROUPS,
    /// not the collector's environment count, SQL scheduler count, or affinity count.</summary>
    public uint? LogicalProcessorCount { get; }
    public PdhCpuRawCounter? ProcessCounter { get; }
    public PdhCpuRawCounter? OsCounter { get; }
}

public sealed record PairedCpuComponent(decimal? Percent, PairedCpuGapReason Reason,
    long? FromNative100ns = null, long? ToNative100ns = null)
{
    public bool Available => Percent is not null && Reason == PairedCpuGapReason.None;
}

/// <summary>Numerical evidence for a candidate native process and its observed host.
/// Available arithmetic does not admit this candidate as the monitored SQL target.</summary>
public sealed record PairedCpuComparison(PairedCpuState State, PairedCpuGapReason Reason,
    PairedCpuComponent SqlProcess, PairedCpuComponent OperatingSystem)
{
    public bool NumericallyComparable => State == PairedCpuState.Available && Reason == PairedCpuGapReason.None;
    public PairedCpuNativeQualification NativeQualification { get; } = PairedCpuNativeQualification.Absent;
    public bool TargetComparisonAdmitted { get; }
    public bool HardwareIdentityVerified { get; }
}

/// <summary>Pure numerical qualification of two raw native collections, without
/// source admission. The actual PDH denominator is used; observed UTC, monotonic
/// time and cadence never replace it.</summary>
public static class PairedCpuQualifier
{
    public static PairedCpuComparison Qualify(PairedCpuEndpoint? before, PairedCpuEndpoint after,
        TimeSpan maximumClockSkew)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (maximumClockSkew < TimeSpan.Zero || maximumClockSkew > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(maximumClockSkew));
        if (before is null) return Missing(PairedCpuGapReason.Baseline);
        PairedCpuGapReason boundary = CommonBoundary(before, after, maximumClockSkew);
        if (boundary != PairedCpuGapReason.None) return Missing(boundary);

        PairedCpuComponent os = Counter(before.OsCounter, after.OsCounter,
            PdhCpuRawCounter.OsIdleInverseTimerType, 1, before, after, maximumClockSkew);
        PairedCpuGapReason processBoundary = ProcessBoundary(before, after);
        PairedCpuComponent process = processBoundary == PairedCpuGapReason.None
            ? Counter(before.ProcessCounter, after.ProcessCounter, PdhCpuRawCounter.ProcessTimerType,
                after.LogicalProcessorCount!.Value, before, after, maximumClockSkew)
            : new(null, processBoundary);
        PairedCpuGapReason reason = !process.Available ? process.Reason : !os.Available ? os.Reason :
            process.FromNative100ns != os.FromNative100ns || process.ToNative100ns != os.ToNative100ns
                ? PairedCpuGapReason.IntervalMismatch : PairedCpuGapReason.None;
        return new(reason == PairedCpuGapReason.None ? PairedCpuState.Available :
            process.Available || os.Available ? PairedCpuState.Partial : PairedCpuState.Unavailable,
            reason, process, os);
    }

    private static PairedCpuComparison Missing(PairedCpuGapReason reason) =>
        new(PairedCpuState.Unavailable, reason, new(null, reason), new(null, reason));

    private static PairedCpuGapReason CommonBoundary(PairedCpuEndpoint before, PairedCpuEndpoint after, TimeSpan skew)
    {
        if (before.Binding.TargetId != after.Binding.TargetId) return PairedCpuGapReason.TargetBoundary;
        if (before.Binding.TargetRevision != after.Binding.TargetRevision) return PairedCpuGapReason.RevisionBoundary;
        if (before.Binding.RunId != after.Binding.RunId) return PairedCpuGapReason.RunBoundary;
        if (before.QueryId != after.QueryId) return PairedCpuGapReason.QueryBoundary;
        if (before.Host.Witness != PairedCpuHostWitness.NativeMachineAndBoot ||
            after.Host.Witness != PairedCpuHostWitness.NativeMachineAndBoot ||
            before.Binding.ExpectedHostFingerprint is null || after.Binding.ExpectedHostFingerprint is null ||
            before.Host.ActualHostFingerprint is null || after.Host.ActualHostFingerprint is null ||
            before.Host.MachineFingerprint is null || after.Host.MachineFingerprint is null ||
            before.Host.BootFileTime100ns is null || after.Host.BootFileTime100ns is null ||
            before.LogicalProcessorCount is null || after.LogicalProcessorCount is null)
            return PairedCpuGapReason.SourceQualificationAbsent;
        if (before.Host.ActualHostFingerprint != before.Binding.ExpectedHostFingerprint ||
            after.Host.ActualHostFingerprint != after.Binding.ExpectedHostFingerprint ||
            before.Binding.ExpectedHostFingerprint != after.Binding.ExpectedHostFingerprint)
            return PairedCpuGapReason.HostBindingMismatch;
        if (before.Host.MachineFingerprint != after.Host.MachineFingerprint) return PairedCpuGapReason.MachineBoundary;
        if (before.Host.BootFileTime100ns != after.Host.BootFileTime100ns) return PairedCpuGapReason.BootBoundary;
        if (before.LogicalProcessorCount != after.LogicalProcessorCount) return PairedCpuGapReason.ProcessorTopologyChanged;
        if (before.MonotonicFrequency != after.MonotonicFrequency || after.MonotonicTicks <= before.MonotonicTicks ||
            after.ObservedAtUtc <= before.ObservedAtUtc) return PairedCpuGapReason.ClockBoundary;
        decimal monotonic = MonotonicInterval(before, after);
        if (Math.Abs((decimal)(after.ObservedAtUtc - before.ObservedAtUtc).Ticks - monotonic) > skew.Ticks)
            return PairedCpuGapReason.ClockSkew;
        return PairedCpuGapReason.None;
    }

    private static PairedCpuGapReason ProcessBoundary(PairedCpuEndpoint before, PairedCpuEndpoint after)
    {
        if (before.Binding.SqlProcessWitness != PairedCpuSqlProcessWitness.TargetEngineProcessId ||
            after.Binding.SqlProcessWitness != PairedCpuSqlProcessWitness.TargetEngineProcessId ||
            before.Binding.SqlProcessId is null || after.Binding.SqlProcessId is null ||
            before.SqlProcessCreationFileTime100ns is null || after.SqlProcessCreationFileTime100ns is null)
            return PairedCpuGapReason.SourceQualificationAbsent;
        if (before.Binding.SqlProcessId != after.Binding.SqlProcessId ||
            before.SqlProcessCreationFileTime100ns != after.SqlProcessCreationFileTime100ns)
            return PairedCpuGapReason.ProcessRestart;
        return PairedCpuGapReason.None;
    }

    private static decimal MonotonicInterval(PairedCpuEndpoint before, PairedCpuEndpoint after) =>
        ((decimal)after.MonotonicTicks - before.MonotonicTicks) / after.MonotonicFrequency * TimeSpan.TicksPerSecond;

    private static PairedCpuComponent Counter(PdhCpuRawCounter? before, PdhCpuRawCounter? after,
        uint expectedType, uint processors, PairedCpuEndpoint beforeEndpoint, PairedCpuEndpoint afterEndpoint, TimeSpan skew)
    {
        if (before is null || after is null || before.ApiStatus != 0 || after.ApiStatus != 0)
            return new(null, PairedCpuGapReason.SourceUnavailable);
        if (before.CStatus is not (PdhCpuRawCounter.ValidData or PdhCpuRawCounter.NewData) ||
            after.CStatus is not (PdhCpuRawCounter.ValidData or PdhCpuRawCounter.NewData))
            return new(null, PairedCpuGapReason.CounterStatusInvalid);
        if (before.CounterType != expectedType || after.CounterType != expectedType)
            return new(null, PairedCpuGapReason.CounterTypeMismatch);
        if (before.TimeBase != PdhCpuRawCounter.HundredNanosecondTimeBase || after.TimeBase != before.TimeBase)
            return new(null, PairedCpuGapReason.CounterTimeBaseMismatch);
        if (before.FirstValue < 0 || after.FirstValue < before.FirstValue ||
            before.CollectedLocalFileTime100ns <= 0 || after.CollectedLocalFileTime100ns < before.CollectedLocalFileTime100ns)
            return new(null, PairedCpuGapReason.CounterReset);
        if (before.SecondValue <= 0 || after.SecondValue <= before.SecondValue)
            return new(null, PairedCpuGapReason.DenominatorInvalid);
        decimal interval = (decimal)after.SecondValue - before.SecondValue;
        if (Math.Abs(interval - MonotonicInterval(beforeEndpoint, afterEndpoint)) > skew.Ticks)
            return new(null, PairedCpuGapReason.ClockSkew);
        decimal delta = (decimal)after.FirstValue - before.FirstValue;
        decimal limit = interval * processors;
        if (delta > limit) return new(null, PairedCpuGapReason.CounterRangeInvalid);
        decimal percent = expectedType == PdhCpuRawCounter.OsIdleInverseTimerType
            ? 100m * (1m - delta / interval) : 100m * delta / limit;
        return new(percent, PairedCpuGapReason.None, before.SecondValue, after.SecondValue);
    }
}
