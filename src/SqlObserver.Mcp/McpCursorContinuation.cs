using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.Mcp;

/// <summary>Authenticated MCP-only context; application cursor contracts remain unchanged.</summary>
internal sealed class McpCursorContinuation
{
    internal const string WindowProperty = "__mcpWindow";
    private readonly JsonElement payload;

    public McpCursorContinuation(string tool, JsonElement authenticatedPayload)
    {
        if (authenticatedPayload.TryGetProperty(WindowProperty, out JsonElement context))
        {
            if (!NeedsPrivateWindow(tool) || context.ValueKind != JsonValueKind.Object || context.EnumerateObject().Count() != 2)
                throw new ArgumentException("Cursor window context is invalid.");
            Window = McpCursorWindow.Parse(context);
            JsonObject stripped = JsonNode.Parse(authenticatedPayload.GetRawText())!.AsObject();
            stripped.Remove(WindowProperty);
            payload = JsonSerializer.SerializeToElement(stripped);
        }
        else
        {
            payload = authenticatedPayload;
            if (tool is "search_diagnostic_events" or "search_deadlocks" or "get_top_queries" or "get_query_history" or "get_blocking_history")
                Window = McpCursorWindow.Parse(payload);
        }
    }

    public McpCursorWindow? Window { get; }
    public static bool NeedsPrivateWindow(string tool) => tool is "get_metric_series" or "get_job_failures";
    public static bool HasPagedWindow(string tool) => NeedsPrivateWindow(tool) || tool is "search_diagnostic_events" or "search_deadlocks" or "get_top_queries" or "get_query_history" or "get_blocking_history";

    public T Read<T>(JsonSerializerOptions options) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(payload, options) ?? throw new ArgumentException("Cursor is invalid."); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException or InvalidOperationException)
        { throw new McpInputValidationException(McpInputValidation.InvalidCursor); }
    }
}

internal sealed record McpCursorWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public static McpCursorWindow Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("fromUtc", out JsonElement from) || !value.TryGetProperty("toUtc", out JsonElement to)
            || from.ValueKind != JsonValueKind.String || to.ValueKind != JsonValueKind.String
            || !from.TryGetDateTimeOffset(out DateTimeOffset fromUtc) || !to.TryGetDateTimeOffset(out DateTimeOffset toUtc)
            || fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("Cursor window is invalid.");
        return new McpCursorWindow(fromUtc, toUtc);
    }

    public JsonObject ToJson() => new() { ["fromUtc"] = FromUtc.ToString("O"), ["toUtc"] = ToUtc.ToString("O") };
}
