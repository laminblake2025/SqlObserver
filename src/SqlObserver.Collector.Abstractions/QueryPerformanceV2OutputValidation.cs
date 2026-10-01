using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Collector.Abstractions;

/// <summary>Shared output boundary for the versioned Query Store and selected cache protocol.</summary>
public static class QueryPerformanceV2OutputValidation
{
    public const int MaximumSourceRows = 514_769;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static void Validate(CollectorPayload payload, MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CollectorRunOutcome outcome,
        CollectorRunAccounting accounting, CollectorLossEvidence loss)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(loss);
        if (payload.Metrics.Count != 0 || payload.Databases.Items.Count != 0 ||
            payload.DatabaseFiles.Items.Count != 0 || payload.SqlVolumes.Items.Count != 0 ||
            payload.ActivitySessions.Items.Count != 0 || payload.ActivityRequests.Items.Count != 0 ||
            payload.ServerWaits.Items.Count != 0 || payload.BlockingEdges.Items.Count != 0 ||
            payload.Deadlocks.Items.Count != 0 || payload.ServerConfigurations.Items.Count != 0 ||
            payload.QueryStoreGroups is not null || payload.OperationalHealth is not null ||
            payload.HostMetricsContext is not null ||
            payload.QueryPerformance.Items.Any(static item => item.Source != QueryPerformanceSource.QueryStore ||
                item.Semantics != QueryMetricSemantics.QueryStoreInterval) ||
            payload.QueryPerformanceStatuses.Any(static status => status.Status == QueryPerformanceReadStatus.PlanCacheRows))
            throw new InvalidDataException("Query performance v2 accepts only Query Store observations and selected cache tuples.");

        bool success = outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial;
        if (accounting.SourceRowsRead > MaximumSourceRows || accounting.ResponseBytes > MaximumResponseBytes ||
            accounting.OutputBytes > MaximumResponseBytes ||
            success && (accounting.OutputItemsProduced != payload.ItemCount ||
                accounting.OutputBytes != payload.EstimatedSizeBytes) ||
            !success && (payload.QueryPerformance.Items.Count != 0 || payload.PlanCacheRuntime is not null ||
                payload.QueryPerformanceStatuses.Count != 0))
            throw new InvalidDataException("Query performance v2 source or output accounting is invalid.");
        if (outcome == CollectorRunOutcome.OutputInvalid &&
            (loss.Kind != CollectorLossKind.OutputValidationFailure ||
             loss.MinimumLostItems < Math.Max(1, accounting.OutputItemsProduced) ||
             loss.MinimumLostItems > 22_000 || loss.MinimumLostBytes != accounting.OutputBytes))
            throw new InvalidDataException("Query performance v2 must preserve rejected output as bounded loss evidence.");
        if (!success && outcome != CollectorRunOutcome.OutputInvalid &&
            (accounting.OutputItemsProduced != 0 || accounting.OutputBytes != 0 || loss.HasLoss))
            throw new InvalidDataException("A failed query performance v2 run cannot publish output accounting.");

        PlanCacheRuntimeOutputBatch? cache = payload.PlanCacheRuntime;
        long minimumSourceRows = payload.QueryPerformanceStatuses.Sum(static status => (long)status.SourceRowsRead)
            + (cache?.CandidateRows ?? 0);
        if (accounting.SourceRowsRead < minimumSourceRows)
            throw new InvalidDataException("Query performance v2 omitted source rows from its accounting.");
        // The native reader attaches a wait snapshot once per plan. Repeated
        // sidecars would charge bytes twice but persist only one plan snapshot.
        if (payload.QueryPerformance.Items.Where(static item => item.WaitSnapshot is not null)
            .GroupBy(static item => (item.Query.DatabaseId, item.Query.QueryFingerprint, item.Plan!.PlanFingerprint))
            .Any(static group => group.Count() > 1))
            throw new InvalidDataException("Query performance v2 wait evidence must occur once per plan.");
        if (cache is null) return; // Query Store-only results remain supported.
        int minimumMissing = Math.Max(Math.Max(cache.CandidateRows - cache.CandidateLimit, 0),
            cache.UnresolvedRows + cache.InvisibleDatabaseRows + cache.UnqualifiedRows);
        if (cache.TargetId != targetId || cache.TargetRevision != targetRevision ||
            outcome == CollectorRunOutcome.Succeeded && !cache.QualifiedExhaustive ||
            !cache.QualifiedExhaustive && (outcome != CollectorRunOutcome.Partial || !loss.HasLoss) ||
            cache.CandidateRows > cache.CandidateLimit && loss.CountIsExact ||
            loss.MinimumLostItems < minimumMissing)
            throw new InvalidDataException("Query performance v2 cache scope or incomplete coverage is not accounted.");
    }
}
