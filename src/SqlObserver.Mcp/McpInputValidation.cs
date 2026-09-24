using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.Mcp;

// Only catalog-owned field names and fixed reasons enter these messages.
// Downstream ArgumentException messages must never be returned to clients.
internal sealed class McpInputValidationException(string message) : ArgumentException(message);

internal static class McpInputValidation
{
    internal const string InvalidCursor = "cursor is invalid or does not match this tool and its original arguments.";

    internal static void ValidateArguments(string name, IDictionary<string, JsonElement> args)
    {
        using JsonDocument schema = JsonDocument.Parse(McpCatalog.Definitions.Single(d => d.Name == name).InputSchemaJson);
        JsonElement root = schema.RootElement;
        JsonElement properties = root.GetProperty("properties");
        if (args.Keys.Any(key => !properties.TryGetProperty(key, out _)))
            throw new McpInputValidationException("The request contains an unsupported argument.");
        if (root.TryGetProperty("required", out JsonElement requiredArray))
            foreach (JsonElement required in requiredArray.EnumerateArray())
                if (!args.TryGetValue(required.GetString()!, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new McpInputValidationException($"{required.GetString()} is required.");
        foreach ((string key, JsonElement value) in args)
        {
            JsonElement property = properties.GetProperty(key);
            string type = property.GetProperty("type").GetString()!;
            if (type == "integer")
            {
                int minimum = property.GetProperty("minimum").GetInt32();
                int maximum = property.GetProperty("maximum").GetInt32();
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int integer) || integer < minimum || integer > maximum)
                    throw new McpInputValidationException($"{key} must be an integer from {minimum} to {maximum}.");
                continue;
            }
            if (value.ValueKind != JsonValueKind.String)
                throw new McpInputValidationException($"{key} must be a string.");
            string text = value.GetString()!;
            if (property.TryGetProperty("format", out JsonElement format))
            {
                if (format.GetString() == "uuid" && (!Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty))
                    throw new McpInputValidationException($"{key} must be a non-empty UUID.");
                if (format.GetString() == "date-time") ParseUtc(text, key);
            }
            if (property.TryGetProperty("enum", out JsonElement values) && !values.EnumerateArray().Any(v => v.GetString() == text))
                throw new McpInputValidationException(key == "metricKey"
                    ? "metricKey must be one of the advertised metric keys."
                    : "metric must be one of: cpuMilliseconds, durationMilliseconds, executions, logicalReads, writes, rows.");
            if (property.TryGetProperty("minLength", out JsonElement minLength) && text.Length < minLength.GetInt32() ||
                property.TryGetProperty("maxLength", out JsonElement maxLength) && text.Length > maxLength.GetInt32() ||
                property.TryGetProperty("pattern", out JsonElement pattern) && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant))
                throw new McpInputValidationException(key switch
                {
                    "cursor" => InvalidCursor,
                    "queryFingerprint" or "planFingerprint" => $"{key} must contain exactly 64 hexadecimal characters.",
                    "metricKey" => "metricKey must be one of the advertised metric keys.",
                    _ => $"{key} does not match its advertised format."
                });
        }
        if (name == "compare_metric_windows") ValidateComparison(args);
    }

    internal static DateTimeOffset ParseUtc(string text, string field)
    {
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (text.EndsWith(".Z", StringComparison.Ordinal) || !DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            throw new McpInputValidationException($"{field} must be an ISO 8601 UTC timestamp ending in Z.");
        return parsed;
    }

    internal static void ValidateWindow(string tool, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) throw new McpInputValidationException("toUtc must be later than fromUtc.");
        if (to - from > McpCatalog.WindowMaximum(tool))
            throw new McpInputValidationException($"fromUtc and toUtc must span no more than {McpCatalog.WindowLabel(tool)}.");
    }

    private static void ValidateComparison(IDictionary<string, JsonElement> args)
    {
        DateTimeOffset leftFrom = ParseUtc(args["leftFromUtc"].GetString()!, "leftFromUtc");
        DateTimeOffset leftTo = ParseUtc(args["leftToUtc"].GetString()!, "leftToUtc");
        DateTimeOffset rightFrom = ParseUtc(args["rightFromUtc"].GetString()!, "rightFromUtc");
        DateTimeOffset rightTo = ParseUtc(args["rightToUtc"].GetString()!, "rightToUtc");
        if (leftTo <= leftFrom || leftTo - leftFrom > TimeSpan.FromDays(31))
            throw new McpInputValidationException("leftFromUtc and leftToUtc must define a positive window of no more than 31 days.");
        if (rightTo <= rightFrom || rightTo - rightFrom > TimeSpan.FromDays(31))
            throw new McpInputValidationException("rightFromUtc and rightToUtc must define a positive window of no more than 31 days.");
        if (leftTo - leftFrom != rightTo - rightFrom || leftFrom < rightTo && rightFrom < leftTo)
            throw new McpInputValidationException("leftFromUtc/leftToUtc and rightFromUtc/rightToUtc must have equal durations and must not overlap.");
    }
}
