using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Mcp;

/// <summary>Explicit backup v2 assessment wire fields; application objects never become public wholesale.</summary>
internal static class McpBackupAssessments
{
    internal const string Tool = "get_backup_assessments_v2";
    internal const int MaximumPageSize = 20;
    private static readonly JsonSerializerOptions CursorOptions = new(JsonSerializerDefaults.Web);
    internal static readonly string[] RootFields = ["contractVersion", "targetId", "atUtc", "targetRevision", "runId",
        "sourceObservedAtUtc", "completedAtUtc", "receivedAtUtc", "sourceZoneId", "historyLowerBoundLocal", "coverage",
        "evidenceStatus", "sourceRows", "outputItems", "retainedItems", "databaseCount", "collectorEnabled", "items", "hasMore", "nextCursor"];

    private static readonly JsonObject DatabaseSchema = BuildDatabaseSchema();
    private static readonly JsonObject RootSchema = BuildRootSchema();
    internal static JsonObject ItemSchema() => (JsonObject)DatabaseSchema.DeepClone();
    internal static JsonObject FieldSchema(string field) => (JsonObject)RootSchema["properties"]![field]!.DeepClone();

    internal static JsonObject Map(BackupAssessmentPage page)
    {
        BackupEvidenceHeader h = page.Header;
        var root = new JsonObject
        {
            ["contractVersion"] = 2, ["targetId"] = h.TargetId.Value.ToString("D"), ["atUtc"] = Utc(h.AtUtc),
            ["targetRevision"] = h.TargetRevision, ["runId"] = h.RunId?.ToString("D"),
            ["sourceObservedAtUtc"] = Utc(h.SourceObservedAtUtc), ["completedAtUtc"] = Utc(h.CompletedAtUtc), ["receivedAtUtc"] = Utc(h.ReceivedAtUtc),
            ["sourceZoneId"] = h.SourceZoneId, ["historyLowerBoundLocal"] = Local(h.HistoryLowerBoundLocal),
            ["coverage"] = Name(h.Coverage), ["evidenceStatus"] = Name(h.EvidenceStatus),
            ["sourceRows"] = h.SourceRows, ["outputItems"] = h.OutputItems, ["retainedItems"] = h.RetainedItems,
            ["databaseCount"] = h.DatabaseCount, ["collectorEnabled"] = h.CollectorEnabled,
            ["items"] = new JsonArray(page.Items.Select(item => (JsonNode)Database(item)).ToArray()), ["hasMore"] = page.NextCursor is not null,
        };
        if (page.NextCursor is not null) root["nextCursor"] = JsonSerializer.SerializeToNode(page.NextCursor, CursorOptions);
        return root;
    }

    private static JsonObject Database(BackupDatabaseAssessment item)
    {
        var e = item.Source.Evidence; var p = item.Source.Policy;
        return new JsonObject
        {
            ["databaseOrdinal"] = item.Source.DatabaseOrdinal, ["databaseFingerprint"] = e.DatabaseFingerprint,
            ["familyFingerprint"] = e.FamilyFingerprint, ["recoveryForkFingerprint"] = e.RecoveryForkFingerprint,
            ["groupFingerprint"] = e.GroupFingerprint, ["agDatabaseFingerprint"] = e.AgDatabaseFingerprint, ["localReplicaFingerprint"] = e.LocalReplicaFingerprint,
            ["recoveryModel"] = Name(e.RecoveryModel), ["catalogDatabaseState"] = e.CatalogDatabaseState,
            ["scope"] = Name(e.Scope), ["localReplicaRole"] = e.LocalReplicaRole, ["localReplicaPreferred"] = e.LocalReplicaPreferred,
            ["unclassifiedBackupSeen"] = e.UnclassifiedBackupSeen, ["coverage"] = Name(e.Coverage), ["policyRevision"] = item.PolicyRevision,
            ["policy"] = p is null ? null : new JsonObject
            {
                ["enabled"] = p.Enabled, ["maximumFullAgeSeconds"] = (int)p.MaximumFullAge.TotalSeconds,
                ["maximumLogAgeSeconds"] = p.MaximumLogAge is { } log ? (int?)log.TotalSeconds : null,
                ["maximumObservationAgeSeconds"] = (int)p.MaximumObservationAge.TotalSeconds,
                ["fullRequirement"] = Name(p.FullRequirement), ["requireChecksums"] = p.RequireChecksums,
            },
            ["full"] = Check(item.Full), ["log"] = Check(item.Log),
            ["observedIssues"] = new JsonArray(item.ObservedIssues.Select(issue => (JsonNode)new JsonObject
            { ["kind"] = Name(issue.Kind), ["copyOnly"] = issue.CopyOnly, ["reason"] = Name(issue.Reason) }).ToArray()),
            ["samples"] = new JsonArray(e.Samples.Select((sample, index) => (JsonNode)new JsonObject
            {
                ["kind"] = Name(sample.Freshness.Kind), ["copyOnly"] = sample.Freshness.CopyOnly,
                ["recordedFinishUtc"] = Utc(sample.Freshness.FinishUtc), ["sourceLocalFinish"] = Local(sample.Freshness.SourceLocalFinish),
                ["sourceLocalStart"] = Local(sample.SourceLocalStart), ["recordedStartOffsetMinutes"] = sample.RecordedStartOffsetMinutes,
                ["sizeBytes"] = sample.SizeBytes?.ToString(CultureInfo.InvariantCulture), ["hasChecksum"] = sample.Freshness.HasChecksum,
                ["isDamaged"] = sample.Freshness.IsDamaged, ["metadataComplete"] = sample.Freshness.MetadataComplete,
                ["firstRecoveryForkFingerprint"] = sample.FirstRecoveryForkFingerprint, ["lastRecoveryForkFingerprint"] = sample.LastRecoveryForkFingerprint,
                ["timeState"] = Name(sample.TimeResolution.State), ["qualificationRevision"] = sample.TimeResolution.QualificationRevision,
                ["qualificationTrusted"] = item.Source.QualificationTrusted[index],
            }).ToArray()),
        };
    }

    private static JsonObject Check(BackupFreshnessCheck check) => new()
    {
        ["status"] = Name(check.Status), ["reason"] = Name(check.Reason), ["maximumAgeSeconds"] = check.MaximumAge?.TotalSeconds,
        ["observedAgeSeconds"] = check.ObservedAge?.TotalSeconds,
        ["selectedBackup"] = check.SelectedBackup is not { } b ? null : new JsonObject
        {
            ["kind"] = Name(b.Kind), ["copyOnly"] = b.CopyOnly, ["finishUtc"] = Utc(b.FinishUtc), ["sourceLocalFinish"] = Local(b.SourceLocalFinish),
            ["hasChecksum"] = b.HasChecksum, ["isDamaged"] = b.IsDamaged, ["metadataComplete"] = b.MetadataComplete,
        },
    };
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string? Utc(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static string? Local(DateTime? value) => value?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static JsonObject BuildRootSchema() => Object(false,
        ("contractVersion", Integer(2, 2)), ("targetId", Text(false, format: "uuid")), ("atUtc", Text(false, format: "date-time")),
        ("targetRevision", Integer(1, long.MaxValue)), ("runId", Text(true, format: "uuid")),
        ("sourceObservedAtUtc", Text(true, format: "date-time")), ("completedAtUtc", Text(true, format: "date-time")), ("receivedAtUtc", Text(true, format: "date-time")),
        ("sourceZoneId", Text(true, maximum: 128)), ("historyLowerBoundLocal", LocalSchema()), ("coverage", Tokens<BackupFreshnessCoverage>()),
        ("evidenceStatus", Tokens<BackupEvidenceStatus>()), ("sourceRows", Integer(0, 3073)), ("outputItems", Integer(0, 3072)),
        ("retainedItems", Integer(0, 3072)), ("databaseCount", Integer(0, 512)), ("collectorEnabled", Type("boolean")),
        ("items", Array(DatabaseSchema, MaximumPageSize)), ("hasMore", Type("boolean")));

    private static JsonObject BuildDatabaseSchema()
    {
        JsonObject selected = Object(true, ("kind", Tokens<BackupKind>()), ("copyOnly", Type("boolean")),
            ("finishUtc", Text(true, format: "date-time")), ("sourceLocalFinish", LocalSchema()),
            ("hasChecksum", Type("boolean", true)), ("isDamaged", Type("boolean", true)), ("metadataComplete", Type("boolean", true)));
        JsonObject check = Object(false, ("status", Tokens<BackupFreshnessStatus>()), ("reason", Tokens<BackupFreshnessReason>()),
            ("maximumAgeSeconds", Number(true)), ("observedAgeSeconds", Number(true)), ("selectedBackup", selected));
        JsonObject sample = Object(false, ("kind", Tokens<BackupKind>()), ("copyOnly", Type("boolean")), ("recordedFinishUtc", Text(true, format: "date-time")),
            ("sourceLocalFinish", LocalSchema()), ("sourceLocalStart", LocalSchema()), ("recordedStartOffsetMinutes", Integer(-840, 840, true)),
            ("sizeBytes", Text(true, maximum: 19, pattern: "^(0|[1-9][0-9]{0,18})$")),
            ("hasChecksum", Type("boolean", true)), ("isDamaged", Type("boolean", true)), ("metadataComplete", Type("boolean", true)),
            ("firstRecoveryForkFingerprint", Fingerprint()), ("lastRecoveryForkFingerprint", Fingerprint(false)),
            ("timeState", Tokens<BackupFinishTimeState>()), ("qualificationRevision", Integer(1, long.MaxValue, true)), ("qualificationTrusted", Type("boolean")));
        JsonObject policy = Object(true, ("enabled", Type("boolean")), ("maximumFullAgeSeconds", Integer(1, int.MaxValue)),
            ("maximumLogAgeSeconds", Integer(1, int.MaxValue, true)), ("maximumObservationAgeSeconds", Integer(1, 3600)),
            ("fullRequirement", Tokens<FullBackupRequirement>()), ("requireChecksums", Type("boolean")));
        JsonObject issue = Object(false, ("kind", Tokens<BackupKind>()), ("copyOnly", Type("boolean")), ("reason", Tokens<BackupFreshnessReason>()));
        return Object(false, ("databaseOrdinal", Integer(1, 512)), ("databaseFingerprint", Fingerprint()), ("familyFingerprint", Fingerprint()),
            ("recoveryForkFingerprint", Fingerprint()), ("groupFingerprint", Fingerprint()), ("agDatabaseFingerprint", Fingerprint()), ("localReplicaFingerprint", Fingerprint()),
            ("recoveryModel", Tokens<BackupRecoveryModel>()), ("catalogDatabaseState", Text(true, maximum: 60)), ("scope", Tokens<BackupFreshnessScope>()),
            ("localReplicaRole", Text(true, maximum: 60)), ("localReplicaPreferred", Type("boolean", true)), ("unclassifiedBackupSeen", Type("boolean")),
            ("coverage", Tokens<BackupFreshnessCoverage>()), ("policyRevision", Integer(1, long.MaxValue, true)), ("policy", policy),
            ("full", check), ("log", (JsonObject)check.DeepClone()), ("observedIssues", Array(issue, 2 * BackupFreshnessEvidence.MaximumSamples)),
            ("samples", Array(sample, BackupFreshnessEvidence.MaximumSamples)));
    }

    private static JsonObject Type(string name, bool nullable = false) => new() { ["type"] = nullable ? new JsonArray(name, "null") : JsonValue.Create(name) };
    private static JsonObject Integer(long minimum, long maximum, bool nullable = false)
    { JsonObject s = Type("integer", nullable); s["minimum"] = minimum; s["maximum"] = maximum; return s; }
    private static JsonObject Number(bool nullable)
    { JsonObject s = Type("number", nullable); s["minimum"] = 0; return s; }
    private static JsonObject Text(bool nullable, string? format = null, int? maximum = null, string? pattern = null)
    { JsonObject s = Type("string", nullable); if (format is not null) s["format"] = format; if (maximum.HasValue) s["maxLength"] = maximum.Value; if (pattern is not null) s["pattern"] = pattern; return s; }
    private static JsonObject Fingerprint(bool nullable = true) => Text(nullable, pattern: "^[0-9a-f]{64}$");
    private static JsonObject LocalSchema() => Text(true, pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{6}$");
    private static JsonObject Tokens<T>() where T : struct, Enum => new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetValues<T>().Select(v => (JsonNode)JsonValue.Create(Name(v))!).ToArray()) };
    private static JsonObject Array(JsonObject item, int maximum) => new() { ["type"] = "array", ["items"] = item.DeepClone(), ["maxItems"] = maximum };
    private static JsonObject Object(bool nullable, params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = nullable ? new JsonArray("object", "null") : JsonValue.Create("object"),
        ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema.DeepClone()))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()), ["additionalProperties"] = false,
    };

    internal static void Project(JsonNode? node) => ProjectNode(node, RootSchema, root: true);
    private static void ProjectNode(JsonNode? node, JsonObject schema, bool root = false)
    {
        if (node is JsonObject obj && schema["properties"] is JsonObject fields)
            foreach (string name in obj.Select(p => p.Key).ToArray())
            {
                if (root && name == "nextCursor" && obj[name] is JsonValue) continue;
                if (fields[name] is not JsonObject child || obj[name] is JsonObject && child["properties"] is not JsonObject ||
                    obj[name] is JsonArray && child["items"] is not JsonObject) obj.Remove(name);
                else ProjectNode(obj[name], child);
            }
        else if (node is JsonArray array && schema["items"] is JsonObject item)
            foreach (JsonNode? child in array) ProjectNode(child, item);
    }
}
