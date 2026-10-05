using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

public static class CpuComponentsOutputValidation
{
    public const int MaximumResponseBytes = CpuPairedCaptureOutputValidation.MaximumResponseBytes;
    public static long AccountedResponseBytes(CpuComponentsObservation value) => checked(
        (long)(value.Bytes.TcpBeforeHelperBytes ?? 0) + (value.Bytes.CpuPairHelperBytes ?? 0) +
        (value.Bytes.TcpAfterHelperBytes ?? 0) + value.Bytes.SqlLogicalResponseUpperBoundBytes);

    public static void Validate(CollectorPayload payload, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunId runId,
        CollectorRunOutcome outcome, CollectorRunAccounting accounting, CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CpuComponentsObservation? value = payload.CpuComponents;
        if (payload.ItemCount != (value is null ? 0 : 1) || payload.OperationalHealth is not null ||
            payload.CpuPairedCapture is not null || payload.HostMetricsContext is not null ||
            payload.QueryPerformanceStatuses.Count != 0 || payload.QueryPerformanceTargetStatus is not null ||
            payload.QueryStoreGroups is not null || payload.PlanCacheRuntime is not null || payload.PlanCacheContent is not null ||
            accounting.SourceRowsRead > CpuComponentsObservation.MaximumSourceFrames ||
            accounting.ResponseBytes > MaximumResponseBytes || accounting.OutputBytes > CpuComponentsObservation.MaximumCaptureBytes ||
            loss.HasLoss && (outcome != CollectorRunOutcome.OutputInvalid || loss.Kind != CollectorLossKind.OutputValidationFailure))
            throw new InvalidDataException("CPU components violate their isolated bounded output contract.");
        if (outcome == CollectorRunOutcome.Succeeded)
        {
            if (value is null || value.TargetId != targetId || value.TargetRevision != targetRevision || value.RunId != runId ||
                accounting.SourceRowsRead != value.SourceFrames || accounting.OutputItemsProduced != 1 ||
                accounting.ResponseBytes != AccountedResponseBytes(value) || accounting.OutputBytes != value.EstimatedSizeBytes)
                throw new InvalidDataException("CPU components identity or accounting is invalid.");
            value.SqlProcess.Validate(); value.OperatingSystem.Validate();
        }
        else if (value is not null || accounting.OutputItemsProduced != 0 || accounting.OutputBytes != 0 || outcome == CollectorRunOutcome.Partial)
            throw new InvalidDataException("Failed CPU collection cannot publish usable component output.");
    }
}
