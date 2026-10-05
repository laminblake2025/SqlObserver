namespace SqlObserver.Domain.Collection;

public sealed record CpuComponentWindowHistoryCoverage(int RunCount, int RetainedReceiptCount,
    int CompleteRunCount, int GapRunCount, int FailedRunCount, int LostRunCount,
    int MissingPayloadRunCount, bool HasVisibilityGap);
public sealed record CpuComponentWindowHistoryValue(decimal? Percent, decimal? RawPercent,
    string Quality, string Reason, string? FromNative100ns, string? ToNative100ns,
    string? ObservedFromUtc, string? ObservedToUtc, uint? NormalizationLogicalProcessors,
    string Method, uint? ProviderApiStatus, uint? ProviderCStatus, bool RawFormulaEligible,
    string RawFormulaReason, decimal? ProviderRawPercent);
public sealed record CpuComponentWindowHistoryObservation(int SourceSchemaVersion, string State, string Reason,
    bool SourceAssociationVerified, string AssociationReason, bool ExactPairEligible,
    CpuComponentWindowHistoryValue SqlProcess, CpuComponentWindowHistoryValue OperatingSystem);
public sealed record CpuComponentWindowHistoryReceipt(Guid RunId, long SampleTargetRevision,
    DateTimeOffset CompletedAtUtc, string Outcome, string LossKind, int MinimumLostItems,
    bool ObservationPresent, string State, string Reason, CpuComponentWindowHistoryObservation? Observation);

/// <summary>Public co-observation semantics. An adjusted provider value never implies healthy raw arithmetic.</summary>
public static class CpuComponentWindowHistoryValidation
{
    public const int MaximumPageSize = CpuCaptureWindowHistoryValidation.MaximumPageSize;
    public static TimeSpan CursorLifetime => CpuCaptureWindowHistoryValidation.CursorLifetime;
    public static void Id(Guid value) => CpuCaptureWindowHistoryValidation.Id(value);
    public static void Revision(long value) => CpuCaptureWindowHistoryValidation.Revision(value);
    public static void Utc(DateTimeOffset value) => CpuCaptureWindowHistoryValidation.Utc(value);
    public static void Window(DateTimeOffset from, DateTimeOffset to) => CpuCaptureWindowHistoryValidation.Window(from, to);
    public static void Digest(string value) => CpuCaptureWindowHistoryValidation.Digest(value);
    public static void Coverage(CpuComponentWindowHistoryCoverage value)
    {
        ArgumentNullException.ThrowIfNull(value);
        CpuCaptureWindowHistoryValidation.Coverage(new(value.RunCount, value.RetainedReceiptCount,
            value.CompleteRunCount, value.GapRunCount, value.FailedRunCount, value.LostRunCount,
            value.MissingPayloadRunCount, value.HasVisibilityGap));
    }
    public static void Receipt(CpuComponentWindowHistoryReceipt value)
    {
        ArgumentNullException.ThrowIfNull(value); Id(value.RunId); Revision(value.SampleTargetRevision); Utc(value.CompletedAtUtc);
        if (value.Outcome is not ("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open") ||
            value.LossKind is not ("none" or "source_row_limit" or "response_byte_limit" or "output_validation_failure" or "visibility_incomplete") ||
            value.MinimumLostItems is < 0 or > 6 || value.LossKind == "none" && value.MinimumLostItems != 0 ||
            value.ObservationPresent != (value.Observation is not null)) throw new ArgumentException("Invalid CPU observation receipt.");
        if (value.Observation is { } observation)
        {
            Observation(observation);
            if (value.Outcome != "succeeded" || value.LossKind != "none" || value.State != observation.State || value.Reason != observation.Reason)
                throw new ArgumentException("CPU receipt disagrees with its observation.");
        }
        else if (value.State != "unavailable" || value.Reason != (value.Outcome == "succeeded" ? "payload_missing" : "collection_failed"))
            throw new ArgumentException("Missing CPU evidence was presented as measured.");
    }
    public static void Observation(CpuComponentWindowHistoryObservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Component(value.SqlProcess); Component(value.OperatingSystem);
        bool both = value.SqlProcess.Percent is not null && value.OperatingSystem.Percent is not null;
        bool any = value.SqlProcess.Percent is not null || value.OperatingSystem.Percent is not null;
        string state = both && value.SqlProcess.Quality == "available" && value.OperatingSystem.Quality == "available" ? "available" : any ? "partial" : "unavailable";
        if (value.SourceSchemaVersion != 1 || value.State != state ||
            value.Reason != (state == "available" ? "measured" : state == "partial" ? "component_quality" : "components_unavailable") ||
            !Enum.TryParse<CpuComponentAssociationReason>(value.AssociationReason, out var association) || !Enum.IsDefined(association) || association.ToString() != value.AssociationReason ||
            value.SourceAssociationVerified != (association == CpuComponentAssociationReason.None) ||
            any && !value.SourceAssociationVerified ||
            value.ExactPairEligible != (value.SourceAssociationVerified && both && value.SqlProcess.RawFormulaEligible && value.OperatingSystem.RawFormulaEligible &&
              value.SqlProcess.FromNative100ns == value.OperatingSystem.FromNative100ns && value.SqlProcess.ToNative100ns == value.OperatingSystem.ToNative100ns))
            throw new ArgumentException("CPU observation overstates its source evidence.");
    }
    public static void Component(CpuComponentWindowHistoryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Method != "pdh_calculate_exact_raw_pair" || value.Quality is not ("available" or "provider_adjusted" or "unavailable") ||
            value.Percent is < 0 or > 100 || value.NormalizationLogicalProcessors is 0 or > 4096 ||
            !Enum.TryParse<CpuComponentReason>(value.Reason, out var reason) || !Enum.IsDefined(reason) || reason.ToString() != value.Reason ||
            !Enum.TryParse<PairedCpuGapReason>(value.RawFormulaReason, out var rawReason) || !Enum.IsDefined(rawReason) || rawReason.ToString() != value.RawFormulaReason ||
            (value.Quality == "unavailable") != (value.Percent is null) || value.Quality == "unavailable" && value.Reason == "None" ||
            (value.FromNative100ns is null) != (value.ToNative100ns is null) || (value.ObservedFromUtc is null) != (value.ObservedToUtc is null)) throw new ArgumentException("Invalid CPU component quality.");
        if (value.Percent is not null && (value.FromNative100ns is null || value.ToNative100ns is null ||
            CpuCaptureWindowHistoryValidation.Counter(value.FromNative100ns) <= 0 ||
            CpuCaptureWindowHistoryValidation.Counter(value.ToNative100ns) <= CpuCaptureWindowHistoryValidation.Counter(value.FromNative100ns) ||
            value.ObservedFromUtc is null || value.ObservedToUtc is null || value.NormalizationLogicalProcessors is null or 0 ||
            value.ProviderApiStatus != 0 || value.ProviderCStatus is not (0 or 1) || value.ProviderRawPercent is null ||
            value.Percent != value.ProviderRawPercent / value.NormalizationLogicalProcessors ||
            value.Quality == "available" && (!value.RawFormulaEligible || value.Reason != "None") ||
            value.Quality == "provider_adjusted" && (value.Reason != "ProviderAdjusted" || value.RawFormulaEligible || value.RawPercent is not < 0 ||
                value.Percent != 0 || value.ProviderRawPercent != 0 || value.NormalizationLogicalProcessors != 1 || value.RawFormulaReason != "CounterRangeInvalid"))) throw new ArgumentException("A measured CPU value lacks valid source evidence.");
        if (value.RawFormulaEligible && (value.RawFormulaReason != "None" || value.RawPercent is null or < 0 or > 100))
            throw new ArgumentException("CPU raw eligibility overstates arithmetic.");
        if (value.FromNative100ns is { } nativeFrom) CpuCaptureWindowHistoryValidation.Counter(nativeFrom,true);
        if (value.ToNative100ns is { } nativeTo) CpuCaptureWindowHistoryValidation.Counter(nativeTo,true);
        if (value.ObservedFromUtc is { } from) CpuCaptureWindowHistoryValidation.SourceUtc(from);
        if (value.ObservedToUtc is { } to) CpuCaptureWindowHistoryValidation.SourceUtc(to);
        if (value.Percent is not null && value.ObservedFromUtc is { } a && value.ObservedToUtc is { } b && string.CompareOrdinal(a,b) >= 0)
            throw new ArgumentException("CPU observation bracket is not forward.");
    }
}
