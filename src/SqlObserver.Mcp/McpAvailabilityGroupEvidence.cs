using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Mcp;

/// <summary>Closed AG v2 wire fields: no application object is exposed wholesale.</summary>
internal static class McpAvailabilityGroupEvidence
{
    internal const string ReplicasTool = "get_availability_replicas_v2";
    internal const string DatabasesTool = "get_availability_databases_v2";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    internal static bool IsTool(string tool) => tool is ReplicasTool or DatabasesTool;
    internal static readonly string[] RootFields = ["schemaVersion", "targetId", "atUtc", "targetRevision", "runId",
        "sourceObservedAtUtc", "completedAtUtc", "state", "evidenceStatus", "sourceRows", "outputItems",
        "retainedItems", "lossKind", "truncated", "collectorEnabled", "items", "hasMore", "nextCursor"];
    private static readonly string[] IdentityFields = ["groupFingerprint", "replicaFingerprint", "observerReplicaFingerprint",
        "replicaIsLocal", "visibilityScope", "stateObserved"];
    internal static string[] ItemFields(string tool) => tool == ReplicasTool
        ? [.. IdentityFields, "role", "operationalState", "connectedState"]
        : [.. IdentityFields, "databaseFingerprint", "synchronizationState", "synchronizationHealth", "databaseState",
            "isSuspended", "logSendQueueKb", "logSendRateKbPerSecond", "redoQueueKb", "redoRateKbPerSecond", "sourceLastCommitLocal"];

    internal static JsonObject Map<T>(AvailabilityGroupEvidencePage<T> page, Func<T, JsonObject> map)
    {
        AvailabilityGroupEvidenceHeader header = page.Header;
        var result = new JsonObject
        {
            ["schemaVersion"] = 2, ["targetId"] = header.TargetId.Value.ToString("D"), ["atUtc"] = Utc(header.AtUtc),
            ["targetRevision"] = header.TargetRevision, ["runId"] = header.RunId?.ToString("D"),
            ["sourceObservedAtUtc"] = Utc(header.SourceObservedAtUtc), ["completedAtUtc"] = Utc(header.CompletedAtUtc),
            ["state"] = Camel(header.State), ["evidenceStatus"] = Camel(header.EvidenceStatus),
            ["sourceRows"] = header.SourceRows, ["outputItems"] = header.OutputItems, ["retainedItems"] = header.RetainedItems,
            ["lossKind"] = Camel(header.LossKind), ["truncated"] = header.Truncated, ["collectorEnabled"] = header.CollectorEnabled,
            ["items"] = new JsonArray(page.Items.Select(item => (JsonNode)map(item)).ToArray()),
            ["hasMore"] = page.NextCursor is not null,
        };
        if (page.NextCursor is not null) result["nextCursor"] = JsonSerializer.SerializeToNode(page.NextCursor, Options);
        return result;
    }

    private static string Camel<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string? Utc(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static JsonObject Identity(AvailabilityReplicaEvidenceIdentity identity, bool observed) => new()
    {
        ["groupFingerprint"] = identity.GroupFingerprint, ["replicaFingerprint"] = identity.ReplicaFingerprint,
        ["observerReplicaFingerprint"] = identity.ObserverReplicaFingerprint, ["replicaIsLocal"] = identity.ReplicaIsLocal,
        ["visibilityScope"] = Camel(identity.VisibilityScope), ["stateObserved"] = observed,
    };

    internal static JsonObject Replica(AvailabilityReplicaEvidence item)
    {
        JsonObject result = Identity(item.Identity, item.StateObserved);
        result["role"] = item.Role; result["operationalState"] = item.OperationalState; result["connectedState"] = item.ConnectedState;
        return result;
    }

    internal static JsonObject Database(AvailabilityDatabaseReplicaEvidence item)
    {
        JsonObject result = Identity(item.Identity, item.StateObserved);
        result["databaseFingerprint"] = item.DatabaseFingerprint;
        result["synchronizationState"] = item.SynchronizationState; result["synchronizationHealth"] = item.SynchronizationHealth;
        result["databaseState"] = item.DatabaseState; result["isSuspended"] = item.IsSuspended;
        result["logSendQueueKb"] = item.LogSendQueueKb; result["logSendRateKbPerSecond"] = item.LogSendRateKbPerSecond;
        result["redoQueueKb"] = item.RedoQueueKb; result["redoRateKbPerSecond"] = item.RedoRateKbPerSecond;
        result["sourceLastCommitLocal"] = item.SourceLastCommitLocal?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return result;
    }

    internal static void Project(string tool, JsonNode? node)
    {
        if (node is not JsonObject root) return;
        foreach (string field in root.Select(pair => pair.Key).ToArray())
            if (!RootFields.Contains(field, StringComparer.Ordinal) ||
                field != "items" && root[field] is JsonObject or JsonArray) root.Remove(field);
        if (root["items"] is JsonArray items)
        {
            string[] allowed = ItemFields(tool);
            foreach (JsonObject item in items.OfType<JsonObject>())
                foreach (string field in item.Select(pair => pair.Key).ToArray())
                    if (!allowed.Contains(field, StringComparer.Ordinal) || item[field] is JsonObject or JsonArray) item.Remove(field);
        }
    }

    internal static JsonObject ItemSchema(string tool)
    {
        string[] fields = ItemFields(tool);
        var properties = new JsonObject();
        foreach (string field in fields) properties[field] = FieldSchema("items", field);
        return new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(fields), ["additionalProperties"] = false };
    }

    internal static JsonObject FieldSchema(string path, string field)
    {
        if (field is "targetId" or "runId") return StringSchema(field == "runId", format: "uuid");
        if (field is "atUtc" or "sourceObservedAtUtc" or "completedAtUtc") return StringSchema(field != "atUtc", format: "date-time");
        if (field.EndsWith("Fingerprint", StringComparison.Ordinal))
            return StringSchema(field == "observerReplicaFingerprint", pattern: "^[0-9a-f]{64}$");
        if (field == "sourceLastCommitLocal") return StringSchema(true,
            pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}$");
        if (field is "truncated" or "collectorEnabled" or "hasMore" or "stateObserved") return Type("boolean");
        if (field is "replicaIsLocal" or "isSuspended") return Type("boolean", true);
        if (field is "schemaVersion" or "targetRevision" or "sourceRows" or "outputItems" or "retainedItems" ||
            field is "logSendQueueKb" or "logSendRateKbPerSecond" or "redoQueueKb" or "redoRateKbPerSecond")
        {
            JsonObject schema = Type("integer", path == "items");
            schema["minimum"] = field is "schemaVersion" or "targetRevision" ? 1 : 0;
            schema["maximum"] = field is "schemaVersion" ? 2L : field is "sourceRows" ? 2049L :
                field is "outputItems" or "retainedItems" ? 2048L : long.MaxValue;
            if (field == "schemaVersion") schema["const"] = 2;
            return schema;
        }
        return field switch
        {
            "state" => Tokens(false, "complete", "partial", "degraded", "unsupported", "permissionDenied", "noData"),
            "evidenceStatus" => Tokens(false, "notCollected", "collectionFailed", "evidenceNotRetained", "withheldByByteLimit", "noData", "retained"),
            "lossKind" => Tokens(false, "none", "sourceRowLimit", "responseByteLimit", "outputValidationFailure", "ingestionRejection", "visibilityIncomplete"),
            "visibilityScope" => Tokens(false, "primaryAllKnown", "secondaryLocalOnly", "resolvingLocalOnly"),
            "role" => Tokens(true, "PRIMARY", "SECONDARY", "RESOLVING"),
            "operationalState" => Tokens(true, "PENDING_FAILOVER", "PENDING", "ONLINE", "OFFLINE", "FAILED", "FAILED_NO_QUORUM"),
            "connectedState" => Tokens(true, "CONNECTED", "DISCONNECTED"),
            "synchronizationState" => Tokens(true, "NOT SYNCHRONIZING", "SYNCHRONIZING", "SYNCHRONIZED", "REVERTING", "INITIALIZING"),
            "synchronizationHealth" => Tokens(true, "NOT_HEALTHY", "PARTIALLY_HEALTHY", "HEALTHY"),
            "databaseState" => Tokens(true, "ONLINE", "RESTORING", "RECOVERING", "RECOVERY_PENDING", "SUSPECT", "EMERGENCY", "OFFLINE"),
            _ => throw new ArgumentException("Unknown AG v2 MCP wire field.", nameof(field)),
        };
    }

    private static JsonObject Type(string type, bool nullable = false) => new()
    {
        ["type"] = nullable ? new JsonArray(type, "null") : JsonValue.Create(type),
    };
    private static JsonObject StringSchema(bool nullable, string? format = null, string? pattern = null)
    {
        JsonObject schema = Type("string", nullable);
        if (format is not null) schema["format"] = format;
        if (pattern is not null) schema["pattern"] = pattern;
        return schema;
    }
    private static JsonObject Tokens(bool nullable, params string[] values)
    {
        JsonObject schema = Type("string", nullable);
        var tokens = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        if (nullable) tokens.Add((JsonNode?)null);
        schema["enum"] = tokens;
        return schema;
    }
}
