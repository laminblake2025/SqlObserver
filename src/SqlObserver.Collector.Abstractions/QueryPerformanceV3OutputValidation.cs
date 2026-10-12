using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

public static class QueryPerformanceV3OutputValidation
{
    public const int MaximumItems = 22_008;
    public const int MaximumSourceRows = QueryPerformanceV2OutputValidation.MaximumSourceRows + PlanCacheContentOutputBatch.MaximumEntries;
    public const int MaximumResponseBytes = QueryPerformanceV2OutputValidation.MaximumResponseBytes;
    public static void Validate(CollectorPayload payload, MonitoredInstanceId targetId, ObservationTargetRevision revision,
        CollectorRunOutcome outcome, CollectorRunAccounting accounting, CollectorLossEvidence loss) =>
        QueryPerformanceV2OutputValidation.Validate(payload, targetId, revision, outcome, accounting, loss, protocolVersion: 3);
}
