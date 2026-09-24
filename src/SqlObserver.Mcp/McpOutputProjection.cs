using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Mcp;

internal static class McpOutputProjection
{
    // Every catalog entry has an entry here. Keeping this table explicit makes
    // a newly added tool fail closed until its minimum projection is reviewed.
    private static readonly Dictionary<string, HashSet<string>> ToolFields = new(StringComparer.Ordinal)
    {
        ["list_metric_catalog"] = Fields("version", "checksum", "items"),
        ["list_incidents"] = Fields("targetId", "fromUtc", "toUtc", "items", "targetRevision", "snapshotUtc", "publicationRevision", "hasMore", "nextCursor"),
        ["list_instances"] = Fields("targets", "nextCursor", "hasMore"),
        ["get_instance_capabilities"] = Fields("targetId", "capabilities", "state", "targetRevision", "snapshotUtc", "repositoryTimeUtc"),
        ["get_instance_health"] = Fields("targetId", "coreCollector", "repositoryTimeUtc", "state", "targetRevision", "snapshotUtc"),
        ["get_active_alerts"] = Fields("items", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_metric_series"] = Fields("targetId", "metricKey", "fromUtc", "toUtc", "items", "state", "targetRevision", "snapshotUtc", "hasMore", "nextCursor"),
        ["compare_metric_windows"] = Fields("leftValue", "rightValue", "delta", "percent", "leftSamples", "rightSamples", "complete"),
        ["get_wait_summary"] = Fields("items", "evidence", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_active_sessions"] = Fields("items", "evidence", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_active_requests"] = Fields("items", "evidence", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_blocking_chain"] = Fields("items", "evidence", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_blocking_history"] = Fields("items", "evidence", "fromUtc", "toUtc", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_deadlock"] = Fields("targetId", "eventId", "occurredAtUtc", "fingerprint", "participants", "relations", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"),
        ["search_deadlocks"] = Fields("items", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_top_queries"] = Fields("items", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_query_history"] = Fields("items", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_query_plan_metadata"] = Fields("targetId", "plan", "source", "observedAtUtc", "coverage", "contentAvailable"),
        ["get_database_health"] = Fields("targetId", "items", "snapshotRunId", "snapshotTargetRevision", "collector", "nextCursor", "repositoryTimeUtc", "hasMore"),
        ["get_tempdb_health"] = Fields("items", "state", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_file_io"] = Fields("targetId", "items", "snapshotRunId", "snapshotTargetRevision", "collector", "nextCursor", "repositoryTimeUtc", "hasMore"),
        ["get_storage_forecast"] = Fields("targetId", "metricKey", "horizon", "items", "state", "targetRevision", "snapshotUtc", "hasMore", "nextCursor"),
        ["get_backup_status"] = Fields("items", "state", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_job_failures"] = Fields("items", "state", "snapshotUtc", "nextCursor", "hasMore"),
        ["get_availability_health"] = Fields("items", "state", "visibilityScope", "snapshotUtc", "truncated"),
        ["get_incident_evidence"] = Fields("targetId", "threadId", "items", "generations", "targetRevision", "snapshotUtc", "hasMore", "nextCursor"),
        ["search_diagnostic_events"] = Fields("targetId", "fromUtc", "toUtc", "items", "hasMore", "nextCursor", "targetRevision", "snapshotUtc")
    };

    private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> ToolPathFields =
        new(StringComparer.Ordinal)
        {
            ["list_metric_catalog"] = Paths("items"),
            ["list_incidents"] = Paths("items"),
            ["list_instances"] = Paths("targets"),
            ["get_instance_capabilities"] = Paths("capabilities"),
            ["get_instance_health"] = Paths("coreCollector"),
            ["get_active_alerts"] = Paths("items"),
            ["get_metric_series"] = Paths("items", "dimensions"),
            ["compare_metric_windows"] = Paths(),
            ["get_wait_summary"] = Paths("items", "evidence", "loss"),
            ["get_active_sessions"] = Paths("items", "evidence", "loss"),
            ["get_active_requests"] = Paths("items", "evidence", "loss"),
            ["get_blocking_chain"] = Paths("items", "evidence", "loss"),
            ["get_blocking_history"] = Paths("items", "evidence", "loss"),
            ["get_deadlock"] = Paths("participants", "relations"),
            ["search_deadlocks"] = Paths("items"),
            ["get_top_queries"] = Paths("items", "query", "plan"),
            ["get_query_history"] = Paths("items", "query", "metrics"),
            ["get_query_plan_metadata"] = Paths("plan"),
            ["get_database_health"] = Paths("items", "collector", "observation"),
            ["get_tempdb_health"] = Paths("items"),
            ["get_file_io"] = Paths("items", "collector", "observation"),
            ["get_storage_forecast"] = Paths("items", "dimensions"),
            ["get_backup_status"] = Paths("items"),
            ["get_job_failures"] = Paths("items"),
            ["get_availability_health"] = Paths("items"),
            ["get_incident_evidence"] = Paths("items", "generations"),
            ["search_diagnostic_events"] = Paths("items", "safeMetadata")
        };

    static McpOutputProjection()
    {
        // Populate each tool's path table independently. There is no shared
        // nested-field union: the pair (tool,path) is the contract key.
        foreach ((string tool, Dictionary<string, HashSet<string>> paths) in ToolPathFields)
        {
            foreach (string path in paths.Keys.ToArray())
                paths[path] = new HashSet<string>(FieldsForToolPath(tool, path), StringComparer.Ordinal);
            if (paths.ContainsKey("items")) paths["items"] = new HashSet<string>(ItemFields(tool), StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, HashSet<string>> Paths(params string[] paths) =>
        paths.ToDictionary(path => path, path => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);

    private static IReadOnlyCollection<string> FieldsForToolPath(string tool, string path) => (tool, path) switch
    {
        ("list_instances", "targets") => ["targetId", "key", "displayName", "lifecycle", "revision", "createdAtUtc", "discoveryRequestedAtUtc", "updatedAtUtc", "retiredAtUtc"],
        ("get_instance_capabilities", "capabilities") => ["name", "status", "reason", "state", "revision", "observedAtUtc"],
        ("get_instance_health", "coreCollector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc", "lastSuccessAtUtc", "nextDueAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "evidence") => ["targetId", "runId", "targetRevision", "outcome", "reason", "loss", "completedAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "loss") => ["kind", "minimumLostItems", "countIsExact", "minimumLostBytes"],
        ("get_top_queries" or "get_query_history", "query") => ["databaseId", "queryFingerprint"],
        ("get_top_queries", "plan") => ["databaseId", "queryFingerprint", "planFingerprint"],
        ("get_query_plan_metadata", "plan") => ["databaseId", "queryFingerprint", "planFingerprint"],
        ("get_query_history", "metrics") => ["cpuMilliseconds", "durationMilliseconds", "executions", "logicalReads", "writes", "rows"],
        ("get_database_health" or "get_file_io", "collector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc", "lastSuccessAtUtc", "nextDueAtUtc"],
        ("get_database_health", "observation") => ["databaseId", "databaseName", "state", "observedAtUtc"],
        ("get_file_io", "observation") => ["databaseId", "fileId", "fileName", "sizeBytes", "readOperations", "writeOperations", "readBytes", "writeBytes", "ioStallMilliseconds", "readStallMilliseconds", "writeStallMilliseconds", "observedAtUtc"],
        ("get_metric_series" or "get_storage_forecast", "dimensions") => ["key", "value"],
        ("get_deadlock", "participants") => ["sessionId", "isVictim"],
        ("get_deadlock", "relations") => ["blockerSessionId", "waiterSessionId", "resourceCategory", "lockMode"],
        ("get_incident_evidence", "generations") => ["threadId", "generation", "observedAtUtc", "correlationSha256", "supersedesPrevious", "evidencePacketId"],
        ("search_diagnostic_events", "safeMetadata") => ["metricKey", "participantCount", "relationCount", "parseTruncated"],
        _ => []
    };

    private static IReadOnlyCollection<string> ItemFields(string tool) => tool switch
    {
        "list_metric_catalog" => ["metricKey", "displayName", "unit", "source", "aggregation", "dimensionKeys"],
        "list_incidents" => ["threadId", "openedAtUtc", "latestGenerationObservedAtUtc", "generationCount"],
        "get_active_alerts" => ["alertId", "ruleId", "targetId", "ruleName", "state", "firstObservedUtc", "firedUtc", "acknowledgedUtc", "value", "reason", "deliverySuppressed"],
        "get_metric_series" => ["observedAtUtc", "value", "dimensions"],
        "get_wait_summary" => ["waitType", "waitingTasksCount", "waitTimeMilliseconds", "maximumWaitTimeMilliseconds", "signalWaitTimeMilliseconds", "waitingTasksDelta", "waitTimeMillisecondsDelta", "signalWaitTimeMillisecondsDelta", "baselineAvailable", "resetDetected", "observedAtUtc"],
        "get_active_sessions" => ["sessionId", "status", "isUserProcess", "openTransactionCount", "cpuMilliseconds", "memoryUsagePages", "reads", "writes", "logicalReads", "totalElapsedMilliseconds", "observedAtUtc"],
        "get_active_requests" => ["sessionId", "requestId", "status", "command", "cpuMilliseconds", "totalElapsedMilliseconds", "reads", "writes", "logicalReads", "rowCount", "percentComplete", "observedAtUtc"],
        "get_blocking_chain" => ["blockedSessionId", "blockerKind", "blockerSessionId", "rootBlockerSessionId", "chainDepth", "chainState", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        "get_blocking_history" => ["evidence", "blockedSessionId", "blockerKind", "blockerSessionId", "rootBlockerSessionId", "chainDepth", "chainState", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        "search_deadlocks" => ["targetId", "eventId", "occurredAtUtc", "fingerprint", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"],
        "get_top_queries" => ["targetId", "query", "plan", "source", "sourceState", "metric", "value", "semantics", "intervalStartUtc", "intervalEndUtc", "coverage", "fresh", "truncated", "contentAvailable", "collectionRunId", "planFingerprint", "observationKey"],
        "get_query_history" => ["targetId", "query", "source", "sourceState", "metrics", "semantics", "intervalStartUtc", "intervalEndUtc", "resetDetected", "fresh", "truncated", "contentAvailable", "collectionRunId", "coverage", "planFingerprint", "observationKey"],
        "get_database_health" => ["observation", "collector"],
        "get_file_io" => ["observation", "collector"],
        "get_tempdb_health" => ["fileId", "sizeBytes", "usedBytes", "freeBytes", "state"],
        "get_storage_forecast" => ["forecastId", "metricKey", "horizonStartUtc", "horizonEndUtc", "estimate", "lowerBound", "upperBound", "slopePerDay", "confidence", "residual", "model", "sourceGeneration", "visibilityState", "dimensionsSha256"],
        "get_backup_status" => ["backupType", "backupAtUtc", "state", "databaseFingerprint", "sizeBytes", "sourceTimeUnknown"],
        "get_job_failures" => ["jobId", "firstObservedAtUtc", "state", "failureFingerprint"],
        "get_availability_health" => ["kind", "groupFingerprint", "replicaFingerprint", "databaseFingerprint", "role", "operationalState", "connectedState", "synchronizationState", "databaseState", "visibilityScope", "stateAvailable"],
        "get_incident_evidence" => ["occurredAtUtc", "packetId", "evidenceKind", "sourceRunId", "sourceDigest", "identityDigest", "sourceCutoffDigest", "sourceCutoffUtc", "confidence", "visibilityState"],
        "search_diagnostic_events" => ["occurredAtUtc", "eventId", "eventKind", "severity", "safeMetadata", "collectedAtUtc", "targetRevision"],
        _ => []
    };

    private static HashSet<string> Fields(params string[] names)
    {
        var result = new HashSet<string>(names, StringComparer.Ordinal);
        return result;
    }

    public static void Project(string tool, JsonNode? node)
    {
        if (!ToolFields.TryGetValue(tool, out HashSet<string>? fields))
        {
            if (node is JsonObject unknown) foreach (string property in unknown.Select(x => x.Key).ToArray()) unknown.Remove(property);
            return;
        }
        ProjectNode(node, tool, fields, string.Empty, root: true);
    }

    public static IReadOnlyCollection<string> RootFields(string tool) =>
        ToolFields.TryGetValue(tool, out HashSet<string>? fields) ? fields : Array.Empty<string>();

    public static IReadOnlyCollection<string> RequiredRootFields(string tool) => tool switch
    {
        "list_metric_catalog" => ["version", "checksum", "items"],
        "list_incidents" => ["targetId", "fromUtc", "toUtc", "items", "targetRevision", "snapshotUtc", "publicationRevision", "hasMore"],
        "list_instances" => ["targets", "hasMore"],
        "get_instance_capabilities" => ["targetId", "state", "targetRevision", "snapshotUtc", "repositoryTimeUtc"],
        "get_instance_health" => ["targetId", "coreCollector", "repositoryTimeUtc", "state", "snapshotUtc"],
        "get_active_alerts" => ["items", "snapshotUtc", "hasMore"],
        "get_metric_series" => ["targetId", "metricKey", "fromUtc", "toUtc", "items", "state", "targetRevision", "snapshotUtc", "hasMore"],
        "compare_metric_windows" => ["complete"],
        "get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" => ["items", "snapshotUtc", "hasMore"],
        "get_blocking_history" => ["items", "fromUtc", "toUtc", "snapshotUtc", "hasMore"],
        "get_deadlock" => ["targetId", "eventId", "occurredAtUtc", "fingerprint", "participants", "relations", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"],
        "search_deadlocks" => ["items", "snapshotUtc", "hasMore"],
        "get_top_queries" or "get_query_history" => ["items", "snapshotUtc", "hasMore"],
        "get_query_plan_metadata" => ["targetId", "plan", "source", "observedAtUtc", "coverage", "contentAvailable"],
        "get_database_health" or "get_file_io" => ["targetId", "items", "collector", "repositoryTimeUtc", "hasMore"],
        "get_tempdb_health" or "get_backup_status" or "get_job_failures" => ["items", "state", "snapshotUtc", "hasMore"],
        "get_storage_forecast" => ["targetId", "metricKey", "horizon", "items", "state", "targetRevision", "snapshotUtc", "hasMore"],
        "get_availability_health" => ["items", "state", "visibilityScope", "snapshotUtc", "truncated"],
        "get_incident_evidence" => ["targetId", "threadId", "items", "generations", "targetRevision", "snapshotUtc", "hasMore"],
        "search_diagnostic_events" => ["targetId", "fromUtc", "toUtc", "items", "hasMore", "targetRevision", "snapshotUtc"],
        _ => Array.Empty<string>()
    };

    public static JsonObject NestedObjectSchema(string tool, string path)
    {
        if (tool == "get_availability_health" && path == "items")
            return new JsonObject { ["oneOf"] = new JsonArray(AvailabilityItemSchema("replica"), AvailabilityItemSchema("database")) };
        var properties = new JsonObject();
        foreach (string field in NestedFields(tool, path)) properties[field] = SchemaForField(tool, path, field);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        string[] required = RequiredFields(tool, path);
        if (required.Length > 0) schema["required"] = JsonSerializer.SerializeToNode(required);
        return schema;
    }

    private static JsonObject AvailabilityItemSchema(string kind)
    {
        string[] fields = kind == "replica"
            ? ["kind", "groupFingerprint", "replicaFingerprint", "role", "operationalState", "connectedState", "visibilityScope", "stateAvailable"]
            : ["kind", "groupFingerprint", "databaseFingerprint", "synchronizationState", "databaseState", "visibilityScope", "stateAvailable"];
        var properties = new JsonObject();
        foreach (string field in fields) properties[field] = SchemaForField("get_availability_health", "items", field);
        properties["kind"] = new JsonObject { ["type"] = "string", ["const"] = kind };
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(fields), ["additionalProperties"] = false
        };
    }

    public static JsonObject SchemaForField(string tool, string path, string field)
    {
        if (tool == "list_incidents")
        {
            if (field is "publicationRevision" or "generationCount") return new JsonObject { ["type"] = "integer", ["minimum"] = 0 };
            if (path == "items" && field == "latestGenerationObservedAtUtc") return new JsonObject { ["type"] = new JsonArray("string", "null"), ["format"] = "date-time" };
            if (path == "items" && field == "threadId") return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            if (field is "openedAtUtc" or "fromUtc" or "toUtc" or "snapshotUtc") return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }
        if (tool == "list_metric_catalog")
        {
            if (path.Length == 0 && field == "version") return new JsonObject { ["type"] = "integer", ["minimum"] = 1 };
            if (path.Length == 0 && field == "checksum") return new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" };
            if (path == "items" && field == "source") return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("host", "replication") };
            if (path == "items" && field == "aggregation") return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("gauge", "sum") };
            if (path == "items" && field == "dimensionKeys") return new JsonObject
            {
                ["type"] = "array", ["uniqueItems"] = true,
                ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 }
            };
            if (path == "items" && field is "metricKey" or "displayName" or "unit") return new JsonObject { ["type"] = "string", ["minLength"] = 1 };
        }
        if (path == "dimensions" && field == "value") return new JsonObject { ["type"] = "string" };
        if (tool == "get_wait_summary" && path == "items" &&
            field is "waitingTasksDelta" or "waitTimeMillisecondsDelta" or "signalWaitTimeMillisecondsDelta")
            return new JsonObject
            {
                ["type"] = new JsonArray("string", "null"), ["pattern"] = "^(0|[1-9][0-9]*)$",
                ["description"] = "Non-negative decimal-string change since the preceding sampled baseline; null when a baseline is unavailable or a reset was detected."
            };
        if (tool == "get_wait_summary" && path == "items" &&
            field is "waitingTasksCount" or "waitTimeMilliseconds" or "signalWaitTimeMilliseconds")
            return new JsonObject { ["type"] = "string", ["description"] = "Cumulative counter total; use the corresponding delta for change since the preceding sample." };
        if (tool == "get_wait_summary" && path == "items" && field == "maximumWaitTimeMilliseconds")
            return new JsonObject { ["type"] = "string", ["description"] = "Maximum single-wait duration reported by the cumulative source counter, in milliseconds; no delta is available." };
        if ((tool is "get_blocking_chain" or "get_blocking_history") && path == "items")
        {
            if (field is "blockerSessionId" or "rootBlockerSessionId")
                return new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 1, ["maximum"] = 32_767 };
            if (field == "chainDepth") return new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = BlockingChainLimits.MaximumDepth };
            if (field == "chainState") return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("resolved", "cycle", "depthLimit", "externalBlocker") };
        }
        if (tool == "get_backup_status" && path == "items")
        {
            if (field == "sizeBytes") return new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 0 };
            if (field == "databaseFingerprint") return new JsonObject { ["type"] = "string", ["description"] = "Opaque database fingerprint; not a database name." };
            if (field == "sourceTimeUnknown") return new JsonObject { ["type"] = "boolean", ["description"] = "True when the backup source timestamp cannot be resolved to UTC." };
        }
        if (tool == "get_job_failures" && path == "items")
        {
            if (field == "jobId") return new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "SQL Agent job identifier; not a job name." };
            if (field == "firstObservedAtUtc") return new JsonObject { ["type"] = "string", ["description"] = "Repository UTC first-observed time; not the source job execution time." };
            if (field == "failureFingerprint") return new JsonObject { ["type"] = "string", ["description"] = "Opaque failure fingerprint; not a message or error reason." };
        }
        if (tool == "get_file_io" && path == "observation" && field is "readStallMilliseconds" or "writeStallMilliseconds")
            return new JsonObject { ["type"] = new JsonArray("integer", "null"), ["minimum"] = 0, ["description"] = "Cumulative directional I/O stall milliseconds; null when the historical split was not collected. Not per-operation latency." };
        if (tool == "get_file_io" && path == "observation" && field == "ioStallMilliseconds")
            return new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Cumulative read and write I/O stall time in milliseconds; not per-operation latency." };
        if (tool == "get_availability_health")
        {
            if (field == "visibilityScope") return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("primaryAllKnown", "secondaryLocalOnly", "resolvingLocalOnly") };
            if (field == "stateAvailable") return new JsonObject { ["type"] = "boolean" };
        }
        // Activity counters are deliberately emitted as invariant strings by
        // the application contracts (the values can exceed JSON number
        // precision in long-running servers). Keep the schema exact for both
        // session and request snapshots; query metrics remain integers below.
        if (path == "items" && (tool is "get_active_sessions" or "get_active_requests") &&
            (field is "cpuMilliseconds" or "memoryUsagePages" or "reads" or "writes" or "logicalReads" or "totalElapsedMilliseconds" or "rowCount"))
            return new JsonObject { ["type"] = "string" };
        if (path == "items" && (tool is "get_wait_summary" or "get_blocking_chain" or "get_blocking_history") &&
            (field is "waitingTasksCount" or "waitTimeMilliseconds" or "maximumWaitTimeMilliseconds" or "signalWaitTimeMilliseconds" or "waitingTaskCount" or "waitDurationMilliseconds"))
            return new JsonObject { ["type"] = "string" };
        if (path == "items" && (tool is "get_database_health" or "get_file_io") && (field is "collector" or "observation"))
            return NestedObjectSchema(tool, field);
        if (path == "items" && tool == "get_blocking_history" && field == "evidence")
            return NestedObjectSchema(tool, field);
        if (path.Length == 0 && ToolPathFields.TryGetValue(tool, out Dictionary<string, HashSet<string>>? paths) && paths.ContainsKey(field))
        {
            JsonObject nested = NestedObjectSchema(tool, field);
            if (field is "capabilities") nested["type"] = new JsonArray("object", "null");
            return nested;
        }
        if (field is "items" or "targets" or "participants" or "relations" or "generations")
            return new JsonObject { ["type"] = "array", ["items"] = NestedObjectSchema(tool, field) };
        if (field is "dimensions")
            return new JsonObject { ["type"] = "array", ["items"] = NestedObjectSchema(tool, field) };
        if (field is "query" or "plan" or "metrics" or "safeMetadata" or "observation" or "loss")
        {
            JsonObject nested = NestedObjectSchema(tool, field);
            if (field is "plan" or "capabilities") nested["type"] = new JsonArray("object", "null");
            return nested;
        }
        string type = field switch
        {
            "revision" or "targetRevision" or "snapshotTargetRevision" or "sessionId" or "requestId" or "openTransactionCount" or "participantCount" or "relationCount" or "severity" or "databaseId" or "fileId" or "blockedSessionId" or "blockerSessionId" or "waiterSessionId" or "waitingTasksCount" or "waitingTaskCount" or "waitTimeMilliseconds" or "maximumWaitTimeMilliseconds" or "signalWaitTimeMilliseconds" or "waitDurationMilliseconds" or "sizeBytes" or "usedBytes" or "freeBytes" or "sizePages" or "usedPages" or "freePages" or "sourceGeneration" or "generation" or "leftSamples" or "rightSamples" or "cpuMilliseconds" or "durationMilliseconds" or "executions" or "logicalReads" or "writes" or "rows" or "readOperations" or "writeOperations" or "readBytes" or "writeBytes" => "integer",
            "minimumLostItems" or "minimumLostBytes" => "integer",
            "value" or "confidence" or "residual" or "estimate" or "lowerBound" or "upperBound" or "slopePerDay" or "percentComplete" or "latencyMilliseconds" or "horizon" or "leftValue" or "rightValue" or "delta" or "percent" => "number",
            "hasMore" or "isVictim" or "isUserProcess" or "baselineAvailable" or "resetDetected" or "fresh" or "truncated" or "contentAvailable" or "acknowledged" or "supersedesPrevious" or "deliverySuppressed" or "parseTruncated" or "complete" or "countIsExact" => "boolean",
            _ => "string"
        };
        bool nullable = field is "retiredAtUtc" or "firedUtc" or "acknowledgedUtc" or "sourceRunId" or "evidencePacketId" or "collectionRunId" or "planFingerprint" or "forecastId" or "reason" or "value" or "lowerBound" or "upperBound" or "slopePerDay"
            || (tool == "get_backup_status" && path == "items" && field == "backupAtUtc");
        return nullable ? new JsonObject { ["type"] = new JsonArray(type, "null") } : new JsonObject { ["type"] = type };
    }

    private static IReadOnlyCollection<string> NestedFields(string tool, string path) =>
        ToolPathFields.TryGetValue(tool, out Dictionary<string, HashSet<string>>? paths) && paths.TryGetValue(path, out HashSet<string>? fields)
            ? fields : Array.Empty<string>();

    private static string[] RequiredFields(string tool, string path) => (tool, path) switch
    {
        ("list_metric_catalog", "items") => ["metricKey", "displayName", "unit", "source", "aggregation", "dimensionKeys"],
        ("list_incidents", "items") => ["threadId", "openedAtUtc", "latestGenerationObservedAtUtc", "generationCount"],
        ("list_instances", "targets") => ["targetId", "key", "displayName", "lifecycle", "revision", "createdAtUtc", "discoveryRequestedAtUtc", "updatedAtUtc"],
        ("get_instance_capabilities", "capabilities") => ["state", "status", "reason", "revision", "observedAtUtc"],
        ("get_instance_health", "coreCollector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "evidence") => ["targetId", "runId", "targetRevision", "outcome", "reason", "completedAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "loss") => ["kind", "minimumLostItems", "countIsExact", "minimumLostBytes"],
        ("get_top_queries" or "get_query_history", "query") => ["databaseId", "queryFingerprint"],
        ("get_top_queries", "plan") or ("get_query_plan_metadata", "plan") => ["databaseId", "queryFingerprint", "planFingerprint"],
        ("get_database_health" or "get_file_io", "collector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc"],
        ("get_database_health", "observation") => ["databaseId", "databaseName", "state", "observedAtUtc"],
        ("get_file_io", "observation") => ["databaseId", "fileId", "fileName", "sizeBytes", "readOperations", "writeOperations", "readBytes", "writeBytes", "ioStallMilliseconds", "readStallMilliseconds", "writeStallMilliseconds", "observedAtUtc"],
        ("get_metric_series" or "get_storage_forecast", "dimensions") => ["key", "value"],
        ("get_deadlock", "participants") => ["sessionId", "isVictim"],
        ("get_deadlock", "relations") => ["blockerSessionId", "waiterSessionId", "resourceCategory", "lockMode"],
        ("get_incident_evidence", "generations") => ["threadId", "generation", "observedAtUtc", "correlationSha256", "supersedesPrevious"],
        ("search_diagnostic_events", "safeMetadata") => [],
        ("get_active_alerts", "items") => ["alertId", "ruleId", "targetId", "ruleName", "state", "firstObservedUtc", "deliverySuppressed"],
        ("get_metric_series", "items") => ["observedAtUtc", "value", "dimensions"],
        ("get_wait_summary", "items") => ["waitType", "waitingTasksCount", "waitTimeMilliseconds", "maximumWaitTimeMilliseconds", "signalWaitTimeMilliseconds", "waitingTasksDelta", "waitTimeMillisecondsDelta", "signalWaitTimeMillisecondsDelta", "baselineAvailable", "resetDetected", "observedAtUtc"],
        ("get_active_sessions", "items") => ["sessionId", "status", "isUserProcess", "openTransactionCount", "cpuMilliseconds", "memoryUsagePages", "reads", "writes", "logicalReads", "totalElapsedMilliseconds", "observedAtUtc"],
        ("get_active_requests", "items") => ["sessionId", "requestId", "status", "command", "cpuMilliseconds", "totalElapsedMilliseconds", "reads", "writes", "logicalReads", "rowCount", "percentComplete", "observedAtUtc"],
        ("get_blocking_chain", "items") => ["blockedSessionId", "blockerKind", "blockerSessionId", "rootBlockerSessionId", "chainDepth", "chainState", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        ("get_blocking_history", "items") => ["evidence", "blockedSessionId", "blockerKind", "blockerSessionId", "rootBlockerSessionId", "chainDepth", "chainState", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        ("search_deadlocks", "items") => ["targetId", "eventId", "occurredAtUtc", "fingerprint", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"],
        ("get_top_queries", "items") => ["targetId", "query", "source", "sourceState", "metric", "semantics", "intervalStartUtc", "intervalEndUtc", "coverage", "fresh", "truncated", "contentAvailable"],
        ("get_query_history", "items") => ["targetId", "query", "source", "sourceState", "metrics", "semantics", "intervalStartUtc", "intervalEndUtc", "resetDetected", "fresh", "truncated", "contentAvailable", "coverage"],
        ("get_database_health" or "get_file_io", "items") => ["observation", "collector"],
        ("get_tempdb_health", "items") => ["fileId", "sizeBytes", "usedBytes", "freeBytes", "state"],
        ("get_storage_forecast", "items") => ["metricKey", "horizonStartUtc", "horizonEndUtc", "confidence", "residual", "model", "sourceGeneration", "visibilityState", "dimensionsSha256"],
        ("get_backup_status", "items") => ["backupType", "backupAtUtc", "state", "databaseFingerprint", "sizeBytes", "sourceTimeUnknown"],
        ("get_job_failures", "items") => ["jobId", "firstObservedAtUtc", "state", "failureFingerprint"],
        ("get_availability_health", "items") => ["kind", "groupFingerprint", "visibilityScope", "stateAvailable"],
        ("get_incident_evidence", "items") => ["occurredAtUtc", "packetId", "evidenceKind", "sourceDigest", "identityDigest", "sourceCutoffDigest", "confidence", "visibilityState"],
        ("search_diagnostic_events", "items") => ["occurredAtUtc", "eventId", "eventKind", "severity", "safeMetadata", "collectedAtUtc", "targetRevision"],
        _ => Array.Empty<string>()
    };

    private static void ProjectNode(JsonNode? node, string tool, HashSet<string> fields, string path, bool root = false)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
            {
                // Sensitive names are listed explicitly as a belt-and-braces
                // guard even if a future common-field edit accidentally includes one.
                IReadOnlyCollection<string> allowed = root ? fields : NestedFields(tool, path);
                if (!allowed.Contains(property.Key) || IsSensitive(property.Key)) obj.Remove(property.Key);
                else if (IsValueObject(property.Key, property.Value)) obj[property.Key] = ((JsonObject)property.Value!)["value"]!.DeepClone();
                else ProjectNode(property.Value, tool, fields, property.Key, root: false);
            }
        }
        else if (node is JsonArray array) foreach (JsonNode? item in array) ProjectNode(item, tool, fields, path, root: false);
    }

    private static bool IsSensitive(string name) => name.Equals("connectionPolicy", StringComparison.OrdinalIgnoreCase)
        || name.Equals("endpoint", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || name.Equals("queryText", StringComparison.OrdinalIgnoreCase)
        || name.Equals("querySql", StringComparison.OrdinalIgnoreCase)
        || name.Equals("planXml", StringComparison.OrdinalIgnoreCase)
        || name.Equals("planText", StringComparison.OrdinalIgnoreCase)
        || name.Equals("providerError", StringComparison.OrdinalIgnoreCase)
        || name.Equals("physicalPath", StringComparison.OrdinalIgnoreCase)
        || name.Equals("jobCommand", StringComparison.OrdinalIgnoreCase)
        || name.Equals("commandText", StringComparison.OrdinalIgnoreCase)
        || name.Equals("rawJson", StringComparison.OrdinalIgnoreCase)
        || name.Equals("rawPayload", StringComparison.OrdinalIgnoreCase)
        || name.Equals("message", StringComparison.OrdinalIgnoreCase);

    private static bool IsValueObject(string name, JsonNode? value) =>
        name is "targetId" or "instanceId" or "key" or "displayName" or "revision" or "targetRevision" or "databaseId" or "queryFingerprint" or "planFingerprint"
        && value is JsonObject obj && obj.Count == 1 && obj["value"] is JsonValue;
}
