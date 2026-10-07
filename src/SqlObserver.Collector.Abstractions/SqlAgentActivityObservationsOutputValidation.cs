using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

/// <summary>Closed bounded observations; retained source completeness never admits a live Agent metric.</summary>
public static class SqlAgentActivityObservationsOutputValidation
{
    public const int MaximumResponseBytes = 524288;
    public const int MaximumCaptureBytes = 524288;

    // Conservative logical accounting of fixed typed source fields, not measured TDS bytes.
    public static int AccountedResponseBytes(SqlAgentModuleCaptureResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked(4096 + value.Capture.SourceRowsRead * 768);
    }

    public static int EstimatedSizeBytes(SqlAgentModuleCaptureResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked(4096 + value.Capture.Jobs.Count * 768);
    }

    public static void Validate(CollectorPayload payload, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunId runId,
        CollectorRunOutcome outcome, CollectorRunAccounting accounting, CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(loss);
        SqlAgentModuleCaptureResult? value = payload.SqlAgentModuleCapture;
        if (payload.ItemCount != (value is null ? 0 : 1) || payload.OperationalHealth is not null ||
            payload.HostMetricsContext is not null || payload.CpuPairedCapture is not null || payload.CpuComponents is not null ||
            payload.QueryPerformanceStatuses.Count != 0 || payload.QueryPerformanceTargetStatus is not null ||
            payload.QueryStoreGroups is not null || payload.PlanCacheRuntime is not null || payload.PlanCacheContent is not null ||
            accounting.SourceRowsRead > SqlAgentActivityCapture.MaximumSourceRows || accounting.ResponseBytes > MaximumResponseBytes ||
            accounting.OutputBytes > MaximumCaptureBytes)
            throw new InvalidDataException("Agent observations violate their isolated bounded output contract.");

        if (outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
        {
            if (value is null || value.Capture.TargetId != targetId || value.Capture.TargetRevision != targetRevision ||
                value.Capture.RunId != runId || value.NativeQualification != SqlAgentActivityNativeQualification.Absent ||
                value.ProductionAdmitted || value.ServiceSessionAssociationQualified ||
                accounting.SourceRowsRead != value.Capture.SourceRowsRead || accounting.OutputItemsProduced != 1 ||
                accounting.ResponseBytes != AccountedResponseBytes(value) || accounting.OutputBytes != EstimatedSizeBytes(value) ||
                payload.EstimatedSizeBytes != EstimatedSizeBytes(value) ||
                value.Capture.Truncated != (outcome == CollectorRunOutcome.Partial) ||
                (value.Capture.Truncated
                    ? loss.Kind != CollectorLossKind.SourceRowLimit || loss.MinimumLostItems != 1 || loss.CountIsExact || loss.MinimumLostBytes != 0
                    : loss.HasLoss))
                throw new InvalidDataException("Agent observations identity, source accounting or loss evidence is invalid.");
        }
        else if (value is not null || accounting.SourceRowsRead != 0 || accounting.ResponseBytes != 0 ||
            accounting.OutputItemsProduced != 0 || accounting.OutputBytes != 0 ||
            (outcome == CollectorRunOutcome.OutputInvalid
                ? loss.Kind != CollectorLossKind.OutputValidationFailure || loss.MinimumLostItems != 1 || loss.CountIsExact || loss.MinimumLostBytes != 0
                : loss.HasLoss))
            throw new InvalidDataException("Failed Agent collection cannot publish retained observations.");
    }
}
