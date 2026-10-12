using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;

namespace SqlObserver.Mcp;

/// <summary>Closed, explicit v2 SQL discovery and baseline projections.</summary>
internal static class McpSqlMetricBaselines
{
    internal const string CatalogTool = "list_sql_metric_catalog_v2";
    internal const string BaselineTool = "get_sql_metric_baselines_v2";
    internal static bool IsTool(string tool) => tool is CatalogTool or BaselineTool;
    private static readonly AnalyticsMetricDefinition[] Definitions = MetricCatalogV2.All
        .Where(d => d.Source == AnalyticsMetricSource.SqlEngine).OrderBy(d => d.Metric.Key, StringComparer.Ordinal).ToArray();
    private static readonly JsonObject CatalogSchema = BuildCatalogSchema();
    private static readonly JsonObject BaselineSchema = BuildBaselineSchema();

    internal static string InputSchema(string tool)
    {
        JsonObject properties = new();
        if (tool == BaselineTool)
        {
            properties["instanceId"] = Text(format: "uuid");
            properties["targetRevision"] = Integer(1, long.MaxValue);
            properties["metricKey"] = Tokens(Definitions.Select(d => d.Metric.Key).ToArray());
            properties["fromUtc"] = Text(format: "date-time"); properties["toUtc"] = Text(format: "date-time");
            properties["snapshotUtc"] = Text(format: "date-time");
            properties["generation"] = Integer(1, long.MaxValue); properties["generation"]!["default"] = 1;
            foreach (var field in properties) field.Value!["description"] = McpCatalogDescriptions.Parameter(tool, field.Key);
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false,
            ["required"] = tool == BaselineTool ? new JsonArray("instanceId", "targetRevision", "metricKey", "fromUtc", "toUtc") : new JsonArray(),
        }.ToJsonString();
    }

    internal static JsonElement OutputSchema(string tool)
    {
        JsonObject schema = Object(("data", tool == CatalogTool ? CatalogSchema : BaselineSchema));
        using JsonDocument document = JsonDocument.Parse(schema.ToJsonString()); return document.RootElement.Clone();
    }

    internal static JsonObject Catalog(AuthorizationContext authorization)
    {
        if (!(authorization.HasRole(ApplicationRole.Viewer) || authorization.HasRole(ApplicationRole.Operator) ||
            authorization.HasRole(ApplicationRole.TargetAdministrator))) throw new UnauthorizedAccessException();
        return new JsonObject
        {
            ["catalogVersion"] = MetricCatalogV2.Version, ["catalogChecksum"] = MetricCatalogV2.Checksum,
            ["baselineTool"] = BaselineTool, ["algorithmVersion"] = "sql-baseline-v2", ["sourceAlgorithmVersion"] = "sql-rollup-v2",
            ["sourceWindowDays"] = 28, ["hourCount"] = 168,
            ["items"] = new JsonArray(Definitions.Select(d => (JsonNode)new JsonObject
            {
                ["metricKey"] = d.Metric.Key, ["displayName"] = d.Metric.DisplayName, ["unit"] = d.Metric.Unit,
                ["source"] = "sqlEngine", ["derivationVersion"] = d.DerivationVersion,
                ["requiresSameEngineEpoch"] = d.RequiresSameEngineEpoch, ["requiresStableSchedulerCount"] = d.RequiresStableSchedulerCount,
                ["maximumSampleGapSeconds"] = d.MaximumSampleGap?.TotalSeconds,
            }).ToArray()),
        };
    }

    internal static JsonObject Map(SqlMetricBaselinePage page) => new()
    {
        ["targetId"] = page.TargetId.ToString("D"), ["targetRevision"] = page.TargetRevision,
        ["metricKey"] = page.MetricKey, ["unit"] = page.Unit, ["generation"] = page.Generation,
        ["fromUtc"] = Utc(page.FromUtc), ["toUtc"] = Utc(page.ToUtc), ["snapshotUtc"] = Utc(page.SnapshotUtc),
        ["catalogVersion"] = page.CatalogVersion, ["catalogChecksum"] = page.CatalogChecksum,
        ["algorithmVersion"] = page.AlgorithmVersion, ["sourceAlgorithmVersion"] = page.SourceAlgorithmVersion, ["state"] = page.State,
        ["items"] = new JsonArray(page.Items.OrderBy(row => row.Statistics.HourOfWeek).Select(row =>
        {
            BaselineResult s = row.Statistics;
            return (JsonNode)new JsonObject
            {
                ["hourOfWeek"] = s.HourOfWeek, ["completeDays"] = s.CompleteDays, ["sampleCount"] = s.SampleCount,
                ["median"] = s.Median, ["mad"] = s.Mad, ["p10"] = s.P10, ["p90"] = s.P90,
                ["coverage"] = s.Coverage, ["confidence"] = s.Confidence, ["visibilityState"] = s.VisibilityState,
                ["sourceCutoffUtc"] = Utc(row.SourceCutoffUtc),
            };
        }).ToArray()),
    };
    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject BuildCatalogSchema() => Object(
        ("catalogVersion", Integer(2, 2)), ("catalogChecksum", Tokens(MetricCatalogV2.Checksum)),
        ("baselineTool", Tokens(BaselineTool)), ("algorithmVersion", Tokens("sql-baseline-v2")), ("sourceAlgorithmVersion", Tokens("sql-rollup-v2")),
        ("sourceWindowDays", Integer(28, 28)), ("hourCount", Integer(168, 168)),
        ("items", Array(Object(("metricKey", Tokens(Definitions.Select(d => d.Metric.Key).ToArray())), ("displayName", Text()),
            ("unit", Tokens("count", "bytes", "percent", "batches_per_second")), ("source", Tokens("sqlEngine")),
            ("derivationVersion", Tokens("direct-gauge-v1", "core-batch-rate-v1", "core-scheduler-cpu-v1")),
            ("requiresSameEngineEpoch", Type("boolean")), ("requiresStableSchedulerCount", Type("boolean")),
            ("maximumSampleGapSeconds", Number(120, true))), 7, 7)));

    private static JsonObject BuildBaselineSchema() => Object(
        ("targetId", Text(format: "uuid")), ("targetRevision", Integer(1, long.MaxValue)),
        ("metricKey", Tokens(Definitions.Select(d => d.Metric.Key).ToArray())), ("unit", Tokens("count", "bytes", "percent", "batches_per_second")),
        ("generation", Integer(1, long.MaxValue)), ("fromUtc", Text(format: "date-time")), ("toUtc", Text(format: "date-time")),
        ("snapshotUtc", Text(format: "date-time")), ("catalogVersion", Integer(2, 2)), ("catalogChecksum", Tokens(MetricCatalogV2.Checksum)),
        ("algorithmVersion", Tokens("sql-baseline-v2")), ("sourceAlgorithmVersion", Tokens("sql-rollup-v2")),
        ("state", Tokens("no_data", "unavailable", "partial", "complete")),
        ("items", Array(Object(("hourOfWeek", Integer(0, 167)), ("completeDays", Integer(0, 28)), ("sampleCount", Integer(0, 4)),
            ("median", Number(nullable: true)), ("mad", Number(nullable: true)), ("p10", Number(nullable: true)), ("p90", Number(nullable: true)),
            ("coverage", Number(1)), ("confidence", Number(.5)), ("visibilityState", Tokens("unavailable", "partial", "complete")),
            ("sourceCutoffUtc", Text(format: "date-time"))), 0, 168)));

    private static JsonObject Type(string type, bool nullable = false) => new() { ["type"] = nullable ? new JsonArray(type, "null") : JsonValue.Create(type) };
    private static JsonObject Text(string? format = null) { var s = Type("string"); if (format is not null) s["format"] = format; return s; }
    private static JsonObject Integer(long min, long max) { var s = Type("integer"); s["minimum"] = min; s["maximum"] = max; return s; }
    private static JsonObject Number(double? maximum = null, bool nullable = false)
    { var s = Type("number", nullable); s["minimum"] = 0; if (maximum.HasValue) s["maximum"] = maximum.Value; return s; }
    private static JsonObject Tokens(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()) };
    private static JsonObject Array(JsonObject item, int min, int max) => new() { ["type"] = "array", ["items"] = item, ["minItems"] = min, ["maxItems"] = max };
    private static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema.DeepClone()))),
        ["required"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f.Name)!).ToArray()), ["additionalProperties"] = false,
    };

    internal static IReadOnlyCollection<string> RootFields(string tool) => ((JsonObject)(tool == CatalogTool ? CatalogSchema : BaselineSchema)["properties"]!).Select(f => f.Key).ToArray();
    internal static void Project(string tool, JsonNode? node) => ProjectNode(node, tool == CatalogTool ? CatalogSchema : BaselineSchema);
    private static void ProjectNode(JsonNode? node, JsonObject schema)
    {
        if (node is JsonObject obj && schema["properties"] is JsonObject fields)
            foreach (string name in obj.Select(p => p.Key).ToArray())
            {
                if (fields[name] is not JsonObject child || obj[name] is JsonObject && child["properties"] is not JsonObject ||
                    obj[name] is JsonArray && child["items"] is not JsonObject) obj.Remove(name);
                else ProjectNode(obj[name], child);
            }
        else if (node is JsonArray array && schema["items"] is JsonObject item)
            foreach (JsonNode? child in array) ProjectNode(child, item);
    }
}
