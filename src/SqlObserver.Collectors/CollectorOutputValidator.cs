using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
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
        if (manifest.Id.Value == "queries.performance" &&
            (manifest.ManifestVersion.Value is not (1 or 2) ||
             manifest.OutputSchemaVersion.Value != manifest.ManifestVersion.Value ||
             manifest.OutputKind != CollectorOutputKind.QueryPerformance))
            throw new InvalidDataException("Query performance requires a supported matching version and output schema.");

        bool incrementalDeadlocks = manifest.Id.Value == "deadlocks.system-health" && manifest.ManifestVersion.Value == 2;
        if (incrementalDeadlocks)
        {
            DeadlockReadExecutionContext context = request.DeadlockReadContext
                ?? throw new InvalidDataException("Incremental deadlock output requires a fenced read context.");
            if (result.DeadlockReadProof is { } proof)
            {
                context.Validate(proof);
                if (proof.State == DeadlockReadSourceState.Ready && (result.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
                    || result.Accounting.SourceRowsRead != proof.Positions.Count || result.Accounting.ResponseBytes < 128 + 64 * proof.Positions.Count
                    || result.Payload.Deadlocks.Items.Count > proof.Positions.Count || proof.SourceLoss && !result.Loss.HasLoss
                    || result.Payload.Deadlocks.Items.Any(d => d.ParseTruncated) && !proof.SourceLoss)
                    || proof.State != DeadlockReadSourceState.Ready && (result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial || result.Payload.ItemCount != 0))
                    throw new InvalidDataException("Incremental deadlock source proof contradicts its output.");
            }
            else if (result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
                throw new InvalidDataException("Incremental deadlock success omitted its source proof.");
        }
        else if (request.DeadlockReadContext is not null || result.DeadlockReadProof is not null)
            throw new InvalidDataException("Deadlock read context belongs only to the incremental deadlock contract.");

        int sourceRowBound = manifest.Id.Value == "sql-agent.failures"
            ? OperationalHealthBounds.AgentScanRows
            : manifest.Id.Value == "backups.status" && manifest.ManifestVersion.Value == 2
                ? BackupV2ReadSnapshot.MaximumRows + 1
            : manifest.Id.Value == "availability-groups.health"
            ? OperationalHealthBounds.AvailabilityMaximumRows + 1
            : manifest.Id.Value == "replication.health"
                ? ReplicationBounds.MaximumRows + 1
                : manifest.Id.Value == "queries.performance-groups"
                    ? 2 * (manifest.Limits.MaxRows + 1) + QueryPerformanceBounds.MaximumDatabases + 1
                : manifest.Id.Value == "storage.volume"
                    ? manifest.Limits.MaxRows + 1
                : manifest.Id.Value == "queries.performance"
            ? manifest.ManifestVersion.Value == 2
                ? QueryPerformanceV2OutputValidation.MaximumSourceRows
                : QueryPerformanceBounds.MaximumDatabases * QueryPerformanceBounds.ProbeRows
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
            (payload.QueryStoreGroups?.Items.Count ?? 0) > Contract.MaxQueryStoreGroupObservations ||
            (payload.QueryStoreGroups is not null && Contract.MaxQueryStoreGroupObservations == 0) ||
            (payload.PlanCacheRuntime?.Items.Count ?? 0) > Contract.MaxPlanCacheRuntimeObservations ||
            (payload.PlanCacheRuntime is not null && Contract.MaxPlanCacheRuntimeObservations == 0) ||
            payload.ServerConfigurations.Items.Count > Contract.MaxServerConfigurationObservations ||
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
                item.TargetId != request.TargetId || item.TargetRevision != request.TargetRevision) ||
            payload.QueryStoreGroups is { } groupOutput &&
                (groupOutput.TargetId != request.TargetId || groupOutput.TargetRevision != request.TargetRevision) ||
            payload.ServerConfigurations.Items.Any(item =>
                item.TargetId != request.TargetId ||
                item.TargetRevision != request.TargetRevision ||
                item.ServerMajorVersion != request.CapabilityProfile.ServerIdentity?.Version.Major))
        {
            throw new InvalidDataException("Collector inventory output belongs to a different target revision.");
        }

        if (manifest.Id.Value is "backups.status" or "sql-agent.failures" or "tempdb.health" or "availability-groups.health" or "replication.health")
        {
            ValidateOperationalHealthPayload(manifest, payload.OperationalHealth, request, result);
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

        bool queryPerformanceV2 = manifest.Id.Value == "queries.performance" &&
            manifest.ManifestVersion.Value == 2 && manifest.OutputSchemaVersion.Value == 2 &&
            manifest.OutputKind == CollectorOutputKind.QueryPerformance;
        if (payload.PlanCacheRuntime is not null && !queryPerformanceV2)
            throw new InvalidDataException("Selected cache tuples require the exact query performance v2 contract.");
        if (queryPerformanceV2)
            QueryPerformanceV2OutputValidation.Validate(payload, request.TargetId, request.TargetRevision,
                result.Outcome, result.Accounting, result.Loss);

        // Incomplete topology and unqualified cache evidence require a supported
        // protocol with explicit coverage. This is not a generic Partial marker.
        if (result.Loss.Kind == CollectorLossKind.VisibilityIncomplete &&
            !(queryPerformanceV2 && payload.PlanCacheRuntime is { QualifiedExhaustive: false } &&
                result.Outcome == CollectorRunOutcome.Partial && result.Reason == CollectorRunReason.VisibilityIncomplete) &&
            (manifest.Id.Value is not ("availability-groups.health" or "replication.health" or "backups.status") ||
             result.Outcome != CollectorRunOutcome.Partial ||
             result.Reason != CollectorRunReason.VisibilityIncomplete ||
             payload.OperationalHealth?.Snapshot is not (AvailabilityGroupsSnapshot { State: OperationalObservationState.Degraded } or AvailabilityGroupsV2Snapshot { State: OperationalObservationState.Degraded } or ReplicationHealthSnapshot { State: OperationalObservationState.Degraded } or BackupV2ReadSnapshot { Coverage: BackupFreshnessCoverage.Partial })))
        {
            throw new InvalidDataException("VisibilityIncomplete requires an explicitly incomplete supported health observation.");
        }

        if (manifest.Id.Value == "queries.performance")
        {
            ValidateQueryPerformanceBoundsAndStatuses(payload, result);
        }
        if (manifest.OutputKind == CollectorOutputKind.QueryStoreGroups)
        {
            QueryStoreGroupOutputBatch? groups = payload.QueryStoreGroups;
            if (payload.QueryPerformanceStatuses.Count != 0 || payload.QueryPerformanceTargetStatus is not null ||
                payload.HostMetricsContext is not null)
                throw new InvalidDataException("Query Store group output cannot carry another collector's sidecars.");
            if (result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial)
            {
                if (groups is null || result.Accounting.OutputItemsProduced != groups.Items.Count ||
                    result.Accounting.OutputBytes < groups.EstimatedSizeBytes ||
                    (!groups.RuntimeComplete && result.Outcome != CollectorRunOutcome.Partial) ||
                    (groups.HasTruncatedWaitCoverage && result.Outcome != CollectorRunOutcome.Partial))
                    throw new InvalidDataException("Query Store group coverage or accounting is incomplete.");
            }
            else if (groups is not null)
                throw new InvalidDataException("A failed Query Store group run cannot carry source output.");
        }
        if (manifest.OutputKind == CollectorOutputKind.ServerConfigurations)
        {
            IReadOnlyList<ServerConfigurationObservation> items = payload.ServerConfigurations.Items;
            if (result.Outcome == CollectorRunOutcome.Succeeded &&
                (items.Count == 0 || result.Accounting.SourceRowsRead != items.Count ||
                 result.Accounting.OutputItemsProduced != items.Count ||
                 result.Accounting.OutputBytes < payload.ServerConfigurations.EstimatedSizeBytes) ||
                result.Outcome != CollectorRunOutcome.Succeeded && items.Count != 0)
                throw new InvalidDataException("Server configuration scan coverage or accounting is invalid.");
        }
    }

    private static void ValidateOperationalHealthPayload(CollectorManifest manifest, OperationalHealthPayload? envelope,
        CollectorExecutionRequest request, CollectorExecutionResult result)
    {
        string collectorId = manifest.Id.Value;
        if (collectorId == "backups.status" && manifest.ManifestVersion.Value == 2 && manifest.OutputSchemaVersion.Value == 2)
        {
            ValidateBackupsV2(manifest, envelope, request, result);
            return;
        }
        if (collectorId == "availability-groups.health" && manifest.ManifestVersion.Value == 2 &&
            manifest.OutputSchemaVersion.Value == 2)
        {
            ValidateAvailabilityGroupsV2(manifest, envelope, request, result);
            return;
        }
        if (envelope is null) return; // Unsupported/permission outcomes carry an empty payload.
        if (collectorId == "backups.status" && manifest.ManifestVersion.Value == 1 && manifest.OutputSchemaVersion.Value == 1 &&
            envelope.Snapshot is BackupStatusSnapshot backups && backups.TargetId == request.TargetId &&
            backups.TargetRevision == request.TargetRevision && backups.Items.Count <= OperationalHealthBounds.BackupMaximumRows) return;
        if (collectorId == "sql-agent.failures" && envelope.Snapshot is SqlAgentFailureSnapshot agent && agent.TargetId == request.TargetId && agent.TargetRevision == request.TargetRevision && agent.Items.Count <= OperationalHealthBounds.AgentMaximumRows && agent.SourceRowsRead is >= 0 and <= OperationalHealthBounds.AgentScanRows) return;
        if (collectorId == "tempdb.health" && envelope.Snapshot is TempDbSnapshot tempdb && tempdb.TargetId == request.TargetId && tempdb.TargetRevision == request.TargetRevision && tempdb.Files.Count <= OperationalHealthBounds.TempDbMaximumFiles) return;
        if (collectorId == "availability-groups.health" && manifest.ManifestVersion.Value == 1 && manifest.OutputSchemaVersion.Value == 1 && envelope.Snapshot is AvailabilityGroupsSnapshot groups && groups.TargetId == request.TargetId && groups.TargetRevision == request.TargetRevision && groups.Replicas.Count + groups.Databases.Count <= OperationalHealthBounds.AvailabilityMaximumRows) return;
        if (collectorId == "replication.health" && envelope.Snapshot is ReplicationHealthSnapshot replication && replication.TargetId == request.TargetId && replication.TargetRevision == request.TargetRevision && replication.Items.Count <= ReplicationBounds.MaximumRows && replication.SourceRowsRead is >= 0 and <= ReplicationBounds.MaximumRows + 1) return;
        throw new InvalidDataException("Operational-health output kind, target revision, or bounds are invalid.");
    }

    private static void ValidateBackupsV2(CollectorManifest manifest, OperationalHealthPayload? envelope,
        CollectorExecutionRequest request, CollectorExecutionResult result)
    {
        if (manifest.OutputKind != CollectorOutputKind.BackupsStatus || result.Payload.HostMetricsContext is not null ||
            result.Payload.QueryPerformanceStatuses.Count != 0 || result.Payload.QueryPerformanceTargetStatus is not null)
            throw new InvalidDataException("Backup v2 output cannot carry another collector's sidecars.");
        if (result.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
        {
            if (envelope is not null || result.Accounting.SourceRowsRead != 0 || result.Accounting.OutputItemsProduced != 0 ||
                result.Accounting.ResponseBytes != 0 || result.Accounting.OutputBytes != 0 ||
                (result.Outcome == CollectorRunOutcome.OutputInvalid
                    ? result.Loss.Kind != CollectorLossKind.OutputValidationFailure || result.Loss.MinimumLostItems < 1
                    : result.Loss.HasLoss))
                throw new InvalidDataException("A failed backup v2 run cannot publish evidence.");
            return;
        }
        if (envelope?.Snapshot is not BackupV2ReadSnapshot snapshot || snapshot.TargetId != request.TargetId ||
            snapshot.TargetRevision != request.TargetRevision || snapshot.RunId != request.RunId ||
            result.Accounting.SourceRowsRead != snapshot.SourceRowsRead || envelope.ItemCount != snapshot.ItemCount ||
            result.Accounting.OutputItemsProduced != snapshot.ItemCount || envelope.EstimatedSizeBytes != snapshot.EstimatedSizeBytes ||
            result.Accounting.OutputBytes < result.Payload.EstimatedSizeBytes || result.Accounting.ResponseBytes < result.Accounting.OutputBytes)
            throw new InvalidDataException("Backup v2 identity, source accounting or output accounting is invalid.");
        bool valid = snapshot.Coverage switch
        {
            BackupFreshnessCoverage.Complete => result.Outcome == CollectorRunOutcome.Succeeded && !result.Loss.HasLoss &&
                result.Reason == CollectorRunReason.Completed,
            BackupFreshnessCoverage.Partial => result.Outcome == CollectorRunOutcome.Partial &&
                result.Reason == CollectorRunReason.VisibilityIncomplete && result.Loss.Kind == CollectorLossKind.VisibilityIncomplete &&
                result.Loss.MinimumLostItems == 1 && !result.Loss.CountIsExact && result.Loss.MinimumLostBytes == 0,
            BackupFreshnessCoverage.Truncated => result.Outcome == CollectorRunOutcome.Partial && snapshot.ItemCount == 0 &&
                snapshot.SourceRowsRead > 0 && result.Loss.MinimumLostItems == snapshot.SourceRowsRead && !result.Loss.CountIsExact &&
                result.Loss.MinimumLostBytes == 0 &&
                (result.Reason == CollectorRunReason.SourceRowLimit && result.Loss.Kind == CollectorLossKind.SourceRowLimit ||
                 result.Reason == CollectorRunReason.ResponseByteLimit && result.Loss.Kind == CollectorLossKind.ResponseByteLimit),
            _ => false,
        };
        if (!valid) throw new InvalidDataException("Backup v2 completeness and disclosed loss are inconsistent.");
    }

    private static void ValidateAvailabilityGroupsV2(CollectorManifest manifest, OperationalHealthPayload? envelope,
        CollectorExecutionRequest request, CollectorExecutionResult result)
    {
        if (manifest.OutputKind != CollectorOutputKind.AvailabilityGroupsHealth ||
            result.Payload.HostMetricsContext is not null || result.Payload.QueryPerformanceStatuses.Count != 0 ||
            result.Payload.QueryPerformanceTargetStatus is not null)
            throw new InvalidDataException("AG v2 output cannot carry another collector's sidecars.");
        if (result.Outcome is not (CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial))
        {
            if (envelope is not null || result.Accounting.SourceRowsRead != 0 ||
                result.Accounting.OutputItemsProduced != 0 || result.Accounting.ResponseBytes != 0 ||
                result.Accounting.OutputBytes != 0 ||
                (result.Outcome == CollectorRunOutcome.OutputInvalid
                    ? result.Loss.Kind != CollectorLossKind.OutputValidationFailure || result.Loss.MinimumLostItems < 1
                    : result.Loss.HasLoss))
                throw new InvalidDataException("A failed AG v2 run cannot publish evidence.");
            return;
        }
        if (envelope?.Snapshot is not AvailabilityGroupsV2Snapshot groups ||
            groups.TargetId != request.TargetId || groups.TargetRevision != request.TargetRevision ||
            groups.RunId != request.RunId || result.Accounting.SourceRowsRead != groups.SourceRowsRead ||
            envelope.ItemCount != groups.Replicas.Count + groups.Databases.Count ||
            result.Accounting.OutputItemsProduced != envelope.ItemCount ||
            envelope.EstimatedSizeBytes < groups.EstimatedSizeBytes ||
            result.Accounting.OutputBytes < result.Payload.EstimatedSizeBytes ||
            result.Accounting.ResponseBytes < result.Accounting.OutputBytes)
            throw new InvalidDataException("AG v2 identity, coverage or accounting is invalid.");

        bool validCoverage = groups.State switch
        {
            OperationalObservationState.Complete or OperationalObservationState.NoData =>
                result.Outcome == CollectorRunOutcome.Succeeded && !result.Loss.HasLoss &&
                result.Reason == CollectorRunReason.Completed,
            OperationalObservationState.Degraded =>
                result.Outcome == CollectorRunOutcome.Partial &&
                result.Loss.Kind == CollectorLossKind.VisibilityIncomplete &&
                result.Loss.MinimumLostItems == 1 && !result.Loss.CountIsExact &&
                result.Loss.MinimumLostBytes == 0 && result.Reason == CollectorRunReason.VisibilityIncomplete,
            OperationalObservationState.Partial =>
                result.Outcome == CollectorRunOutcome.Partial && !result.Loss.CountIsExact &&
                (result.Loss.Kind == CollectorLossKind.SourceRowLimit &&
                    result.Reason == CollectorRunReason.SourceRowLimit &&
                    groups.SourceRowsRead == manifest.Limits.MaxRows + 1 && envelope.ItemCount == manifest.Limits.MaxRows &&
                    result.Loss.MinimumLostItems == 1 && result.Loss.MinimumLostBytes == 0 ||
                 result.Loss.Kind == CollectorLossKind.ResponseByteLimit &&
                    result.Reason == CollectorRunReason.ResponseByteLimit && envelope.ItemCount == 0 &&
                    groups.SourceRowsRead > 0 && result.Loss.MinimumLostItems == groups.SourceRowsRead),
            _ => false,
        };
        if (!validCoverage)
            throw new InvalidDataException("AG v2 completeness and loss evidence are inconsistent.");
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
