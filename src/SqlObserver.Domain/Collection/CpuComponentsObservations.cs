using System.Text.Json;
using System.Text.Json.Serialization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum CpuComponentAssociationState { Unavailable, Verified }
public enum CpuComponentAssociationReason
{
    None, HostBindingUnavailable, HostBindingDenied, HostBindingBoundary,
    HostCpuCapabilityUnavailable, NativeCaptureIncomplete, NativeAssociationRejected, MonitorIdentityUnavailable, NativeProcessAccessDenied,
}
public enum CpuComponentQuality { Unavailable, Measured, ProviderAdjusted }
public enum CpuComponentReason
{
    None, AssociationUnavailable, SourceUnavailable, CounterStatusInvalid, CounterTypeMismatch,
    CounterTimeBaseMismatch, CounterReset, DenominatorInvalid, ClockBoundary,
    ProviderFailure, ProviderOutOfRange, ProviderAdjusted, RawFormulaOutOfRange,
}

/// <summary>Native provider calculation on the exact saved raw pair, before any processor normalization.</summary>
public sealed record CpuComponentProviderCalculation(uint ApiStatus, uint CStatus, decimal? Percent);

/// <summary>One independently timed CPU component. Raw counters are private repository evidence;
/// public projections must explicitly select sanitized fields rather than serialize this type.</summary>
public sealed record CpuComponentValue(decimal? Percent, CpuComponentQuality Quality, CpuComponentReason Reason,
    long? FromNative100ns, long? ToNative100ns, DateTimeOffset? ObservedFromUtc, DateTimeOffset? ObservedToUtc,
    decimal? RawFormulaPercent, bool RawFormulaEligible, PairedCpuGapReason RawFormulaReason,
    decimal? ProviderRawPercent, uint? ProviderApiStatus, uint? ProviderCStatus,
    uint? LogicalProcessorDivisor, PdhCpuRawCounter? RawBefore = null, PdhCpuRawCounter? RawAfter = null)
{
    public const string CalculationMethod = "pdh_calculate_exact_raw_pair";
    public string Method { get; } = CalculationMethod;
    public bool Available => Percent is not null && Quality != CpuComponentQuality.Unavailable;

    public void Validate()
    {
        if (!Enum.IsDefined(Quality) || !Enum.IsDefined(Reason) || !Enum.IsDefined(RawFormulaReason) ||
            Percent is < 0 or > 100 || LogicalProcessorDivisor is 0 or > 4096 ||
            (FromNative100ns is null) != (ToNative100ns is null) ||
            (ObservedFromUtc is null) != (ObservedToUtc is null) ||
            ObservedFromUtc?.Offset != null && ObservedFromUtc.Value.Offset != TimeSpan.Zero ||
            ObservedToUtc?.Offset != null && ObservedToUtc.Value.Offset != TimeSpan.Zero)
            throw new ArgumentException("CPU component contains invalid bounded evidence.");
        if (Available)
        {
            if (FromNative100ns is not > 0 || ToNative100ns <= FromNative100ns ||
                ObservedFromUtc is null || ObservedToUtc <= ObservedFromUtc || LogicalProcessorDivisor is null ||
                ProviderApiStatus != 0 || ProviderCStatus is not (0 or 1) || ProviderRawPercent is null ||
                Percent != ProviderRawPercent / LogicalProcessorDivisor || RawBefore is null || RawAfter is null ||
                FromNative100ns != RawBefore.SecondValue || ToNative100ns != RawAfter.SecondValue ||
                Quality == CpuComponentQuality.Measured && (Reason != CpuComponentReason.None || !RawFormulaEligible) ||
                Quality == CpuComponentQuality.ProviderAdjusted &&
                (Reason != CpuComponentReason.ProviderAdjusted || RawFormulaEligible || RawFormulaPercent is not < 0 ||
                 Percent != 0 || ProviderRawPercent != 0 || LogicalProcessorDivisor != 1 ||
                 RawFormulaReason != PairedCpuGapReason.CounterRangeInvalid ||
                 RawBefore.CounterType != PdhCpuRawCounter.OsIdleInverseTimerType || RawAfter.CounterType != PdhCpuRawCounter.OsIdleInverseTimerType))
                throw new ArgumentException("An available CPU component requires its exact provider and interval evidence.");
            bool os = RawBefore!.CounterType == PdhCpuRawCounter.OsIdleInverseTimerType;
            if (RawBefore.CounterType is not (PdhCpuRawCounter.ProcessTimerType or PdhCpuRawCounter.OsIdleInverseTimerType) ||
                RawAfter!.CounterType != RawBefore.CounterType || os && LogicalProcessorDivisor != 1 ||
                RawBefore.ApiStatus != 0 || RawAfter.ApiStatus != 0 || RawBefore.CStatus is not (0 or 1) || RawAfter.CStatus is not (0 or 1) ||
                RawBefore.TimeBase != PdhCpuRawCounter.HundredNanosecondTimeBase || RawAfter.TimeBase != RawBefore.TimeBase ||
                RawBefore.FirstValue < 0 || RawAfter.FirstValue < RawBefore.FirstValue ||
                RawBefore.CollectedLocalFileTime100ns <= 0 || RawAfter.CollectedLocalFileTime100ns < RawBefore.CollectedLocalFileTime100ns)
                throw new ArgumentException("CPU component raw status, counter type, timebase or monotonicity is invalid.");
            decimal delta = (decimal)RawAfter.FirstValue - RawBefore.FirstValue;
            decimal interval = (decimal)RawAfter.SecondValue - RawBefore.SecondValue;
            decimal formula = os ? 100m * (1m - delta / interval) : 100m * delta / interval / LogicalProcessorDivisor!.Value;
            bool inRange = formula is >= 0 and <= 100;
            if (formula != RawFormulaPercent || RawFormulaEligible != inRange ||
                RawFormulaReason != (inRange ? PairedCpuGapReason.None : PairedCpuGapReason.CounterRangeInvalid))
                throw new ArgumentException("CPU raw formula and eligibility do not match the retained counters.");
        }
        else if (Percent is not null || Quality != CpuComponentQuality.Unavailable || Reason == CpuComponentReason.None)
            throw new ArgumentException("Unavailable CPU components cannot publish percentages.");
        if (RawFormulaEligible && (RawFormulaReason != PairedCpuGapReason.None || RawFormulaPercent is null))
            throw new ArgumentException("CPU raw eligibility requires valid arithmetic evidence.");
    }

    public static CpuComponentValue Unavailable(CpuComponentReason reason = CpuComponentReason.AssociationUnavailable) =>
        new(null, CpuComponentQuality.Unavailable, reason, null, null, null, null,
            null, false, PairedCpuGapReason.SourceQualificationAbsent, null, null, null, null);
}

/// <summary>Source-owned, attested, co-observed CPU components. Equal native intervals
/// are a separate fact, never inferred from a shared query or display window.</summary>
public sealed record CpuComponentsObservation
{
    public const int CurrentSourceSchemaVersion = 1;
    public const int MaximumCaptureBytes = 65_536;
    public const int MaximumSourceFrames = 6;
    private static readonly JsonSerializerOptions SizeOptions = new(JsonSerializerDefaults.Web);

    public CpuComponentsObservation(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        CollectorRunId runId, long? hostBindingRevision, long? hostProfileRevision, DateTimeOffset observedAtUtc,
        CpuComponentAssociationState associationState, CpuComponentAssociationReason associationReason,
        CpuTcpAssociationGap nativeAssociationReason, CpuComponentValue sqlProcess, CpuComponentValue operatingSystem,
        bool exactPairEligible, CpuPairedCaptureByteAccounting bytes, int sourceFrames)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(runId); ArgumentNullException.ThrowIfNull(sqlProcess);
        ArgumentNullException.ThrowIfNull(operatingSystem); ArgumentNullException.ThrowIfNull(bytes);
        if (!Enum.IsDefined(associationState) || !Enum.IsDefined(associationReason) || !Enum.IsDefined(nativeAssociationReason) ||
            hostBindingRevision is <= 0 || hostProfileRevision is <= 0 || hostBindingRevision != hostProfileRevision ||
            sourceFrames is < 0 or > MaximumSourceFrames ||
            bytes.TcpBeforeHelperBytes is < 0 or > CpuPairedCaptureObservation.MaximumHelperBytes ||
            bytes.CpuPairHelperBytes is < 0 or > CpuPairedCaptureObservation.MaximumHelperBytes ||
            bytes.TcpAfterHelperBytes is < 0 or > CpuPairedCaptureObservation.MaximumHelperBytes ||
            bytes.SqlLogicalResponseUpperBoundBytes is < 0 or > CpuPairedCaptureObservation.SqlLogicalResponseUpperBoundBytes)
            throw new ArgumentException("CPU components envelope is invalid.");
        sqlProcess.Validate(); operatingSystem.Validate();
        if (sqlProcess.Available && sqlProcess.RawBefore?.CounterType != PdhCpuRawCounter.ProcessTimerType ||
            operatingSystem.Available && operatingSystem.RawBefore?.CounterType != PdhCpuRawCounter.OsIdleInverseTimerType)
            throw new ArgumentException("CPU components cannot exchange process and operating-system counter identities.");
        bool verified = associationState == CpuComponentAssociationState.Verified;
        if (verified != (associationReason == CpuComponentAssociationReason.None) ||
            verified && (nativeAssociationReason != CpuTcpAssociationGap.None || hostBindingRevision is null || sourceFrames != MaximumSourceFrames) ||
            !verified && (sqlProcess.Available || operatingSystem.Available) ||
            exactPairEligible != (verified && sqlProcess.Available && operatingSystem.Available &&
                sqlProcess.RawFormulaEligible && operatingSystem.RawFormulaEligible &&
                sqlProcess.FromNative100ns == operatingSystem.FromNative100ns && sqlProcess.ToNative100ns == operatingSystem.ToNative100ns))
            throw new ArgumentException("CPU source association and exact-pair eligibility contradict their evidence.");
        TargetId = targetId; TargetRevision = targetRevision; RunId = runId;
        HostBindingRevision = hostBindingRevision; HostProfileRevision = hostProfileRevision;
        ObservedAtUtc = DomainValidation.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        AssociationState = associationState; AssociationReason = associationReason; NativeAssociationReason = nativeAssociationReason;
        SqlProcess = sqlProcess; OperatingSystem = operatingSystem; ExactPairEligible = exactPairEligible;
        Bytes = bytes; SourceFrames = sourceFrames;
        if (EstimatedSizeBytes > MaximumCaptureBytes) throw new ArgumentException("CPU components exceed their bounded envelope.");
    }
    public int SourceSchemaVersion { get; } = CurrentSourceSchemaVersion;
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public CollectorRunId RunId { get; }
    public long? HostBindingRevision { get; }
    public long? HostProfileRevision { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public CpuComponentAssociationState AssociationState { get; }
    public CpuComponentAssociationReason AssociationReason { get; }
    public CpuTcpAssociationGap NativeAssociationReason { get; }
    public CpuComponentValue SqlProcess { get; }
    public CpuComponentValue OperatingSystem { get; }
    public bool ExactPairEligible { get; }
    public CpuPairedCaptureByteAccounting Bytes { get; }
    public int SourceFrames { get; }
    [JsonIgnore] public int EstimatedSizeBytes => JsonSerializer.SerializeToUtf8Bytes(this, SizeOptions).Length;
}
