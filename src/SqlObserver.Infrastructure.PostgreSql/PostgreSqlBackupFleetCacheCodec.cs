using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

internal static class PostgreSqlBackupFleetCacheCodec
{
    private const int MaximumBytes = 16384;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, IgnoreReadOnlyProperties = true,
        MaxDepth = 16, Converters = { new TargetConverter() },
    };
    public static string Encode(BackupFleetSummary summary)
    {
        BackupFleetSummaryContract.Validate(summary);
        string json = JsonSerializer.Serialize(new Document(1, summary), Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid();
        return json;
    }
    public static BackupFleetSummary Decode(string json, MonitoredInstanceId target, long revision)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw Invalid();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Keys(root, "schemaVersion", "summary");
            JsonElement summary = root.GetProperty("summary");
            Keys(summary, "context", "validUntilUtc", "status", "full", "log", "observedFaultDatabases");
            Keys(summary.GetProperty("context"), "targetId", "atUtc", "targetRevision", "runId", "sourceObservedAtUtc", "completedAtUtc",
                "receivedAtUtc", "sourceZoneId", "historyLowerBoundLocal", "coverage", "evidenceStatus", "sourceRows", "outputItems", "retainedItems",
                "databaseCount", "collectorEnabled", "policyHistoryCount", "qualificationCommandRevision");
            foreach (string check in new[] { "full", "log" })
            {
                JsonElement counts = summary.GetProperty(check);
                Keys(counts, "withinThreshold", "policyViolated", "indeterminate", "notRequired", "disabled", "unknownReasons");
                JsonElement reasons = counts.GetProperty("unknownReasons");
                if (reasons.ValueKind != JsonValueKind.Array || reasons.GetArrayLength() > 25) throw Invalid();
                foreach (JsonElement reason in reasons.EnumerateArray()) Keys(reason, "reason", "count");
            }
            Document decoded = JsonSerializer.Deserialize<Document>(json, Options) ?? throw Invalid();
            if (decoded.SchemaVersion != 1 || decoded.Summary?.Context is not { } context || context.TargetId != target || context.TargetRevision != revision) throw Invalid();
            BackupFleetSummaryContract.Validate(decoded.Summary);
            return decoded.Summary with { Full = Own(decoded.Summary.Full), Log = Own(decoded.Summary.Log) };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("The cached backup aggregate could not be decoded.", exception); }
    }
    private static BackupFleetCheckCounts Own(BackupFleetCheckCounts counts) => counts with { UnknownReasons = Array.AsReadOnly(counts.UnknownReasons.ToArray()) };
    private static void Keys(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Invalid();
        if (seen.Count != required.Length) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The cached backup aggregate has an invalid closed schema.");
    private sealed record Document(int SchemaVersion, BackupFleetSummary Summary);
    private sealed class TargetConverter : JsonConverter<MonitoredInstanceId>
    {
        public override MonitoredInstanceId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty) throw new JsonException("Invalid backup target.");
            return new(id);
        }
        public override void Write(Utf8JsonWriter writer, MonitoredInstanceId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
