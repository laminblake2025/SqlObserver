using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

/// <summary>Closed staged capture contract; completion never admits a CPU comparison.</summary>
public static class CpuPairedCaptureOutputValidation
{
    public const int MaximumResponseBytes = 3 * CpuPairedCaptureObservation.MaximumHelperBytes +
        (int)CpuPairedCaptureObservation.SqlLogicalResponseUpperBoundBytes;

    // Composite source accounting: exact received helper UTF-8 lengths plus the
    // explicitly declared SQL logical upper bound. This is not measured TDS bytes.
    public static long AccountedResponseBytes(CpuPairedCaptureObservation value) => checked(
        (long)(value.Bytes.TcpBeforeHelperBytes ?? 0) + (value.Bytes.CpuPairHelperBytes ?? 0) +
        (value.Bytes.TcpAfterHelperBytes ?? 0) + value.Bytes.SqlLogicalResponseUpperBoundBytes);

    public static void Validate(CollectorPayload payload, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunId runId,
        CollectorRunOutcome outcome, CollectorRunAccounting accounting, CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CpuPairedCaptureObservation? capture = payload.CpuPairedCapture;
        if (payload.ItemCount != (capture is null ? 0 : 1) || payload.OperationalHealth is not null ||
            payload.HostMetricsContext is not null || payload.QueryPerformanceStatuses.Count != 0 ||
            payload.QueryPerformanceTargetStatus is not null || payload.QueryStoreGroups is not null ||
            payload.PlanCacheRuntime is not null || payload.PlanCacheContent is not null ||
            accounting.SourceRowsRead > CpuPairedCaptureObservation.MaximumSourceFrames ||
            accounting.ResponseBytes > MaximumResponseBytes ||
            accounting.OutputBytes > CpuPairedCaptureObservation.MaximumCaptureBytes ||
            loss.HasLoss && (outcome != CollectorRunOutcome.OutputInvalid ||
                loss.Kind != CollectorLossKind.OutputValidationFailure))
            throw new InvalidDataException("CPU capture output violates its isolated bounded contract.");
        if (outcome == CollectorRunOutcome.Succeeded)
        {
            if (capture is null || capture.TargetId != targetId || capture.TargetRevision != targetRevision ||
                capture.RunId != runId || capture.NativeQualification != PairedCpuNativeQualification.Absent ||
                capture.TargetComparisonAdmitted || capture.HardwareIdentityVerified ||
                capture.SqlProcessPercent is not null || capture.OperatingSystemPercent is not null ||
                accounting.SourceRowsRead != capture.SourceFrames || accounting.OutputItemsProduced != 1 ||
                accounting.ResponseBytes != AccountedResponseBytes(capture) ||
                accounting.OutputBytes != capture.EstimatedSizeBytes)
                throw new InvalidDataException("CPU capture identity, admission or accounting is invalid.");
        }
        else if (capture is not null || accounting.OutputItemsProduced != 0 || accounting.OutputBytes != 0 ||
            outcome == CollectorRunOutcome.Partial)
            throw new InvalidDataException("A failed CPU capture cannot carry usable output or invented partial coverage.");
    }
}
