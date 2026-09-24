using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Mcp;

#pragma warning disable CA1859
/// <summary>
/// The MCP wire boundary. Every supported application return type has a
/// deliberately small mapper; no application object is serialized wholesale.
/// </summary>
internal static class McpWireMapper
{
    private static readonly JsonSerializerOptions PrimitiveOptions = new(JsonSerializerDefaults.Web);
    private static JsonValue? V(object? value) => value is null ? null : JsonValue.Create(value);
    private static string T(DateTimeOffset value) => value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
    private static string? T(DateTimeOffset? value) => value is null ? null : T(value.Value);
    private static string E<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static JsonObject O(params (string Name, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach ((string name, object? value) in values) if (value is not null) result[name] = value is JsonNode node ? node : JsonSerializer.SerializeToNode(value, PrimitiveOptions);
        return result;
    }
    private static JsonArray A<T>(IEnumerable<T> values, Func<T, JsonNode?> map) => new(values.Select(map).Where(static x => x is not null).Cast<JsonNode>().ToArray());
    private static string Id(object? value) => value switch { MonitoredInstanceId x => x.Value.ToString(), CollectorRunId x => x.Value.ToString(), CollectorId x => x.Value, ObservationTargetRevision x => x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), Guid x => x.ToString("D"), _ => value?.ToString() ?? string.Empty };
    private static string? NullableId(object? value) => value is null ? null : Id(value);
    private static JsonObject ValueObject(string value) => O(("value", value));
    private static JsonObject NumberObject(long value) => O(("value", value));
    private static JsonNode Dimensions(IReadOnlyDictionary<string, string>? values)
    {
        // Dimension names are data, not schema. Use a bounded list of pairs so
        // arbitrary JSON object keys cannot expand the MCP contract.
        return new JsonArray((values ?? new Dictionary<string, string>()).OrderBy(static x => x.Key, StringComparer.Ordinal).Take(16).Select(x => (JsonNode)O(("key", x.Key), ("value", x.Value))).ToArray());
    }

    public static JsonNode? Map(string tool, object? value)
    {
        return tool switch
        {
        "list_metric_catalog" => value is MetricCatalogProjection catalog ? MetricCatalog(catalog) : null,
        "list_incidents" => value is IncidentListPage incidents ? IncidentList(incidents) : null,
        "list_instances" => value is ObservationTargetPage p ? O(("targets", A(p.Targets, Target)), ("nextCursor", Cursor(p.NextCursor)), ("hasMore", p.NextCursor is not null)) : null,
        "get_instance_capabilities" => value is ObservationTargetStatusSnapshot s ? Status(s) : null,
        "get_instance_health" => value is InstanceHealthProjection h ? Health(h) : null,
        "get_active_alerts" => value is AlertActivePage a ? O(("items", A(a.Items, Alert)), ("snapshotUtc", T(a.SnapshotUtc)), ("nextCursor", Cursor(a.NextCursor)), ("hasMore", a.NextCursor is not null)) : null,
        "get_metric_series" => value is MetricSeriesPage m ? Metric(m) : null,
        "compare_metric_windows" => value is WindowComparisonResult c ? O(("leftValue", c.LeftValue), ("rightValue", c.RightValue), ("delta", c.Delta), ("percent", c.Percent), ("leftSamples", c.LeftSamples), ("rightSamples", c.RightSamples), ("complete", c.Complete)) : null,
        "get_wait_summary" => value is ServerWaitSummaryPage w ? Activity(w, Wait) : null,
        "get_active_sessions" => value is ActivitySessionPage ss ? Activity(ss, Session) : null,
        "get_active_requests" => value is ActivityRequestPage rr ? Activity(rr, Request) : null,
        "get_blocking_chain" => value is CurrentBlockingPage b ? Activity(b, Blocking) : null,
        "get_blocking_history" => value is BlockingHistoryPage bh ? BlockingHistory(bh) : null,
        "get_deadlock" => value is DeadlockDetailDto d ? O(("targetId", Id(d.Summary.TargetId)), ("eventId", Id(d.Summary.EventId)), ("occurredAtUtc", T(d.Summary.OccurredAtUtc)), ("fingerprint", d.Summary.Fingerprint), ("participants", A(d.Participants, Participant)), ("relations", A(d.Relations, Relation)), ("participantCount", d.Summary.ParticipantCount), ("relationCount", d.Summary.RelationCount), ("parseTruncated", d.Summary.ParseTruncated), ("collectedAtUtc", T(d.Summary.CollectedAtUtc))) : null,
        "search_deadlocks" => value is DeadlockPage dp ? O(("items", A(dp.Items, Deadlock)), ("snapshotUtc", T(dp.RepositoryTimeUtc)), ("nextCursor", Cursor(dp.NextCursor)), ("hasMore", dp.NextCursor is not null)) : null,
        "get_top_queries" => value is TopQueryPage tq ? QueryPage(tq.Items, tq.HasMore, tq.SnapshotUtc, tq.NextCursor, top: true) : null,
        "get_query_history" => value is QueryHistoryPage qh ? QueryPage(qh.Items, qh.HasMore, qh.SnapshotUtc, qh.NextCursor, top: false) : null,
        "get_query_plan_metadata" => value is QueryPlanMetadataDto plan ? O(("targetId", Id(plan.TargetId)), ("plan", Plan(plan.Plan)), ("source", E(plan.Source)), ("observedAtUtc", T(plan.ObservedAtUtc)), ("coverage", E(plan.Coverage)), ("contentAvailable", plan.ContentAvailable)) : null,
        "get_database_health" => value is DatabaseHealthPage db ? Database(db, file: false) : null,
        "get_file_io" => value is DatabaseFileHealthPage files ? Database(files, file: true) : null,
        "get_tempdb_health" => value is TempDbSnapshot temp ? Operational(temp, temp.Files.Select(File), temp.NextCursor) : null,
        "get_storage_forecast" => value is StorageForecastPage f ? Forecast(f) : null,
        "get_backup_status" => value is BackupStatusSnapshot backups ? Operational(backups, backups.Items.Select(Backup), backups.NextCursor) : null,
        "get_job_failures" => value is SqlAgentFailureSnapshot jobs ? Operational(jobs, jobs.Items.Select(Job), jobs.NextCursor) : null,
        "get_availability_health" => value is AvailabilityGroupsSnapshot ag ? Availability(ag) : null,
        "get_incident_evidence" => value is IncidentEvidencePage incident ? Incident(incident) : null,
        "search_diagnostic_events" => value is DiagnosticEventSearchPage events ? Diagnostic(events) : null,
        _ => throw new ArgumentException($"No reviewed MCP wire mapper exists for {tool}.")
        };
    }

    private static JsonNode Target(ObservationTarget t) => O(("targetId", Id(t.TargetId)), ("key", t.Key.Value), ("displayName", t.DisplayName.Value), ("lifecycle", E(t.Lifecycle)), ("revision", t.Revision.Value), ("createdAtUtc", T(t.CreatedAtUtc)), ("discoveryRequestedAtUtc", T(t.DiscoveryRequestedAtUtc)), ("updatedAtUtc", T(t.UpdatedAtUtc)), ("retiredAtUtc", T(t.RetiredAtUtc)));
    private static JsonNode Status(ObservationTargetStatusSnapshot s) => O(("targetId", Id(s.Target.TargetId)), ("state", E(s.Target.Lifecycle)), ("targetRevision", s.Target.Revision.Value), ("snapshotUtc", T(s.Target.UpdatedAtUtc)), ("repositoryTimeUtc", T(s.Target.UpdatedAtUtc)), ("capabilities", s.LatestCapabilityProfile is null ? null : O(("state", E(s.LatestCapabilityProfile.Outcome)), ("status", s.CapabilityProfileIsCurrent ? "current" : "stale"), ("reason", E(s.LatestCapabilityProfile.Reason)), ("revision", s.LatestCapabilityProfile.TargetRevision.Value), ("observedAtUtc", T(s.LatestCapabilityProfile.CheckedAtUtc)))));
    private static JsonNode Health(InstanceHealthProjection h) => O(("targetId", Id(h.TargetId)), ("coreCollector", Collector(h.CoreCollector)), ("repositoryTimeUtc", T(h.RepositoryTimeUtc)), ("state", E(h.CoreCollector.State)), ("targetRevision", h.CoreCollector.TargetId == h.TargetId ? h.CoreCollector.LatestRun?.TargetRevision.Value : null), ("snapshotUtc", T(h.RepositoryTimeUtc)));
    private static JsonNode Collector(CollectorHealthProjection c) => O(("state", E(c.State)), ("reason", E(c.Reason)), ("status", E(c.State)), ("health", E(c.State)), ("targetId", Id(c.TargetId)), ("collectorId", c.CollectorId.Value), ("observedAtUtc", T(c.RepositoryTimeUtc)), ("lastSuccessAtUtc", T(c.LastSuccessAtUtc)), ("nextDueAtUtc", T(c.NextDueAtUtc)));
    private static JsonNode Alert(AlertActiveDto x) => O(("alertId", Id(x.AlertId)), ("ruleId", Id(x.RuleId)), ("targetId", Id(x.TargetId)), ("ruleName", x.RuleName), ("state", E(x.State)), ("firstObservedUtc", T(x.FirstObservedUtc)), ("firedUtc", T(x.FiredUtc)), ("acknowledgedUtc", T(x.AcknowledgedUtc)), ("value", x.Value), ("reason", x.Reason), ("deliverySuppressed", x.DeliverySuppressed));
    private static JsonNode MetricItem(MetricSeriesItem x) => O(("observedAtUtc", T(x.ObservedAtUtc)), ("value", x.Value), ("dimensions", Dimensions(x.Dimensions)));
    private static JsonNode Metric(MetricSeriesPage x) => O(("targetId", Id(x.TargetId)), ("metricKey", x.MetricKey), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, MetricItem)), ("state", x.State), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)));
    private static JsonNode Wait(ServerWaitSummaryItem x)
    {
        JsonObject result = O(("waitType", x.WaitType.Value), ("waitingTasksCount", x.WaitingTasksCount),
            ("waitTimeMilliseconds", x.WaitTimeMilliseconds), ("maximumWaitTimeMilliseconds", x.MaximumWaitTimeMilliseconds),
            ("signalWaitTimeMilliseconds", x.SignalWaitTimeMilliseconds), ("baselineAvailable", x.BaselineAvailable),
            ("resetDetected", x.ResetDetected), ("observedAtUtc", T(x.ObservedAtUtc)));
        // A missing baseline or reset is unknown, not a zero or cumulative delta.
        result["waitingTasksDelta"] = x.WaitingTasksDelta;
        result["waitTimeMillisecondsDelta"] = x.WaitTimeMillisecondsDelta;
        result["signalWaitTimeMillisecondsDelta"] = x.SignalWaitTimeMillisecondsDelta;
        return result;
    }
    private static JsonNode Session(ActivitySessionSnapshotItem x) => O(("sessionId", x.SessionId), ("status", E(x.Status)), ("isUserProcess", x.IsUserProcess), ("openTransactionCount", x.OpenTransactionCount), ("cpuMilliseconds", x.CpuMilliseconds), ("memoryUsagePages", x.MemoryUsagePages), ("reads", x.Reads), ("writes", x.Writes), ("logicalReads", x.LogicalReads), ("totalElapsedMilliseconds", x.TotalElapsedMilliseconds), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonNode Request(ActivityRequestSnapshotItem x) => O(("sessionId", x.SessionId), ("requestId", x.RequestId), ("status", E(x.Status)), ("command", E(x.Command)), ("cpuMilliseconds", x.CpuMilliseconds), ("totalElapsedMilliseconds", x.TotalElapsedMilliseconds), ("reads", x.Reads), ("writes", x.Writes), ("logicalReads", x.LogicalReads), ("rowCount", x.RowCount), ("percentComplete", x.PercentComplete), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonObject Blocking(BlockingEdgeSnapshotItem x)
    {
        JsonObject result = O(("blockedSessionId", x.BlockedSessionId), ("blockerKind", E(x.BlockerKind)),
            ("waitType", x.WaitType.Value), ("waitingTaskCount", x.WaitingTaskCount),
            ("waitDurationMilliseconds", x.WaitDurationMilliseconds), ("chainDepth", x.ChainDepth),
            ("chainState", E(x.ChainState)), ("observedAtUtc", T(x.ObservedAtUtc)));
        result["blockerSessionId"] = x.BlockerSessionId;
        result["rootBlockerSessionId"] = x.RootBlockerSessionId;
        return result;
    }
    private static JsonNode Loss(CollectorLossEvidence x) => O(("kind", E(x.Kind)), ("minimumLostItems", x.MinimumLostItems), ("countIsExact", x.CountIsExact), ("minimumLostBytes", x.MinimumLostBytes));
    private static JsonNode Evidence(ActivitySnapshotEvidence x) => O(("targetId", Id(x.TargetId)), ("runId", Id(x.RunId)), ("targetRevision", long.TryParse(x.TargetRevision, out long revision) ? revision : 0), ("outcome", E(x.Outcome)), ("reason", E(x.Reason)), ("loss", x.Loss.HasLoss ? Loss(x.Loss) : null), ("completedAtUtc", T(x.CompletedAtUtc)));
    private static JsonNode Activity(ServerWaitSummaryPage x, Func<ServerWaitSummaryItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(ActivitySessionPage x, Func<ActivitySessionSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(ActivityRequestPage x, Func<ActivityRequestSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(CurrentBlockingPage x, Func<BlockingEdgeSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode BlockingHistory(BlockingHistoryPage x) => O(("targetId", Id(x.TargetId)),
        ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, MapBlockingHistoryItem)),
        ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));

    private static JsonNode MapBlockingHistoryItem(BlockingHistoryItem x)
    {
        JsonObject result = Blocking(x.Edge);
        result["evidence"] = Evidence(x.Evidence);
        return result;
    }
    private static JsonNode Participant(DeadlockParticipantDto x) => O(("sessionId", x.SessionId), ("isVictim", x.IsVictim));
    private static JsonNode Relation(DeadlockRelationDto x) => O(("blockerSessionId", x.BlockerSessionId), ("waiterSessionId", x.WaiterSessionId), ("resourceCategory", x.ResourceCategory), ("lockMode", x.LockMode));
    private static JsonNode Deadlock(DeadlockSummaryDto x) => O(("targetId", Id(x.TargetId)), ("eventId", Id(x.EventId)), ("occurredAtUtc", T(x.OccurredAtUtc)), ("fingerprint", x.Fingerprint), ("participantCount", x.ParticipantCount), ("relationCount", x.RelationCount), ("parseTruncated", x.ParseTruncated), ("collectedAtUtc", T(x.CollectedAtUtc)));
    private static JsonNode Query(QueryOpaqueIdentity x) => O(("databaseId", x.DatabaseId), ("queryFingerprint", x.QueryFingerprint));
    private static JsonNode Plan(PlanOpaqueIdentity x) => O(("databaseId", x.Query.DatabaseId), ("queryFingerprint", x.Query.QueryFingerprint), ("planFingerprint", x.PlanFingerprint));
    private static JsonNode QueryPage<U>(IReadOnlyList<U> items, bool more, DateTimeOffset snapshot, QueryPerformanceCursorEnvelope? cursor, bool top) where U : class => O(("items", A(items, x => x switch { TopQueryDto t => TopQuery(t), QueryHistoryDto h => HistoryQuery(h), _ => null })), ("snapshotUtc", T(snapshot)), ("nextCursor", Cursor(cursor)), ("hasMore", more));
    private static JsonNode TopQuery(TopQueryDto x) => O(("targetId", Id(x.TargetId)), ("query", Query(x.Query)), ("plan", x.Plan is null ? null : Plan(x.Plan)), ("source", E(x.Source)), ("sourceState", E(x.SourceState)), ("metric", E(x.Metric)), ("value", x.Value), ("semantics", E(x.Semantics)), ("intervalStartUtc", T(x.IntervalStartUtc)), ("intervalEndUtc", T(x.IntervalEndUtc)), ("coverage", E(x.Coverage)), ("fresh", x.Fresh), ("truncated", x.Truncated), ("contentAvailable", x.ContentAvailable), ("collectionRunId", NullableId(x.CollectionRunId)), ("planFingerprint", x.Plan?.PlanFingerprint), ("observationKey", x.ObservationKey));
    private static JsonNode HistoryQuery(QueryHistoryDto x) => O(("targetId", Id(x.TargetId)), ("query", Query(x.Query)), ("source", E(x.Source)), ("sourceState", E(x.SourceState)), ("metrics", O(("cpuMilliseconds", x.Metrics.CpuMilliseconds), ("durationMilliseconds", x.Metrics.DurationMilliseconds), ("executions", x.Metrics.Executions), ("logicalReads", x.Metrics.LogicalReads), ("writes", x.Metrics.Writes), ("rows", x.Metrics.Rows))), ("semantics", E(x.Semantics)), ("intervalStartUtc", T(x.IntervalStartUtc)), ("intervalEndUtc", T(x.IntervalEndUtc)), ("resetDetected", x.ResetDetected), ("fresh", x.Fresh), ("truncated", x.Truncated), ("contentAvailable", x.ContentAvailable), ("collectionRunId", NullableId(x.CollectionRunId)), ("coverage", E(x.Coverage)), ("planFingerprint", x.PlanFingerprint), ("observationKey", x.ObservationKey));
    private static JsonNode Forecast(StorageForecastPage x) => O(("targetId", Id(x.TargetId)), ("metricKey", x.MetricKey), ("horizon", x.Horizon.TotalDays), ("items", A(x.Items, ForecastItem)), ("state", x.State), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)));
    private static JsonNode ForecastItem(StorageForecastItem x) => O(("forecastId", NullableId(x.ForecastId)), ("metricKey", x.MetricKey), ("horizonStartUtc", T(x.HorizonStartUtc)), ("horizonEndUtc", T(x.HorizonEndUtc)), ("estimate", x.Estimate), ("lowerBound", x.LowerBound), ("upperBound", x.UpperBound), ("slopePerDay", x.SlopePerDay), ("confidence", x.Confidence), ("residual", x.Residual), ("model", x.Model), ("sourceGeneration", x.SourceGeneration), ("visibilityState", x.VisibilityState), ("dimensionsSha256", x.DimensionsSha256));
    private static JsonNode Operational<S>(S x, IEnumerable<JsonNode> items, string? cursor) where S : IOperationalHealthSnapshot
    {
        return x switch
        {
            BackupStatusSnapshot b => O(("items", new JsonArray(items.ToArray())), ("state", E(b.State)), ("snapshotUtc", T(b.ObservedAtUtc)), ("nextCursor", cursor), ("hasMore", cursor is not null)),
            SqlAgentFailureSnapshot a => O(("items", new JsonArray(items.ToArray())), ("state", E(a.State)), ("snapshotUtc", T(a.ObservedAtUtc)), ("nextCursor", cursor), ("hasMore", cursor is not null)),
            TempDbSnapshot t => O(("items", new JsonArray(items.ToArray())), ("state", E(t.State)), ("snapshotUtc", T(t.ObservedAtUtc)), ("nextCursor", cursor), ("hasMore", cursor is not null)),
            _ => O(("items", new JsonArray(items.ToArray())))
        };
    }
    private static JsonNode Backup(BackupStatusObservation x)
    {
        JsonObject result = O(("backupType", E(x.Kind)), ("state", E(x.Coverage)),
            ("databaseFingerprint", x.DatabaseFingerprint), ("sourceTimeUnknown", x.SourceTimeUnknown));
        result["sizeBytes"] = x.SizeBytes;
        result["backupAtUtc"] = T(x.LastFinishUtc);
        return result;
    }
    private static JsonNode Job(SqlAgentFailureObservation x) => O(("jobId", Id(x.JobId)),
        ("firstObservedAtUtc", T(x.FirstObservedAtUtc)), ("state", E(x.FailureKind)), ("failureFingerprint", x.FailureFingerprint),
        ("stepId", x.StepId), ("runStatus", x.RunStatus), ("isJobOutcome", SqlAgentFailureSemantics.IsJobOutcome(x)), ("countsAsJobFailure", SqlAgentFailureSemantics.CountsAsJobFailure(x)));
    private static JsonNode File(TempDbFileObservation x) => O(("fileId", x.FileId), ("sizeBytes", x.SizeBytes), ("usedBytes", x.UsedBytes), ("freeBytes", x.FreeBytes), ("state", E(x.State)));
    private static JsonNode Availability(AvailabilityGroupsSnapshot x) => O(
        ("items", new JsonArray(x.Replicas.Select(Replica).Concat(x.Databases.Select(Database)).ToArray())),
        ("state", E(x.State)), ("visibilityScope", E(x.VisibilityScope)), ("snapshotUtc", T(x.ObservedAtUtc)), ("truncated", x.Truncated));
    private static JsonNode Replica(AvailabilityReplicaObservation x) => O(("kind", "replica"),
        ("groupFingerprint", x.GroupFingerprint), ("replicaFingerprint", x.ReplicaFingerprint), ("role", x.Role),
        ("operationalState", x.OperationalState), ("connectedState", x.ConnectedState),
        ("visibilityScope", E(x.VisibilityScope)), ("stateAvailable", x.StateAvailable));
    private static JsonNode Database(AvailabilityDatabaseObservation x) => O(("kind", "database"),
        ("groupFingerprint", x.GroupFingerprint), ("databaseFingerprint", x.DatabaseFingerprint),
        ("synchronizationState", x.SynchronizationState), ("databaseState", x.DatabaseState),
        ("visibilityScope", E(x.VisibilityScope)), ("stateAvailable", x.StateAvailable));
    private static JsonNode IncidentList(IncidentListPage x) => O(("targetId", Id(x.TargetId)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, IncidentListItem)), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)), ("publicationRevision", x.PublicationRevision), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)));
    private static JsonObject IncidentListItem(IncidentListItem x)
    {
        JsonObject item = O(("threadId", Id(x.ThreadId)), ("openedAtUtc", T(x.OpenedAtUtc)), ("generationCount", x.GenerationCount));
        item["latestGenerationObservedAtUtc"] = T(x.LatestGenerationObservedAtUtc);
        return item;
    }
    private static JsonNode Incident(IncidentEvidencePage x) => O(("targetId", Id(x.TargetId)), ("threadId", Id(x.ThreadId)), ("items", A(x.Items, IncidentItem)), ("generations", A(x.Generations, Generation)), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)));
    private static JsonNode IncidentItem(IncidentEvidenceItem x) => O(("occurredAtUtc", T(x.OccurredAtUtc)), ("packetId", Id(x.PacketId)), ("evidenceKind", x.EvidenceKind), ("sourceRunId", NullableId(x.SourceRunId)), ("sourceDigest", x.SourceDigest), ("identityDigest", x.IdentityDigest), ("sourceCutoffDigest", x.SourceCutoffDigest), ("sourceCutoffUtc", T(x.SourceCutoffUtc)), ("confidence", x.Confidence), ("visibilityState", x.VisibilityState));
    private static JsonNode Generation(IncidentGenerationItem x) => O(("threadId", Id(x.ThreadId)), ("generation", x.Generation), ("observedAtUtc", T(x.ObservedAtUtc)), ("correlationSha256", x.CorrelationSha256), ("supersedesPrevious", x.SupersedesPrevious), ("evidencePacketId", NullableId(x.EvidencePacketId)));
    private static JsonNode Diagnostic(DiagnosticEventSearchPage x) => O(("targetId", Id(x.TargetId)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, DiagnosticItem)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)));
    private static JsonNode DiagnosticItem(DiagnosticEventItem x) => O(("occurredAtUtc", T(x.OccurredAtUtc)), ("eventId", Id(x.EventId)), ("eventKind", x.EventKind), ("severity", x.Severity), ("safeMetadata", O(("metricKey", x.SafeMetadata.MetricKey), ("participantCount", x.SafeMetadata.ParticipantCount), ("relationCount", x.SafeMetadata.RelationCount), ("parseTruncated", x.SafeMetadata.ParseTruncated))), ("collectedAtUtc", T(x.CollectedAtUtc)), ("targetRevision", x.TargetRevision.Value));
    private static JsonNode MetricCatalog(MetricCatalogProjection catalog) => O(
        ("version", catalog.Version), ("checksum", catalog.Checksum),
        ("items", A(catalog.Items, static entry => O(
            ("metricKey", entry.MetricKey), ("displayName", entry.DisplayName), ("unit", entry.Unit),
            ("source", entry.Source), ("aggregation", entry.Aggregation),
            ("dimensionKeys", new JsonArray(entry.DimensionKeys.Select(static key => (JsonNode?)JsonValue.Create(key)).ToArray()))))));

    private static JsonNode Database(DatabaseHealthPage x, bool file) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, DatabaseItem)), ("snapshotRunId", NullableId(x.SnapshotRunId)), ("snapshotTargetRevision", x.SnapshotTargetRevision?.Value), ("collector", Collector(x.Collector)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null), ("repositoryTimeUtc", T(x.RepositoryTimeUtc)));
    private static JsonNode Database(DatabaseFileHealthPage x, bool file) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, DatabaseFileItem)), ("snapshotRunId", NullableId(x.SnapshotRunId)), ("snapshotTargetRevision", x.SnapshotTargetRevision?.Value), ("collector", Collector(x.Collector)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null), ("repositoryTimeUtc", T(x.RepositoryTimeUtc)));
    private static JsonNode DatabaseItem(DatabaseHealthItem x) => O(("observation", O(("databaseId", x.Observation.DatabaseId), ("databaseName", x.Observation.Name.Value), ("state", E(x.Observation.State)), ("observedAtUtc", T(x.Observation.ObservedAtUtc)))), ("collector", Collector(x.Collector)));
    private static JsonNode DatabaseFileItem(DatabaseFileHealthItem x)
    {
        DatabaseFileObservation file = x.Observation;
        JsonObject observation = O(
            ("databaseId", file.DatabaseId), ("fileId", file.FileId), ("fileName", file.LogicalName.Value),
            ("sizeBytes", file.SizeBytes), ("readOperations", file.ReadCount), ("writeOperations", file.WriteCount),
            ("readBytes", file.BytesRead), ("writeBytes", file.BytesWritten),
            ("ioStallMilliseconds", file.IoStallMilliseconds), ("observedAtUtc", T(file.ObservedAtUtc)));
        // Required nullable fields distinguish historical unknowns from zero.
        observation["readStallMilliseconds"] = V(file.ReadStallMilliseconds);
        observation["writeStallMilliseconds"] = V(file.WriteStallMilliseconds);
        return O(("observation", observation), ("collector", Collector(x.Collector)));
    }
    private static JsonNode? Cursor(object? cursor) => cursor switch
    {
        null => null,
        ObservationTargetListCursor x => O(("lastKey", ValueObject(x.LastKey.Value)), ("lastTargetId", ValueObject(Id(x.LastTargetId)))),
        AlertActiveCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("sortAtUtc", T(x.SortAtUtc)), ("alertId", Id(x.AlertId)), ("snapshotUtc", T(x.SnapshotUtc))),
        MetricSeriesCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("metricKey", x.MetricKey), ("observedAtUtc", T(x.ObservedAtUtc)), ("runId", Id(x.RunId)), ("dimensionsKey", x.DimensionsKey), ("snapshotUtc", T(x.SnapshotUtc)), ("targetRevision", NumberObject(x.TargetRevision.Value))),
        StorageForecastCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("targetRevision", NumberObject(x.TargetRevision.Value)), ("metricKey", x.MetricKey), ("dimensionsSha256", x.DimensionsSha256), ("horizon", x.Horizon), ("snapshotUtc", T(x.SnapshotUtc)), ("horizonStartUtc", T(x.HorizonStartUtc)), ("forecastId", Id(x.ForecastId))),
        DiagnosticEventCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("occurredAtUtc", T(x.OccurredAtUtc)), ("eventId", Id(x.EventId)), ("snapshotUtc", T(x.SnapshotUtc)), ("targetRevision", NumberObject(x.TargetRevision.Value))),
        ActivitySessionCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("sessionId", x.SessionId)),
        ActivityRequestCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("sessionId", x.SessionId), ("requestId", x.RequestId)),
        ServerWaitSummaryCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("baselineRunId", x.BaselineRunId is null ? null : ValueObject(Id(x.BaselineRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("waitType", ValueObject(x.WaitType.Value))),
        BlockingEdgeCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("blockedSessionId", x.BlockedSessionId), ("blockerKind", E(x.BlockerKind)), ("blockerSessionId", x.BlockerSessionId), ("waitType", ValueObject(x.WaitType.Value))),
        BlockingHistoryCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("observedAtUtc", T(x.ObservedAtUtc)), ("runId", ValueObject(Id(x.RunId))), ("blockedSessionId", x.BlockedSessionId), ("blockerKind", E(x.BlockerKind)), ("blockerSessionId", x.BlockerSessionId), ("waitType", ValueObject(x.WaitType.Value))),
        DeadlockPageCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("occurredAtUtc", T(x.OccurredAtUtc)), ("eventId", Id(x.EventId)), ("snapshotCollectedAtUtc", T(x.SnapshotCollectedAtUtc)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc))),
        DatabaseHealthCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("databaseId", x.DatabaseId)),
        DatabaseFileHealthCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("snapshotRunId", ValueObject(Id(x.SnapshotRunId))), ("snapshotTargetRevision", NumberObject(x.SnapshotTargetRevision.Value)), ("databaseId", x.DatabaseId), ("fileId", x.FileId)),
        IncidentListCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("targetRevision", NumberObject(x.TargetRevision.Value)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("snapshotUtc", T(x.SnapshotUtc)), ("publicationRevision", x.PublicationRevision), ("openedAtUtc", T(x.OpenedAtUtc)), ("threadId", Id(x.ThreadId))),
        IncidentEvidenceCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("targetRevision", NumberObject(x.TargetRevision.Value)), ("threadId", Id(x.ThreadId)), ("snapshotUtc", T(x.SnapshotUtc)), ("evidenceOccurredAtUtc", T(x.EvidenceOccurredAtUtc)), ("evidencePacketId", NullableId(x.EvidencePacketId)), ("generation", x.Generation)),
        QueryPerformanceCursorEnvelope x => O(("targetId", ValueObject(Id(x.TargetId))), ("databaseId", x.DatabaseId), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("metric", E(x.Metric)), ("metricValue", x.MetricValue), ("snapshotUtc", T(x.SnapshotUtc)), ("intervalEndUtc", T(x.IntervalEndUtc)), ("queryFingerprint", x.QueryFingerprint), ("collectionRunId", x.CollectionRunId is null ? null : Id(x.CollectionRunId)), ("planFingerprint", x.PlanFingerprint), ("observationKey", x.ObservationKey)),
        string x => O(("value", x)),
        _ => throw new ArgumentException("Unsupported cursor type at MCP boundary.")
    };
}
#pragma warning restore CA1859
