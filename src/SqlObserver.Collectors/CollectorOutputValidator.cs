using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using System.Text;

namespace SqlObserver.Collectors;

/// <summary>Validates all adapter output against the immutable registration before repository I/O.</summary>
public sealed class CollectorOutputValidator : ICollectorOutputValidator
{
    private readonly Dictionary<string, CollectorMetricOutputContract> _metrics;

    public CollectorOutputValidator(CollectorOutputContract contract)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _metrics = contract.Metrics.ToDictionary(static metric => metric.MetricId.Value, StringComparer.Ordinal);
    }

    public CollectorOutputContract Contract { get; }

    public void Validate(
        CollectorManifest manifest,
        CollectorExecutionRequest request,
        CollectorExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (result.TargetId != request.TargetId ||
            result.TargetRevision != request.TargetRevision ||
            result.CollectorId != manifest.Id ||
            result.CollectorManifestVersion != manifest.ManifestVersion.Value ||
            result.OutputSchemaVersion != manifest.OutputSchemaVersion.Value ||
            Contract.SchemaVersion.Value != manifest.OutputSchemaVersion.Value)
        {
            throw new InvalidDataException("Collector output identity or contract version is invalid.");
        }

        int sourceRowBound = manifest.Id.Value == "sql-agent.failures"
            ? OperationalHealthBounds.AgentScanRows
            : manifest.Id.Value == "availability-groups.health"
            ? OperationalHealthBounds.AvailabilityMaximumRows + 1
            : manifest.Id.Value == "replication.health"
                ? ReplicationBounds.MaximumRows + 1
                : manifest.Id.Value == "queries.performance"
            ? QueryPerformanceBounds.MaximumDatabases * QueryPerformanceBounds.ProbeRows
            : manifest.Limits.MaxRows;
        if (result.Accounting.SourceRowsRead > sourceRowBound ||
            result.Accounting.ResponseBytes > manifest.Limits.MaxResponseBytes ||
            result.Accounting.OutputBytes > manifest.Limits.MaxResponseBytes)
        {
            throw new InvalidDataException("Collector output exceeded a manifest execution bound.");
        }

        CollectorPayload payload = result.Payload;
        bool requiresCompleteMetricSet =
            Contract.MaxMetricSamples == _metrics.Count &&
            Contract.Metrics.All(static metric => metric.AllowedDimensionKeys.Count == 0);
        if (payload.Metrics.Count > Contract.MaxMetricSamples ||
            payload.Databases.Items.Count > Contract.MaxDatabaseObservations ||
            payload.DatabaseFiles.Items.Count > Contract.MaxDatabaseFileObservations ||
            payload.SqlVolumes.Items.Count > Contract.MaxSqlVolumeObservations ||
            payload.ActivitySessions.Items.Count > Contract.MaxActivitySessionObservations ||
            payload.ActivityRequests.Items.Count > Contract.MaxActivityRequestObservations ||
            payload.ServerWaits.Items.Count > Contract.MaxServerWaitObservations ||
            payload.BlockingEdges.Items.Count > Contract.MaxBlockingEdgeObservations ||
            payload.Deadlocks.Items.Count > Contract.MaxDeadlockObservations ||
            payload.QueryPerformance.Items.Count > Contract.MaxQueryPerformanceObservations ||
            (payload.OperationalHealth?.ItemCount ?? 0) > Contract.MaxOperationalHealthObservations)
        {
            throw new InvalidDataException("Collector output exceeded its registered output cardinality.");
        }

        var metricIdentities = new HashSet<(DateTimeOffset ObservedAtUtc, Guid SampleId)>();
        var metricSeries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var metric in payload.Metrics)
        {
            if (metric.InstanceId != request.TargetId ||
                !_metrics.TryGetValue(metric.MetricId.Value, out CollectorMetricOutputContract? metricContract) ||
                metric.Dimensions.Any(dimension =>
                    !metricContract.AllowedDimensionKeys.Contains(dimension.Key, StringComparer.Ordinal)) ||
                !metricIdentities.Add((metric.ObservedAtUtc, metric.SampleId.Value)) ||
                requiresCompleteMetricSet && !metricSeries.Add(CreateMetricSeriesIdentity(metric)))
            {
                throw new InvalidDataException("Collector metric output is outside its registered schema.");
            }
        }

        if (result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial &&
            requiresCompleteMetricSet &&
            metricSeries.Count != _metrics.Count)
        {
            throw new InvalidDataException("Collector metric output omitted a required metric series.");
        }

        if (payload.Databases.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.DatabaseFiles.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.SqlVolumes.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ActivitySessions.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ActivityRequests.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.ServerWaits.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.BlockingEdges.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.Deadlocks.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.QueryPerformance.Items.Any(item =>
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision))
        {
            throw new InvalidDataException("Collector inventory output belongs to a different target revision.");
        }

        if (manifest.Id.Value is "backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health" or "replication.health")
        {
            ValidateOperationalHealthPayload(manifest.Id.Value, payload.OperationalHealth, request);
        }
        else if (payload.OperationalHealth is not null)
        {
            throw new InvalidDataException("Operational-health output is only valid for a declared operational-health collector.");
        }

        if (result.Outcome == CollectorRunOutcome.Succeeded && result.Loss.HasLoss ||
            result.Outcome == CollectorRunOutcome.Partial && !result.Loss.HasLoss)
        {
            throw new InvalidDataException("Collector outcome does not expose sample loss consistently.");
        }

        // VisibilityIncomplete is a deliberately narrow topology exception: it
        // is an explicit no-row-loss marker for a degraded observation, not a
        // generic way for another collector to manufacture Partial output.
        if (result.Loss.Kind == CollectorLossKind.VisibilityIncomplete &&
            (manifest.Id.Value is not ("availability-groups.health" or "replication.health") ||
             result.Outcome != CollectorRunOutcome.Partial ||
             result.Reason != CollectorRunReason.VisibilityIncomplete ||
             payload.OperationalHealth?.Snapshot is not (AvailabilityGroupsSnapshot { State: OperationalObservationState.Degraded } or ReplicationHealthSnapshot { State: OperationalObservationState.Degraded })))
        {
            throw new InvalidDataException("VisibilityIncomplete is reserved for a degraded availability-groups observation.");
        }

        if (manifest.Id.Value == "queries.performance")
        {
            ValidateQueryPerformanceBoundsAndStatuses(payload, result);
        }
    }

    private static void ValidateOperationalHealthPayload(string collectorId, OperationalHealthPayload? envelope, CollectorExecutionRequest request)
    {
        if (envelope is null) return; // Unsupported/permission outcomes carry an empty payload.
        if (collectorId == "backups.status" && envelope.Snapshot is BackupStatusSnapshot backups && backups.TargetId == request.TargetId && backups.TargetRevision == request.TargetRevision && backups.Items.Count <= OperationalHealthBounds.BackupMaximumRows) return;
        if (collectorId == "sql-agent.failures" && envelope.Snapshot is SqlAgentFailureSnapshot agent && agent.TargetId == request.TargetId && agent.TargetRevision == request.TargetRevision && agent.Items.Count <= OperationalHealthBounds.AgentMaximumRows && agent.SourceRowsRead is >= 0 and <= OperationalHealthBounds.AgentScanRows) return;
        if (collectorId == "tempdb.health" && envelope.Snapshot is TempDbSnapshot tempdb && tempdb.TargetId == request.TargetId && tempdb.TargetRevision == request.TargetRevision && tempdb.Files.Count <= OperationalHealthBounds.TempDbMaximumFiles) return;
        if (collectorId == "availability-groups.health" && envelope.Snapshot is AvailabilityGroupsSnapshot groups && groups.TargetId == request.TargetId && groups.TargetRevision == request.TargetRevision && groups.Replicas.Count + groups.Databases.Count <= OperationalHealthBounds.AvailabilityMaximumRows) return;
        if (collectorId == "replication.health" && envelope.Snapshot is ReplicationHealthSnapshot replication && replication.TargetId == request.TargetId && replication.TargetRevision == request.TargetRevision && replication.Items.Count <= ReplicationBounds.MaximumRows && replication.SourceRowsRead is >= 0 and <= ReplicationBounds.MaximumRows + 1) return;
        throw new InvalidDataException("Operational-health output kind, target revision, or bounds are invalid.");
    }

    private static void ValidateQueryPerformanceBoundsAndStatuses(CollectorPayload payload, CollectorExecutionResult result)
    {
        IReadOnlyList<QueryPerformanceDatabaseStatus> statuses = payload.QueryPerformanceStatuses;
        if (statuses.Count > QueryPerformanceBounds.MaximumDatabases ||
            statuses.Select(static status => status.DatabaseId).Distinct().Count() != statuses.Count ||
            statuses.Sum(static status => status.SourceRowsRead) > QueryPerformanceBounds.MaximumDatabases * QueryPerformanceBounds.ProbeRows)
        {
            throw new InvalidDataException("Query performance database status accounting exceeded its bounded contract.");
        }

        var observationsByDatabase = payload.QueryPerformance.Items
            .GroupBy(static observation => observation.Query.DatabaseId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var statusByDatabase = statuses.ToDictionary(static status => status.DatabaseId);
        foreach (QueryPerformanceObservation observation in payload.QueryPerformance.Items)
        {
            if (!statusByDatabase.ContainsKey(observation.Query.DatabaseId))
            {
                throw new InvalidDataException("Every query performance observation must have one database source status.");
            }
        }

        foreach (QueryPerformanceDatabaseStatus status in statuses)
        {
            observationsByDatabase.TryGetValue(status.DatabaseId, out QueryPerformanceObservation[]? observations);
            int count = observations?.Length ?? 0;
            bool rowStatus = status.Status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.PlanCacheRows;
            if (rowStatus != (count > 0))
            {
                throw new InvalidDataException("Query performance source status and observations are not bidirectionally consistent.");
            }

            if (status.Status == QueryPerformanceReadStatus.QueryStoreRows &&
                observations!.Any(static item => item.Source != QueryPerformanceSource.QueryStore || item.SourceState is not (QueryStoreState.ReadWrite or QueryStoreState.ReadOnly)))
            {
                throw new InvalidDataException("Query Store row status contains a non-Query Store observation.");
            }

            if (status.Status == QueryPerformanceReadStatus.PlanCacheRows &&
                observations!.Any(static item => item.Source != QueryPerformanceSource.PlanCache))
            {
                throw new InvalidDataException("Plan-cache row status contains a non-plan-cache observation.");
            }
        }

        if (payload.QueryPerformance.Items.Count > QueryPerformanceObservationBatch.MaximumItems ||
            result.Accounting.OutputItemsProduced < payload.QueryPerformance.Items.Count)
        {
            throw new InvalidDataException("Query performance emitted observation accounting is invalid.");
        }
    }

    private static string CreateMetricSeriesIdentity(MetricSample metric)
    {
        var builder = new StringBuilder(metric.MetricId.Value.Length + 32);
        AppendIdentityComponent(builder, metric.MetricId.Value);
        foreach (MetricDimension dimension in metric.Dimensions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            AppendIdentityComponent(builder, dimension.Key);
            AppendIdentityComponent(builder, dimension.Value);
        }

        return builder.ToString();
    }

    private static void AppendIdentityComponent(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);
}
