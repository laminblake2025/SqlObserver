using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Audit;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;
using SqlObserver.Security;

namespace SqlObserver.Mcp;

/// <summary>Identifies the MCP adapter assembly and its explicit protocol boundary.</summary>
public static class AssemblyMarker { }

/// <summary>Catalog metadata is embedded in source and hashed deterministically at startup.</summary>
public sealed record McpToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>Stable M11 catalog: additions require an intentional backlog/ADR change.</summary>
public static class McpCatalog
{
    public const string CurrentProtocolVersion = "2026-07-28";
    public const string DownlevelProtocolVersion = "2025-11-25";
    // Reviewed catalog approval point. Digest is re-derived below and must
    // agree, so changing the catalog cannot silently retain this identity.
    public const string ApprovedCatalogDigest = "2C2B4B9D35DC2958F9102CDEDE7AAF450A737DF6E582D0E51BEB2B26085B6529";
    public const string ServerVersion = "m11-2.2.0+catalog-2C2B4B9D35DC2958F9102CDEDE7AAF450A737DF6E582D0E51BEB2B26085B6529";
    private static readonly JsonSerializerOptions DigestJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly IReadOnlyList<McpToolDefinition> Definitions = new[]
    {
        "list_instances", "get_instance_capabilities", "get_instance_health", "get_active_alerts", "get_metric_series",
        "compare_metric_windows", "get_wait_summary", "get_active_sessions", "get_active_requests", "get_blocking_chain",
        "get_blocking_history", "get_deadlock", "search_deadlocks", "get_top_queries", "get_query_history",
        "get_query_plan_metadata", "get_database_health", "get_tempdb_health", "get_file_io", "get_storage_forecast",
        "get_backup_status", "get_job_failures", "get_availability_health", "get_incident_evidence", "search_diagnostic_events"
    }.Select(static name => new McpToolDefinition(name, "Read-only SQL Observer diagnostic projection.", SchemaFor(name))).ToArray();

    private static string SchemaFor(string name)
    {
        var properties = new JsonObject();
        void Add(string key, string json) => properties[key] = JsonNode.Parse(json);
        if (name != "list_instances") Add("instanceId", "{\"type\":\"string\",\"format\":\"uuid\"}");
        bool window = McpCursorContinuation.HasPagedWindow(name) || name == "compare_metric_windows";
        if (window) { Add("fromUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); Add("toUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); }
        // The combined AG view is a bounded summary. Replica/database child
        // streams have independent orderings, so one merged MCP cursor would
        // not be a truthful continuation contract.
        bool paged = name is not ("get_instance_health" or "get_instance_capabilities" or "get_deadlock" or "get_query_plan_metadata" or "compare_metric_windows" or "get_availability_health");
        if (paged) Add("limit", $"{{\"type\":\"integer\",\"minimum\":1,\"maximum\":{LimitMaximum(name)}}}");
        if (paged) Add("cursor", $$"""{"type":"string","minLength":1,"maxLength":{{McpCursorSigner.MaximumTokenLength}},"pattern":"^[A-Za-z0-9_-]{1,{{McpCursorSigner.MaximumTokenLength}}}$"}""");
        if (name is "get_metric_series" or "get_storage_forecast" or "compare_metric_windows") Add("metricKey", "{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}");
        if (name is "get_deadlock") Add("eventId", "{\"type\":\"string\",\"format\":\"uuid\"}");
        if (name is "get_incident_evidence") Add("threadId", "{\"type\":\"string\",\"format\":\"uuid\"}");
        if (name is "get_storage_forecast") Add("horizonDays", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":366}");
        if (name is "get_top_queries") Add("metric", "{\"type\":\"string\",\"enum\":[\"cpuMilliseconds\",\"durationMilliseconds\",\"executions\",\"logicalReads\",\"writes\",\"rows\"]}");
        if (name is "get_query_history" or "get_query_plan_metadata") { Add("databaseId", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":32767}"); Add("queryFingerprint", "{\"type\":\"string\",\"pattern\":\"^[0-9A-Fa-f]{64}$\"}"); }
        if (name is "get_query_plan_metadata") Add("planFingerprint", "{\"type\":\"string\",\"pattern\":\"^[0-9A-Fa-f]{64}$\"}");
        if (name is "compare_metric_windows") { Add("leftFromUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); Add("leftToUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); Add("rightFromUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); Add("rightToUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); }
        if (name is "compare_metric_windows") Add("limit", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}");
        var root = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        string[] required = name switch
        {
            "list_instances" => [],
            "get_deadlock" => ["instanceId", "eventId"],
            "get_incident_evidence" => ["instanceId", "threadId"],
            "get_metric_series" or "get_storage_forecast" => ["instanceId", "metricKey"],
            "compare_metric_windows" => ["instanceId", "metricKey", "leftFromUtc", "leftToUtc", "rightFromUtc", "rightToUtc"],
            "get_top_queries" => ["instanceId", "metric"],
            "get_query_history" => ["instanceId", "databaseId", "queryFingerprint"],
            "get_query_plan_metadata" => ["instanceId", "databaseId", "queryFingerprint", "planFingerprint"],
            _ => ["instanceId"]
        };
        if (required.Length > 0) root["required"] = JsonSerializer.SerializeToNode(required, DigestJsonOptions);
        return root.ToJsonString(DigestJsonOptions);
    }

    public static string Digest { get; } = GetApprovedDigest();

    private static string GetApprovedDigest()
    {
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(Definitions, DigestJsonOptions)));
        return digest == ApprovedCatalogDigest ? digest : throw new InvalidOperationException($"The MCP catalog digest is not the approved identity: {digest}.");
    }

    public static int LimitMaximum(string name) => name switch
    {
        "get_metric_series" or "compare_metric_windows" => 1000,
        "search_diagnostic_events" => 100,
        "get_top_queries" or "get_query_history" => 200,
        "get_storage_forecast" => 200,
        "get_incident_evidence" or "search_deadlocks" => 256,
        "list_instances" or "get_database_health" or "get_file_io" => 100,
        _ => 100
    };

    /// <summary>Closed common envelope for structured output. Text content retains the legacy direct projection shape.</summary>
    public static JsonElement OutputSchema(string toolName)
    {
        JsonObject dataProperties = new();
        foreach (string field in McpOutputProjection.RootFields(toolName))
        {
            dataProperties[field] = field is "items" or "targets" or "generations" or "participants" or "relations"
                ? new JsonObject { ["type"] = "array", ["items"] = McpOutputProjection.NestedObjectSchema(toolName, field) }
                : field is "nextCursor" ? new JsonObject { ["type"] = "string", ["maxLength"] = McpCursorSigner.MaximumTokenLength }
                : McpOutputProjection.SchemaForField(toolName, string.Empty, field);
        }
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    // A not-found projection is represented as null by the
                    // application query boundary; advertise that explicitly.
                    ["type"] = new JsonArray("object", "null"),
                    ["properties"] = dataProperties,
                    ["additionalProperties"] = false
                }
            },
            ["required"] = new JsonArray { "data" },
            ["additionalProperties"] = false
        };
        JsonObject dataSchema = (JsonObject)schema["properties"]!["data"]!;
        IReadOnlyCollection<string> requiredData = McpOutputProjection.RequiredRootFields(toolName);
        if (requiredData.Count > 0) dataSchema["required"] = JsonSerializer.SerializeToNode(requiredData, DigestJsonOptions);
        using JsonDocument document = JsonDocument.Parse(schema.ToJsonString(DigestJsonOptions));
        return document.RootElement.Clone();
    }

    public static JsonElement OutputSchema() => OutputSchema("list_instances");

    /// <summary>Explicit registration list; no assembly scanning is used.</summary>
    public static IReadOnlyList<McpServerTool> CreateTools()
    {
        return Definitions.Select(static definition => new CatalogTool(definition)).ToArray();
    }
}

/// <summary>Small SDK adapter that turns a catalog entry into an invocable MCP tool.</summary>
public sealed class CatalogTool(McpToolDefinition definition) : McpServerTool
{
    private readonly Tool _protocolTool = CreateProtocolTool(definition);

    public override Tool ProtocolTool => _protocolTool;

    public override IReadOnlyList<object> Metadata => Array.Empty<object>();

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IMcpCallHandler? handler = request.Services?.GetService(typeof(IMcpCallHandler)) as IMcpCallHandler;
        if (handler is null)
        {
            return InvokeUnavailableAsync(request.Services);
        }

        return handler.ExecuteAsync(definition.Name, request.Params?.Arguments, request.User ?? new ClaimsPrincipal(), cancellationToken);
    }

    private ValueTask<CallToolResult> InvokeUnavailableAsync(IServiceProvider? provider)
    {
        return CompleteUnavailableAsync(provider);
    }

    private async ValueTask<CallToolResult> CompleteUnavailableAsync(IServiceProvider? provider)
    {
        IMcpInvocationAuditService? audit = provider?.GetService<IMcpInvocationAuditService>();
        if (audit is null) return McpResults.Error("audit_unavailable", "The required invocation audit could not be recorded.");
        try
        {
            var record = new McpInvocationAuditRecord(Guid.NewGuid(), new McpClientIdentifier("anonymous"), new McpToolName(definition.Name), new McpActionName("mcp.tool." + definition.Name), McpAuthorizationResult.NotApplicable, McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure, new AuditCorrelationId(Guid.NewGuid()), McpParameterCanonicalizer.Digest(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>())), TimeSpan.Zero, 0);
            await audit.AppendTerminalAsync(record).ConfigureAwait(false);
            return McpResults.Error("service_unavailable", "The MCP query service is unavailable.");
        }
        catch { return McpResults.Error("audit_unavailable", "The required invocation audit could not be recorded."); }
    }

    private static Tool CreateProtocolTool(McpToolDefinition definition)
    {
        using JsonDocument document = JsonDocument.Parse(definition.InputSchemaJson);
        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = document.RootElement.Clone(),
            OutputSchema = McpCatalog.OutputSchema(definition.Name),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                Title = definition.Name
            }
        };
    }
}

public interface IMcpCallHandler
{
    ValueTask<CallToolResult> ExecuteAsync(string toolName, IDictionary<string, JsonElement>? arguments, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>Applies MCP-wide limits and delegates reads to application query services.</summary>
public sealed class McpCallHandler(IServiceProvider services, IHttpContextAccessor? httpContextAccessor = null) : IMcpCallHandler
{
    private static readonly SemaphoreSlim Global = new(32, 32);
    private static readonly ConcurrentDictionary<string, ActorGate> Actors = new(StringComparer.Ordinal);
    private static readonly object ActorRegistrySync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };
    private readonly IServiceProvider _services = services;

    private readonly record struct McpAuditSnapshot(JsonElement Parameters, Guid? TargetId, Guid? IncidentId);

    public async ValueTask<CallToolResult> ExecuteAsync(string toolName, IDictionary<string, JsonElement>? arguments, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        arguments ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        McpAuditSnapshot auditSnapshot = CaptureAuditSnapshot(toolName, arguments);
        bool known = McpCatalog.Definitions.Any(d => d.Name == toolName);
        if (!known)
        {
            CallToolResult unknown = McpResults.Error("unknown_tool", "The requested tool is not available.");
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.UnknownTool, McpInvocationAuditReason.UnknownTool, TimeSpan.Zero, unknown).ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            CallToolResult cancelled = McpResults.Error("request_cancelled", "The request was cancelled by the caller.");
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.Cancelled, McpInvocationAuditReason.Cancelled, TimeSpan.Zero, cancelled).ConfigureAwait(false);
        }
        try { ValidateRequest(arguments); ValidateArguments(toolName, arguments); }
        catch (ArgumentException)
        {
            CallToolResult invalid = McpResults.Error("invalid_request", "The request is invalid.");
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest, TimeSpan.Zero, invalid).ConfigureAwait(false);
        }

        string actor = user.FindFirstValue(ClaimTypes.PrimarySid) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        if (!TryEnterActor(actor, out ActorGate perActor))
        {
            CallToolResult limited = McpResults.Error("rate_limited", "The actor concurrency limit has been reached.");
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.Limited, McpInvocationAuditReason.ConcurrencyLimit, TimeSpan.Zero, limited).ConfigureAwait(false);
        }
        if (!await Global.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            ReleaseActor(actor, perActor);
            CallToolResult limited = McpResults.Error("rate_limited", "The server concurrency limit has been reached.");
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.Limited, McpInvocationAuditReason.ConcurrencyLimit, TimeSpan.Zero, limited).ConfigureAwait(false);
        }
        using CancellationTokenSource outer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        outer.CancelAfter(TimeSpan.FromSeconds(10));
        Stopwatch stopwatch = Stopwatch.StartNew();
        CallToolResult result;
        McpInvocationOutcome outcome = McpInvocationOutcome.Succeeded;
        McpInvocationAuditReason reason = McpInvocationAuditReason.Completed;
        try
        {
            result = await ExecuteBoundedAsync(toolName, arguments, user, outer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { outcome = McpInvocationOutcome.Cancelled; reason = McpInvocationAuditReason.Cancelled; result = McpResults.Error("request_cancelled", "The request was cancelled by the caller."); }
        catch (OperationCanceledException) { outcome = McpInvocationOutcome.Timeout; reason = McpInvocationAuditReason.Timeout; result = McpResults.Error("request_timed_out", "The request exceeded its execution limit."); }
        catch (McpResponseOversizeException) { outcome = McpInvocationOutcome.Oversize; reason = McpInvocationAuditReason.ResponseOversize; result = McpResults.Error("response_too_large", "The response exceeds its byte limit."); }
        catch (McpApplicationResponseOversizeException) { outcome = McpInvocationOutcome.Oversize; reason = McpInvocationAuditReason.ResponseOversize; result = McpResults.Error("response_too_large", "The response exceeds its byte limit."); }
        catch (UnauthorizedAccessException) { outcome = McpInvocationOutcome.Denied; reason = McpInvocationAuditReason.AuthorizationDenied; result = McpResults.Error("forbidden", "The caller is not authorized for this projection."); }
        catch (ArgumentException) { outcome = McpInvocationOutcome.Invalid; reason = McpInvocationAuditReason.InvalidRequest; result = McpResults.Error("invalid_request", "The request is invalid."); }
        catch (TimeoutException) { outcome = McpInvocationOutcome.Timeout; reason = McpInvocationAuditReason.Timeout; result = McpResults.Error("request_timed_out", "The request exceeded its execution limit."); }
        catch { outcome = McpInvocationOutcome.RepositoryFailure; reason = McpInvocationAuditReason.RepositoryFailure; result = McpResults.Error("request_failed", "The projection could not be completed."); }
        finally { Global.Release(); ReleaseActor(actor, perActor); }

        return await AuditOrWithholdAsync(toolName, auditSnapshot, user, outcome, reason, stopwatch.Elapsed, result).ConfigureAwait(false);
    }

    private async ValueTask<CallToolResult> AuditOrWithholdAsync(string toolName, McpAuditSnapshot auditSnapshot, ClaimsPrincipal user, McpInvocationOutcome outcome, McpInvocationAuditReason reason, TimeSpan duration, CallToolResult result)
    {
        if (TryDeferHttpAudit(toolName, auditSnapshot, user, outcome, reason, duration)) return result;
        try
        {
            if (httpContextAccessor?.HttpContext is HttpContext context) context.Items[McpHttpAuditBoundaryMiddleware.HandledItemKey] = true;
            await AppendAuditAsync(toolName, auditSnapshot, user, outcome, reason, duration, result).ConfigureAwait(false);
            return result;
        }
        catch { return McpResults.Error("audit_unavailable", "The required invocation audit could not be recorded."); }
    }

    private bool TryDeferHttpAudit(string toolName, McpAuditSnapshot snapshot, ClaimsPrincipal user, McpInvocationOutcome outcome, McpInvocationAuditReason reason, TimeSpan duration)
    {
        if (httpContextAccessor?.HttpContext is not HttpContext context || !context.Items.ContainsKey(McpHttpAuditBoundaryMiddleware.BoundaryActiveItemKey)) return false;
        string actor = user.FindFirstValue(ClaimTypes.PrimarySid) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        string safeTool = McpCatalog.Definitions.Any(d => d.Name == toolName) ? toolName : "unknown_tool";
        context.Items[McpHttpAuditBoundaryMiddleware.PendingAuditItemKey] = new McpPendingAudit(
            Guid.NewGuid(), actor, safeTool, McpParameterCanonicalizer.Digest(snapshot.Parameters), snapshot.TargetId, snapshot.IncidentId, outcome, reason, duration);
        context.Items[McpHttpAuditBoundaryMiddleware.HandledItemKey] = true;
        return true;
    }

    private async ValueTask AppendAuditAsync(string toolName, McpAuditSnapshot auditSnapshot, ClaimsPrincipal user, McpInvocationOutcome outcome, McpInvocationAuditReason reason, TimeSpan duration, CallToolResult result)
    {
        IMcpInvocationAuditService? audit = _services.GetService<IMcpInvocationAuditService>();
        if (audit is null)
        {
            IMcpInvocationAuditPort? port = _services.GetService<IMcpInvocationAuditPort>();
            if (port is null) throw new McpAuditUnavailableException(new InvalidOperationException("MCP audit service is not registered."));
            audit = new McpInvocationAuditService(port);
        }
        string actor = user.FindFirstValue(ClaimTypes.PrimarySid) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        string safeTool = McpCatalog.Definitions.Any(d => d.Name == toolName) ? toolName : "unknown_tool";
        long responseBytes = outcome == McpInvocationOutcome.Succeeded ? McpResults.DisclosedResponseBytes(result, JsonOptions) : 0;
        var record = new McpInvocationAuditRecord(Guid.NewGuid(), new McpClientIdentifier(actor), new McpToolName(safeTool), new McpActionName("mcp.tool." + safeTool), outcome == McpInvocationOutcome.Denied ? McpAuthorizationResult.Denied : outcome == McpInvocationOutcome.Succeeded || outcome == McpInvocationOutcome.Oversize || outcome == McpInvocationOutcome.Timeout || outcome == McpInvocationOutcome.Cancelled || outcome == McpInvocationOutcome.Limited || outcome == McpInvocationOutcome.RepositoryFailure ? McpAuthorizationResult.Allowed : McpAuthorizationResult.NotApplicable, outcome, reason, new AuditCorrelationId(Guid.NewGuid()), McpParameterCanonicalizer.Digest(auditSnapshot.Parameters), duration, responseBytes, auditSnapshot.TargetId.HasValue ? new MonitoredInstanceId(auditSnapshot.TargetId.Value) : null, auditSnapshot.IncidentId);
        await audit.AppendTerminalAsync(record).ConfigureAwait(false);
    }

    private static McpAuditSnapshot CaptureAuditSnapshot(string toolName, IDictionary<string, JsonElement> arguments)
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(arguments, JsonOptions).Clone();
        Guid? target = ReadAuditGuid(parameters, "instanceId");
        Guid? incident = toolName == "get_incident_evidence" ? ReadAuditGuid(parameters, "threadId") : null;
        return new McpAuditSnapshot(parameters, target, incident);
    }

    private static void ValidateArguments(string name, IDictionary<string, JsonElement> args)
    {
        using JsonDocument schema = JsonDocument.Parse(McpCatalog.Definitions.Single(d => d.Name == name).InputSchemaJson);
        JsonElement root = schema.RootElement;
        HashSet<string> allowed = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (args.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unsupported argument.");
        if (root.TryGetProperty("required", out JsonElement requiredArray))
            foreach (JsonElement required in requiredArray.EnumerateArray())
                if (!args.TryGetValue(required.GetString()!, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new ArgumentException("Required argument is missing.");
        foreach ((string key, JsonElement value) in args)
        {
            JsonElement property = root.GetProperty("properties").GetProperty(key);
            string type = property.GetProperty("type").GetString()!;
            if (type == "string" && value.ValueKind != JsonValueKind.String) throw new ArgumentException("String argument required.");
            if (type == "integer" && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsedInteger))) throw new ArgumentException("Integer argument required.");
            if (type == "integer")
            {
                int integerValue = value.GetInt32();
                if (property.TryGetProperty("minimum", out JsonElement minimum) && integerValue < minimum.GetInt32() || property.TryGetProperty("maximum", out JsonElement maximum) && integerValue > maximum.GetInt32()) throw new ArgumentException("Integer argument is outside its bounds.");
            }
            if (type == "string")
            {
                string text = value.GetString()!;
                if (property.TryGetProperty("minLength", out JsonElement minLength) && text.Length < minLength.GetInt32() || property.TryGetProperty("maxLength", out JsonElement maxLength) && text.Length > maxLength.GetInt32()) throw new ArgumentException("String argument is outside its bounds.");
                if (property.TryGetProperty("format", out JsonElement format) && format.GetString() == "date-time" && !DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out _)) throw new ArgumentException("Timestamp is invalid.");
                if (property.TryGetProperty("enum", out JsonElement values) && !values.EnumerateArray().Any(v => v.GetString() == text)) throw new ArgumentException("Value is not allowlisted.");
                if (property.TryGetProperty("pattern", out JsonElement pattern) && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern.GetString()!, System.Text.RegularExpressions.RegexOptions.CultureInvariant)) throw new ArgumentException("String argument does not match its required pattern.");
            }
        }
    }

    private static void ValidateRequest(IDictionary<string, JsonElement> args)
    {
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(args, JsonOptions);
        if (encoded.Length > 64 * 1024) throw new ArgumentException("The request exceeds its byte bound.");
        foreach (JsonElement value in args.Values) ValidateDepth(value, 1);
    }

    private static void ValidateDepth(JsonElement value, int depth)
    {
        if (depth > 32) throw new ArgumentException("The request nesting depth exceeds its bound.");
        if (value.ValueKind == JsonValueKind.Object) foreach (JsonProperty property in value.EnumerateObject()) ValidateDepth(property.Value, depth + 1);
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) ValidateDepth(item, depth + 1);
    }

    private async ValueTask<CallToolResult> ExecuteBoundedAsync(string name, IDictionary<string, JsonElement> args, ClaimsPrincipal user, CancellationToken token)
    {
        McpCursorSigner? signer = _services.GetService<McpCursorSigner>();
        WindowsGroupRoleResolver resolver = _services.GetService<WindowsGroupRoleResolver>() ?? throw new UnauthorizedAccessException();
        AuthorizationContext authorization = resolver.Resolve(user);
        Guid? id = ReadGuid(args, "instanceId");
        McpCursorContinuation? continuation = ReadContinuation(name, args, signer);
        DateTimeOffset? requestedFrom = ReadUtc(args, "fromUtc"), requestedTo = ReadUtc(args, "toUtc");
        McpCursorWindow? frozen = continuation?.Window;
        if (frozen is not null && (requestedFrom is not null && requestedFrom != frozen.FromUtc || requestedTo is not null && requestedTo != frozen.ToUtc))
            throw new ArgumentException("Cursor and explicit time window do not match.");
        // Old metric/raw cursors did not retain default bounds. Only an entirely
        // explicit request can safely continue one of those legacy tokens.
        if (continuation is not null && McpCursorContinuation.HasPagedWindow(name) && frozen is null && (requestedFrom is null || requestedTo is null))
            throw new ArgumentException("Cursor has no preserved time window; restart from the first page.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset to = requestedTo ?? frozen?.ToUtc ?? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        DateTimeOffset from = requestedFrom ?? frozen?.FromUtc ?? to.AddDays(-1);
        int limit = ReadLimit(name, args);
        string metric = args.TryGetValue("metricKey", out JsonElement metricValue) ? metricValue.GetString() ?? string.Empty : string.Empty;
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        bool hasWindow = McpCursorContinuation.HasPagedWindow(name) || name == "compare_metric_windows";
        if (hasWindow && (from >= to || to - from > TimeSpan.FromDays(31))) throw new ArgumentException("Invalid time window.");
        object? value = name switch
        {
            "list_instances" => await ListInstancesAsync(authorization, limit, continuation?.Read<ObservationTargetListCursor>(JsonOptions), timeout, token).ConfigureAwait(false),
            "get_instance_capabilities" when id.HasValue => await _services.GetRequiredService<IObservationTargetStatusQueryService>().GetAsync(new GetObservationTargetStatusQuery(authorization, new MonitoredInstanceId(id.Value), timeout), token).ConfigureAwait(false),
            "get_instance_health" when id.HasValue => await _services.GetRequiredService<IHealthProjectionQueryService>().GetInstanceAsync(new GetInstanceHealthQuery(authorization, new MonitoredInstanceId(id.Value), timeout), token).ConfigureAwait(false),
            "get_active_alerts" when id.HasValue => await _services.GetRequiredService<IAlertQueryService>().ListActivePageAsync(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<AlertActiveCursor>(JsonOptions), token).ConfigureAwait(false),
            "get_database_health" when id.HasValue => await _services.GetRequiredService<IHealthProjectionQueryService>().ListDatabasesAsync(new ListDatabaseHealthQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<DatabaseHealthCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_file_io" when id.HasValue => await _services.GetRequiredService<IHealthProjectionQueryService>().ListDatabaseFilesAsync(new ListDatabaseFileHealthQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<DatabaseFileHealthCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_tempdb_health" when id.HasValue => await _services.GetRequiredService<IOperationalHealthQueryService>().GetTempDbAsync(authorization, new OperationalHealthRequest(new MonitoredInstanceId(id.Value), from, to, limit, continuation?.Read<McpRawCursor>(JsonOptions).Value, timeout), token).ConfigureAwait(false),
            "get_backup_status" when id.HasValue => await _services.GetRequiredService<IOperationalHealthQueryService>().GetBackupsAsync(authorization, new OperationalHealthRequest(new MonitoredInstanceId(id.Value), from, to, limit, continuation?.Read<McpRawCursor>(JsonOptions).Value, timeout), token).ConfigureAwait(false),
            "get_job_failures" when id.HasValue => await _services.GetRequiredService<IOperationalHealthQueryService>().GetAgentFailuresAsync(authorization, new OperationalHealthRequest(new MonitoredInstanceId(id.Value), from, to, limit, continuation?.Read<McpRawCursor>(JsonOptions).Value, timeout), token).ConfigureAwait(false),
            "get_availability_health" when id.HasValue => await _services.GetRequiredService<IOperationalHealthQueryService>().GetAvailabilityGroupsAsync(authorization, new OperationalHealthRequest(new MonitoredInstanceId(id.Value), from, to, limit, null, timeout), token).ConfigureAwait(false),
            "get_wait_summary" when id.HasValue => await _services.GetRequiredService<IActivityProjectionQueryService>().ListWaitSummaryAsync(new ListServerWaitSummaryQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<ServerWaitSummaryCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_active_sessions" when id.HasValue => await _services.GetRequiredService<IActivityProjectionQueryService>().ListSessionsAsync(new ListActivitySessionsQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<ActivitySessionCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_active_requests" when id.HasValue => await _services.GetRequiredService<IActivityProjectionQueryService>().ListRequestsAsync(new ListActivityRequestsQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<ActivityRequestCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_blocking_chain" when id.HasValue => await _services.GetRequiredService<IActivityProjectionQueryService>().ListCurrentBlockingAsync(new ListCurrentBlockingQuery(authorization, new MonitoredInstanceId(id.Value), limit, continuation?.Read<BlockingEdgeCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_blocking_history" when id.HasValue => await _services.GetRequiredService<IActivityProjectionQueryService>().ListBlockingHistoryAsync(new ListBlockingHistoryQuery(authorization, new MonitoredInstanceId(id.Value), from, to, limit, continuation?.Read<BlockingHistoryCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "search_deadlocks" when id.HasValue => await _services.GetRequiredService<IDeadlockProjectionQueryService>().ListDeadlocksAsync(new ListDeadlocksQuery(authorization, new MonitoredInstanceId(id.Value), from, to, limit, continuation?.Read<DeadlockPageCursor>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_deadlock" when id.HasValue && ReadGuid(args, "eventId").HasValue => await _services.GetRequiredService<IDeadlockProjectionQueryService>().GetDeadlockAsync(authorization, new MonitoredInstanceId(id.Value), ReadGuid(args, "eventId")!.Value, timeout, token).ConfigureAwait(false),
            "get_metric_series" when id.HasValue => await _services.GetRequiredService<IMetricSeriesQueryService>().GetAsync(new MetricSeriesQuery(authorization, new MonitoredInstanceId(id.Value), metric, from, to, limit, timeout, Cursor: continuation?.Read<MetricSeriesCursor>(JsonOptions)), token).ConfigureAwait(false),
            "get_storage_forecast" when id.HasValue => await _services.GetRequiredService<IStorageForecastQueryService>().GetAsync(new StorageForecastQuery(authorization, new MonitoredInstanceId(id.Value), metric, TimeSpan.FromDays(ReadInt(args, "horizonDays", 30)), limit, timeout, Cursor: continuation?.Read<StorageForecastCursor>(JsonOptions)), token).ConfigureAwait(false),
            "search_diagnostic_events" when id.HasValue => await _services.GetRequiredService<IDiagnosticEventQueryService>().SearchAsync(new DiagnosticEventSearchQuery(authorization, new MonitoredInstanceId(id.Value), from, to, limit, timeout, Cursor: continuation?.Read<DiagnosticEventCursor>(JsonOptions)), token).ConfigureAwait(false),
            "get_incident_evidence" when id.HasValue && ReadGuid(args, "threadId").HasValue => await _services.GetRequiredService<IIncidentEvidenceQueryService>().GetAsync(new IncidentEvidenceQuery(authorization, new MonitoredInstanceId(id.Value), ReadGuid(args, "threadId")!.Value, limit, timeout, Cursor: continuation?.Read<IncidentEvidenceCursor>(JsonOptions)), token).ConfigureAwait(false),
            "compare_metric_windows" when id.HasValue => await _services.GetRequiredService<IAnalyticsQueryService>().CompareAsync(authorization, new AnalyticsQueryRequest(new MonitoredInstanceId(id.Value), metric, from, to, limit, timeout), ReadUtc(args, "leftFromUtc")!.Value, ReadUtc(args, "leftToUtc")!.Value, ReadUtc(args, "rightFromUtc")!.Value, ReadUtc(args, "rightToUtc")!.Value, token).ConfigureAwait(false),
            "get_top_queries" when id.HasValue => await _services.GetRequiredService<IQueryPerformanceApiQueryService>().GetTopAsync(authorization, new TopQueryRequest(new MonitoredInstanceId(id.Value), from, to, ReadMetric(args), limit, continuation?.Read<QueryPerformanceCursorEnvelope>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_query_history" when id.HasValue => await _services.GetRequiredService<IQueryPerformanceApiQueryService>().GetHistoryAsync(authorization, new QueryHistoryRequest(new MonitoredInstanceId(id.Value), new QueryOpaqueIdentity(ReadInt(args, "databaseId", 0), ReadString(args, "queryFingerprint")!), from, to, limit, continuation?.Read<QueryPerformanceCursorEnvelope>(JsonOptions), timeout), token).ConfigureAwait(false),
            "get_query_plan_metadata" when id.HasValue => await _services.GetRequiredService<IQueryPerformanceApiQueryService>().GetPlanAsync(authorization, new QueryPlanMetadataRequest(new MonitoredInstanceId(id.Value), new PlanOpaqueIdentity(new QueryOpaqueIdentity(ReadInt(args, "databaseId", 0), ReadString(args, "queryFingerprint")!), ReadString(args, "planFingerprint")!), timeout), token).ConfigureAwait(false),
            _ => throw new KeyNotFoundException("No handler is registered for this catalog tool.")
        };
        return McpResults.JsonWithWindow(value, JsonOptions, name, args, signer, McpCursorContinuation.NeedsPrivateWindow(name) ? new McpCursorWindow(from, to) : null);
    }

    private async ValueTask<object> ListInstancesAsync(AuthorizationContext authorization, int limit, ObservationTargetListCursor? cursor, RepositoryCallTimeout timeout, CancellationToken token)
    {
        ObservationTargetPage page = await _services.GetRequiredService<IObservationTargetQueryService>().ListAsync(new ListObservationTargetsQuery(authorization, limit, cursor, false, timeout), token).ConfigureAwait(false);
        return page;
    }

    private static Guid? ReadGuid(IDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid parsed) || parsed == Guid.Empty) throw new ArgumentException($"{name} must be a valid UUID.");
        return parsed;
    }
    private static Guid? ReadAuditGuid(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid parsed) && parsed != Guid.Empty ? parsed : null;
    private static Guid? ReadAuditGuid(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid parsed) && parsed != Guid.Empty ? parsed : null;
    private static DateTimeOffset? ReadUtc(IDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        DateTimeOffset parsed = DateTimeOffset.Parse(value.GetString() ?? string.Empty, null, System.Globalization.DateTimeStyles.RoundtripKind);
        if (parsed.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.");
        return parsed;
    }
    private static int ReadLimit(string name, IDictionary<string, JsonElement> args)
    {
        int maximum = McpCatalog.LimitMaximum(name);
        int value = args.TryGetValue("limit", out JsonElement element) ? element.GetInt32() : maximum;
        if (value is < 1 || value > maximum) throw new ArgumentOutOfRangeException(nameof(args));
        return value;
    }
    private static int ReadInt(IDictionary<string, JsonElement> args, string name, int fallback) => args.TryGetValue(name, out JsonElement value) ? value.GetInt32() : fallback;
    private static string? ReadString(IDictionary<string, JsonElement> args, string name) => args.TryGetValue(name, out JsonElement value) ? value.GetString() : null;
    private static McpCursorContinuation? ReadContinuation(string tool, IDictionary<string, JsonElement> args, McpCursorSigner? signer)
    {
        string? text = ReadString(args, "cursor");
        if (text is null) return null;
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Cursor is invalid.");
        if (signer is null) throw new InvalidOperationException("MCP cursor signing is not configured.");
        try { return new McpCursorContinuation(tool, signer.DecodePayload(text, tool, args)); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException) { throw new ArgumentException("Cursor is invalid.", exception); }
    }
    private static QueryPerformanceMetric ReadMetric(IDictionary<string, JsonElement> args) => ReadString(args, "metric") switch
    {
        "cpuMilliseconds" => QueryPerformanceMetric.CpuMilliseconds,
        "durationMilliseconds" => QueryPerformanceMetric.DurationMilliseconds,
        "executions" => QueryPerformanceMetric.Executions,
        "logicalReads" => QueryPerformanceMetric.LogicalReads,
        "writes" => QueryPerformanceMetric.Writes,
        "rows" => QueryPerformanceMetric.Rows,
        _ => throw new ArgumentException("Query metric is invalid.")
    };

    private static void ReleaseActor(string actor, ActorGate gate)
    {
        lock (ActorRegistrySync)
        {
            gate.Exit();
            if (gate.IsIdle) ((ICollection<KeyValuePair<string, ActorGate>>)Actors).Remove(new KeyValuePair<string, ActorGate>(actor, gate));
        }
    }

    private static bool TryEnterActor(string actor, out ActorGate gate)
    {
        lock (ActorRegistrySync)
        {
            gate = Actors.GetOrAdd(actor, static _ => new ActorGate());
            if (gate.TryEnter()) return true;
            gate = null!;
            return false;
        }
    }

    private sealed class ActorGate
    {
        private int active;
        public bool TryEnter() { if (active >= 4) return false; active++; return true; }
        public void Exit() { if (active <= 0) throw new InvalidOperationException("Actor gate underflow."); active--; }
        public bool IsIdle => active == 0;
    }
}

/// <summary>Adapter for legacy services whose continuation is an opaque string.</summary>
public sealed class McpRawCursor
{
    public McpRawCursor(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 4096) throw new ArgumentException("Cursor payload is outside its bound.", nameof(value));
        Value = value;
    }
    public string Value { get; }
}

/// <summary>Integrity-protected, bounded, request-bound cursor encoding.</summary>
public sealed class McpCursorSigner
{
    public const int MaximumTokenLength = 2048;
    private readonly byte[] key;
    public McpCursorSigner(byte[] key)
    {
        if (key is null || key.Length < 32 || key.Length > 4096) throw new ArgumentException("Cursor signing key must be between 32 and 4096 bytes.", nameof(key));
        this.key = key.ToArray();
    }
    public string Encode(JsonElement cursor, string tool, IDictionary<string, JsonElement> arguments)
    {
        JsonObject envelope = new() { ["v"] = 1, ["tool"] = tool, ["request"] = RequestDigest(arguments), ["payload"] = JsonNode.Parse(cursor.GetRawText()) };
        string signed = Canonical(envelope);
        envelope["tag"] = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signed))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical(envelope))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (token.Length is 0 or > MaximumTokenLength) throw new McpApplicationResponseOversizeException();
        return token;
    }
    public T Decode<T>(string token, string tool, IDictionary<string, JsonElement> arguments, JsonSerializerOptions options) where T : class
    {
        JsonElement payload = DecodePayload(token, tool, arguments);
        return JsonSerializer.Deserialize<T>(payload.GetRawText(), options) ?? throw new ArgumentException("Cursor is invalid.");
    }
    public void EncodeNextCursor(JsonNode? node, string tool, IDictionary<string, JsonElement> arguments)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
            {
                if (string.Equals(property.Key, "nextCursor", StringComparison.Ordinal) && property.Value is JsonObject)
                    obj[property.Key] = Encode(JsonSerializer.SerializeToElement(property.Value), tool, arguments);
                else if (string.Equals(property.Key, "nextCursor", StringComparison.Ordinal) && property.Value is JsonValue raw && raw.TryGetValue<string>(out string? rawCursor) && !string.IsNullOrWhiteSpace(rawCursor))
                    obj[property.Key] = Encode(JsonSerializer.SerializeToElement(new McpRawCursor(rawCursor)), tool, arguments);
                else EncodeNextCursor(property.Value, tool, arguments);
            }
        }
        else if (node is JsonArray array) foreach (JsonNode? item in array) EncodeNextCursor(item, tool, arguments);
    }
    internal JsonElement DecodePayload(string token, string tool, IDictionary<string, JsonElement> arguments)
    {
        if (token.Length is 0 or > MaximumTokenLength || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Cursor is invalid.");
        try
        {
            string padded = token.Replace('-', '+').Replace('_', '/') + new string('=', (4 - token.Length % 4) % 4);
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(padded), new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5 || !root.TryGetProperty("v", out JsonElement version) || version.GetInt32() != 1 || !root.TryGetProperty("tool", out JsonElement toolValue) || toolValue.GetString() != tool || !root.TryGetProperty("request", out JsonElement request) || request.GetString() != RequestDigest(arguments) || !root.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag", out JsonElement tagValue)) throw new ArgumentException("Cursor is invalid.");
            string tagText = tagValue.GetString() ?? string.Empty;
            string expected = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(new JsonObject { ["v"] = 1, ["tool"] = tool, ["request"] = request.GetString(), ["payload"] = JsonNode.Parse(payload.GetRawText()) })))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (tagText.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(tagText), Encoding.ASCII.GetBytes(expected))) throw new ArgumentException("Cursor is invalid.");
            return payload.Clone();
        }
        catch (ArgumentException) { throw; }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException) { throw new ArgumentException("Cursor is invalid.", exception); }
    }
    private static string RequestDigest(IDictionary<string, JsonElement> arguments)
    {
        var filtered = arguments.Where(static pair => !string.Equals(pair.Key, "cursor", StringComparison.Ordinal)).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        return McpParameterCanonicalizer.Digest(JsonSerializer.SerializeToElement(filtered)).ToString();
    }
    private static string Canonical(JsonObject value) => value.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}

public static class McpResults
{
    public static CallToolResult Error(string code, string message) => new() { IsError = true, Content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(new { code, message }) } } };
    public static CallToolResult Json(object? value, JsonSerializerOptions options, string? tool = null, IDictionary<string, JsonElement>? arguments = null, McpCursorSigner? signer = null)
    {
        if (value is JsonNode) throw new ArgumentException("Materialized wire JSON is test-only; use a typed application contract.", nameof(value));
        return JsonCore(value, options, tool, arguments, signer);
    }
    internal static CallToolResult JsonWire(JsonNode value, JsonSerializerOptions options, string tool, IDictionary<string, JsonElement>? arguments, McpCursorSigner signer) => JsonCore(value, options, tool, arguments, signer);
    internal static CallToolResult JsonWithWindow(object? value, JsonSerializerOptions options, string tool, IDictionary<string, JsonElement> arguments, McpCursorSigner? signer, McpCursorWindow? window) => JsonCore(value, options, tool, arguments, signer, window);
    private static CallToolResult JsonCore(object? value, JsonSerializerOptions options, string? tool = null, IDictionary<string, JsonElement>? arguments = null, McpCursorSigner? signer = null, McpCursorWindow? window = null)
    {
        // Application contracts are intentionally not MCP contracts.  Keep the
        // conversion explicit and typed so a newly-added property can never
        // silently become a public field.
        JsonNode? payload = value is JsonNode wire
            ? wire.DeepClone()
            : McpWireMapper.Map(tool ?? string.Empty, value);
        // Sign the repository cursor while its complete tie tuple is still
        // present. Projection intentionally removes those internal cursor
        // members from the public payload; signing after projection would make
        // an undecodable empty cursor.
        if (tool is not null && arguments is not null)
        {
            if (payload is JsonObject objectPayload)
            {
                if (signer is null) objectPayload.Remove("nextCursor");
                else if (window is not null && objectPayload["nextCursor"] is JsonNode cursor)
                {
                    JsonObject contextualCursor = cursor is JsonObject typed ? typed : new JsonObject { ["value"] = cursor.GetValue<string>() };
                    contextualCursor[McpCursorContinuation.WindowProperty] = window.ToJson();
                    if (cursor is not JsonObject) objectPayload["nextCursor"] = contextualCursor;
                }
            }
            signer?.EncodeNextCursor(payload, tool, arguments);
        }
        // Projection is deliberately selected by the catalog name.  This is an
        // important boundary: application DTOs are not MCP DTOs and must never
        // be allowed to grow the wire contract by accident.
        McpOutputProjection.Project(tool ?? string.Empty, payload);
        JsonObject structured = new() { ["data"] = payload?.DeepClone() };
        string envelope = structured.ToJsonString(options);
        long disclosedBytes = DisclosedResponseBytes(envelope, options);
        // Streamable HTTP exposes both the text content and StructuredContent.
        // Budget both serialized payload representations plus a bounded
        // JSON-RPC envelope allowance. The text content is itself a JSON
        // string on the wire, so quotes, backslashes, and other escaped
        // characters may consume more bytes than the envelope text alone.
        const int protocolEnvelopeOverheadBytes = 1_024;
        if (disclosedBytes + protocolEnvelopeOverheadBytes > McpQueryBounds.MaximumResponseBytes) throw new McpResponseOversizeException();
        return new CallToolResult { IsError = false, Content = new List<ContentBlock> { new TextContentBlock { Text = envelope } }, StructuredContent = JsonSerializer.Deserialize<JsonElement>(envelope) };
    }

    internal static long DisclosedResponseBytes(CallToolResult result, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new JsonSerializerOptions(JsonSerializerDefaults.Web);
        long bytes = result.Content.Sum(content => content is TextContentBlock text ? JsonSerializer.SerializeToUtf8Bytes(text.Text, options).LongLength : 0);
        if (result.StructuredContent is JsonElement structured)
            bytes += JsonSerializer.SerializeToUtf8Bytes(structured, options).LongLength;
        return bytes;
    }

    private static long DisclosedResponseBytes(string envelope, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, options).LongLength
        + JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<JsonElement>(envelope), options).LongLength;
}

/// <summary>
/// The MCP wire boundary. Every supported application return type has a
/// deliberately small mapper; no application object is serialized wholesale.
/// </summary>
#pragma warning disable CA1859
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
    private static JsonNode Wait(ServerWaitSummaryItem x) => O(("waitType", x.WaitType.Value), ("waitingTasksCount", x.WaitingTasksCount), ("waitTimeMilliseconds", x.WaitTimeMilliseconds), ("maximumWaitTimeMilliseconds", x.MaximumWaitTimeMilliseconds), ("signalWaitTimeMilliseconds", x.SignalWaitTimeMilliseconds), ("baselineAvailable", x.BaselineAvailable), ("resetDetected", x.ResetDetected), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonNode Session(ActivitySessionSnapshotItem x) => O(("sessionId", x.SessionId), ("status", E(x.Status)), ("isUserProcess", x.IsUserProcess), ("openTransactionCount", x.OpenTransactionCount), ("cpuMilliseconds", x.CpuMilliseconds), ("memoryUsagePages", x.MemoryUsagePages), ("reads", x.Reads), ("writes", x.Writes), ("logicalReads", x.LogicalReads), ("totalElapsedMilliseconds", x.TotalElapsedMilliseconds), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonNode Request(ActivityRequestSnapshotItem x) => O(("sessionId", x.SessionId), ("requestId", x.RequestId), ("status", E(x.Status)), ("command", E(x.Command)), ("cpuMilliseconds", x.CpuMilliseconds), ("totalElapsedMilliseconds", x.TotalElapsedMilliseconds), ("reads", x.Reads), ("writes", x.Writes), ("logicalReads", x.LogicalReads), ("rowCount", x.RowCount), ("percentComplete", x.PercentComplete), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonNode Blocking(BlockingEdgeSnapshotItem x) => O(("blockedSessionId", x.BlockedSessionId), ("blockerKind", E(x.BlockerKind)), ("waitType", x.WaitType.Value), ("waitingTaskCount", x.WaitingTaskCount), ("waitDurationMilliseconds", x.WaitDurationMilliseconds), ("observedAtUtc", T(x.ObservedAtUtc)));
    private static JsonNode Loss(CollectorLossEvidence x) => O(("kind", E(x.Kind)), ("minimumLostItems", x.MinimumLostItems), ("countIsExact", x.CountIsExact), ("minimumLostBytes", x.MinimumLostBytes));
    private static JsonNode Evidence(ActivitySnapshotEvidence x) => O(("targetId", Id(x.TargetId)), ("runId", Id(x.RunId)), ("targetRevision", long.TryParse(x.TargetRevision, out long revision) ? revision : 0), ("outcome", E(x.Outcome)), ("reason", E(x.Reason)), ("loss", x.Loss.HasLoss ? Loss(x.Loss) : null), ("completedAtUtc", T(x.CompletedAtUtc)));
    private static JsonNode Activity(ServerWaitSummaryPage x, Func<ServerWaitSummaryItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(ActivitySessionPage x, Func<ActivitySessionSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(ActivityRequestPage x, Func<ActivityRequestSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode Activity(CurrentBlockingPage x, Func<BlockingEdgeSnapshotItem, JsonNode> map) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, map)), ("evidence", x.Evidence is null ? null : Evidence(x.Evidence)), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
    private static JsonNode BlockingHistory(BlockingHistoryPage x) => O(("targetId", Id(x.TargetId)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, i => O(("evidence", Evidence(i.Evidence)), ("blockedSessionId", i.Edge.BlockedSessionId), ("blockerKind", E(i.Edge.BlockerKind)), ("waitType", i.Edge.WaitType.Value), ("waitingTaskCount", i.Edge.WaitingTaskCount), ("waitDurationMilliseconds", i.Edge.WaitDurationMilliseconds), ("observedAtUtc", T(i.Edge.ObservedAtUtc))))), ("snapshotUtc", T(x.RepositoryTimeUtc)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null));
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
        JsonObject result = O(("backupType", E(x.Kind)), ("state", E(x.Coverage)), ("databaseName", x.DatabaseFingerprint), ("value", x.SizeBytes));
        result["backupAtUtc"] = T(x.LastFinishUtc);
        return result;
    }
    private static JsonNode Job(SqlAgentFailureObservation x) => O(("jobName", Id(x.JobId)), ("failureAtUtc", T(x.DetectedAtUtc)), ("state", E(x.FailureKind)), ("reason", x.FailureFingerprint));
    private static JsonNode File(TempDbFileObservation x) => O(("fileId", x.FileId), ("sizeBytes", x.SizeBytes), ("usedBytes", x.UsedBytes), ("freeBytes", x.FreeBytes), ("state", E(x.State)));
    private static JsonNode Availability(AvailabilityGroupsSnapshot x) => O(("items", new JsonArray(x.Replicas.Select(Replica).Concat(x.Databases.Select(Database)).ToArray())), ("state", E(x.State)), ("snapshotUtc", T(x.ObservedAtUtc)), ("truncated", x.Truncated));
    private static JsonNode Replica(AvailabilityReplicaObservation x) => O(("availabilityGroup", x.GroupFingerprint), ("role", x.Role), ("synchronizationState", x.OperationalState), ("state", x.ConnectedState), ("reason", x.ReplicaFingerprint));
    private static JsonNode Database(AvailabilityDatabaseObservation x) => O(("availabilityGroup", x.GroupFingerprint), ("role", x.DatabaseFingerprint), ("synchronizationState", x.SynchronizationState), ("state", x.DatabaseState), ("reason", x.DatabaseFingerprint));
    private static JsonNode Incident(IncidentEvidencePage x) => O(("targetId", Id(x.TargetId)), ("threadId", Id(x.ThreadId)), ("items", A(x.Items, IncidentItem)), ("generations", A(x.Generations, Generation)), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)));
    private static JsonNode IncidentItem(IncidentEvidenceItem x) => O(("occurredAtUtc", T(x.OccurredAtUtc)), ("packetId", Id(x.PacketId)), ("evidenceKind", x.EvidenceKind), ("sourceRunId", NullableId(x.SourceRunId)), ("sourceDigest", x.SourceDigest), ("identityDigest", x.IdentityDigest), ("sourceCutoffDigest", x.SourceCutoffDigest), ("sourceCutoffUtc", T(x.SourceCutoffUtc)), ("confidence", x.Confidence), ("visibilityState", x.VisibilityState));
    private static JsonNode Generation(IncidentGenerationItem x) => O(("threadId", Id(x.ThreadId)), ("generation", x.Generation), ("observedAtUtc", T(x.ObservedAtUtc)), ("correlationSha256", x.CorrelationSha256), ("supersedesPrevious", x.SupersedesPrevious), ("evidencePacketId", NullableId(x.EvidencePacketId)));
    private static JsonNode Diagnostic(DiagnosticEventSearchPage x) => O(("targetId", Id(x.TargetId)), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("items", A(x.Items, DiagnosticItem)), ("hasMore", x.HasMore), ("nextCursor", Cursor(x.NextCursor)), ("targetRevision", x.TargetRevision.Value), ("snapshotUtc", T(x.SnapshotUtc)));
    private static JsonNode DiagnosticItem(DiagnosticEventItem x) => O(("occurredAtUtc", T(x.OccurredAtUtc)), ("eventId", Id(x.EventId)), ("eventKind", x.EventKind), ("severity", x.Severity), ("safeMetadata", O(("metricKey", x.SafeMetadata.MetricKey), ("participantCount", x.SafeMetadata.ParticipantCount), ("relationCount", x.SafeMetadata.RelationCount), ("parseTruncated", x.SafeMetadata.ParseTruncated))), ("collectedAtUtc", T(x.CollectedAtUtc)), ("targetRevision", x.TargetRevision.Value));
    private static JsonNode Database(DatabaseHealthPage x, bool file) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, DatabaseItem)), ("snapshotRunId", NullableId(x.SnapshotRunId)), ("snapshotTargetRevision", x.SnapshotTargetRevision?.Value), ("collector", Collector(x.Collector)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null), ("repositoryTimeUtc", T(x.RepositoryTimeUtc)));
    private static JsonNode Database(DatabaseFileHealthPage x, bool file) => O(("targetId", Id(x.TargetId)), ("items", A(x.Items, DatabaseFileItem)), ("snapshotRunId", NullableId(x.SnapshotRunId)), ("snapshotTargetRevision", x.SnapshotTargetRevision?.Value), ("collector", Collector(x.Collector)), ("nextCursor", Cursor(x.NextCursor)), ("hasMore", x.NextCursor is not null), ("repositoryTimeUtc", T(x.RepositoryTimeUtc)));
    private static JsonNode DatabaseItem(DatabaseHealthItem x) => O(("observation", O(("databaseId", x.Observation.DatabaseId), ("databaseName", x.Observation.Name.Value), ("state", E(x.Observation.State)), ("observedAtUtc", T(x.Observation.ObservedAtUtc)))), ("collector", Collector(x.Collector)));
    private static JsonNode DatabaseFileItem(DatabaseFileHealthItem x) => O(("observation", O(("databaseId", x.Observation.DatabaseId), ("fileId", x.Observation.FileId), ("fileName", x.Observation.LogicalName.Value), ("sizeBytes", x.Observation.SizeBytes), ("readOperations", x.Observation.ReadCount), ("writeOperations", x.Observation.WriteCount), ("readBytes", x.Observation.BytesRead), ("writeBytes", x.Observation.BytesWritten), ("latencyMilliseconds", x.Observation.IoStallMilliseconds), ("observedAtUtc", T(x.Observation.ObservedAtUtc)))), ("collector", Collector(x.Collector)));
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
        IncidentEvidenceCursor x => O(("targetId", ValueObject(Id(x.TargetId))), ("targetRevision", NumberObject(x.TargetRevision.Value)), ("threadId", Id(x.ThreadId)), ("snapshotUtc", T(x.SnapshotUtc)), ("evidenceOccurredAtUtc", T(x.EvidenceOccurredAtUtc)), ("evidencePacketId", NullableId(x.EvidencePacketId)), ("generation", x.Generation)),
        QueryPerformanceCursorEnvelope x => O(("targetId", ValueObject(Id(x.TargetId))), ("databaseId", x.DatabaseId), ("fromUtc", T(x.FromUtc)), ("toUtc", T(x.ToUtc)), ("metric", E(x.Metric)), ("metricValue", x.MetricValue), ("snapshotUtc", T(x.SnapshotUtc)), ("intervalEndUtc", T(x.IntervalEndUtc)), ("queryFingerprint", x.QueryFingerprint), ("collectionRunId", x.CollectionRunId is null ? null : Id(x.CollectionRunId)), ("planFingerprint", x.PlanFingerprint), ("observationKey", x.ObservationKey)),
        string x => O(("value", x)),
        _ => throw new ArgumentException("Unsupported cursor type at MCP boundary.")
    };
}
#pragma warning restore CA1859

internal static class McpOutputProjection
{
    // Every catalog entry has an entry here. Keeping this table explicit makes
    // a newly added tool fail closed until its minimum projection is reviewed.
    private static readonly Dictionary<string, HashSet<string>> ToolFields = new(StringComparer.Ordinal)
    {
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
        ["get_availability_health"] = Fields("items", "state", "snapshotUtc", "truncated"),
        ["get_incident_evidence"] = Fields("targetId", "threadId", "items", "generations", "targetRevision", "snapshotUtc", "hasMore", "nextCursor"),
        ["search_diagnostic_events"] = Fields("targetId", "fromUtc", "toUtc", "items", "hasMore", "nextCursor", "targetRevision", "snapshotUtc")
    };

    private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> ToolPathFields =
        new(StringComparer.Ordinal)
        {
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
        ("get_file_io", "observation") => ["databaseId", "fileId", "fileName", "sizeBytes", "readOperations", "writeOperations", "readBytes", "writeBytes", "latencyMilliseconds", "observedAtUtc"],
        ("get_metric_series" or "get_storage_forecast", "dimensions") => ["key", "value"],
        ("get_deadlock", "participants") => ["sessionId", "isVictim"],
        ("get_deadlock", "relations") => ["blockerSessionId", "waiterSessionId", "resourceCategory", "lockMode"],
        ("get_incident_evidence", "generations") => ["threadId", "generation", "observedAtUtc", "correlationSha256", "supersedesPrevious", "evidencePacketId"],
        ("search_diagnostic_events", "safeMetadata") => ["metricKey", "participantCount", "relationCount", "parseTruncated"],
        _ => []
    };

    private static IReadOnlyCollection<string> ItemFields(string tool) => tool switch
    {
        "get_active_alerts" => ["alertId", "ruleId", "targetId", "ruleName", "state", "firstObservedUtc", "firedUtc", "acknowledgedUtc", "value", "reason", "deliverySuppressed"],
        "get_metric_series" => ["observedAtUtc", "value", "dimensions"],
        "get_wait_summary" => ["waitType", "waitingTasksCount", "waitTimeMilliseconds", "maximumWaitTimeMilliseconds", "signalWaitTimeMilliseconds", "baselineAvailable", "resetDetected", "observedAtUtc"],
        "get_active_sessions" => ["sessionId", "status", "isUserProcess", "openTransactionCount", "cpuMilliseconds", "memoryUsagePages", "reads", "writes", "logicalReads", "totalElapsedMilliseconds", "observedAtUtc"],
        "get_active_requests" => ["sessionId", "requestId", "status", "command", "cpuMilliseconds", "totalElapsedMilliseconds", "reads", "writes", "logicalReads", "rowCount", "percentComplete", "observedAtUtc"],
        "get_blocking_chain" => ["blockedSessionId", "blockerKind", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        "get_blocking_history" => ["evidence", "blockedSessionId", "blockerKind", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        "search_deadlocks" => ["targetId", "eventId", "occurredAtUtc", "fingerprint", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"],
        "get_top_queries" => ["targetId", "query", "plan", "source", "sourceState", "metric", "value", "semantics", "intervalStartUtc", "intervalEndUtc", "coverage", "fresh", "truncated", "contentAvailable", "collectionRunId", "planFingerprint", "observationKey"],
        "get_query_history" => ["targetId", "query", "source", "sourceState", "metrics", "semantics", "intervalStartUtc", "intervalEndUtc", "resetDetected", "fresh", "truncated", "contentAvailable", "collectionRunId", "coverage", "planFingerprint", "observationKey"],
        "get_database_health" => ["observation", "collector"],
        "get_file_io" => ["observation", "collector"],
        "get_tempdb_health" => ["fileId", "sizeBytes", "usedBytes", "freeBytes", "state"],
        "get_storage_forecast" => ["forecastId", "metricKey", "horizonStartUtc", "horizonEndUtc", "estimate", "lowerBound", "upperBound", "slopePerDay", "confidence", "residual", "model", "sourceGeneration", "visibilityState", "dimensionsSha256"],
        "get_backup_status" => ["backupType", "backupAtUtc", "state", "databaseName", "value"],
        "get_job_failures" => ["jobName", "failureAtUtc", "state", "reason"],
        "get_availability_health" => ["availabilityGroup", "role", "synchronizationState", "state", "reason"],
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
        "get_availability_health" => ["items", "state", "snapshotUtc", "truncated"],
        "get_incident_evidence" => ["targetId", "threadId", "items", "generations", "targetRevision", "snapshotUtc", "hasMore"],
        "search_diagnostic_events" => ["targetId", "fromUtc", "toUtc", "items", "hasMore", "targetRevision", "snapshotUtc"],
        _ => Array.Empty<string>()
    };

    public static JsonObject NestedObjectSchema(string tool, string path)
    {
        var properties = new JsonObject();
        foreach (string field in NestedFields(tool, path)) properties[field] = SchemaForField(tool, path, field);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        string[] required = RequiredFields(tool, path);
        if (required.Length > 0) schema["required"] = JsonSerializer.SerializeToNode(required);
        return schema;
    }

    public static JsonObject SchemaForField(string tool, string path, string field)
    {
        if (path == "dimensions" && field == "value") return new JsonObject { ["type"] = "string" };
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
        ("list_instances", "targets") => ["targetId", "key", "displayName", "lifecycle", "revision", "createdAtUtc", "discoveryRequestedAtUtc", "updatedAtUtc"],
        ("get_instance_capabilities", "capabilities") => ["state", "status", "reason", "revision", "observedAtUtc"],
        ("get_instance_health", "coreCollector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "evidence") => ["targetId", "runId", "targetRevision", "outcome", "reason", "completedAtUtc"],
        ("get_wait_summary" or "get_active_sessions" or "get_active_requests" or "get_blocking_chain" or "get_blocking_history", "loss") => ["kind", "minimumLostItems", "countIsExact", "minimumLostBytes"],
        ("get_top_queries" or "get_query_history", "query") => ["databaseId", "queryFingerprint"],
        ("get_top_queries", "plan") or ("get_query_plan_metadata", "plan") => ["databaseId", "queryFingerprint", "planFingerprint"],
        ("get_database_health" or "get_file_io", "collector") => ["state", "reason", "status", "health", "targetId", "collectorId", "observedAtUtc"],
        ("get_database_health", "observation") => ["databaseId", "databaseName", "state", "observedAtUtc"],
        ("get_file_io", "observation") => ["databaseId", "fileId", "fileName", "sizeBytes", "readOperations", "writeOperations", "readBytes", "writeBytes", "latencyMilliseconds", "observedAtUtc"],
        ("get_metric_series" or "get_storage_forecast", "dimensions") => ["key", "value"],
        ("get_deadlock", "participants") => ["sessionId", "isVictim"],
        ("get_deadlock", "relations") => ["blockerSessionId", "waiterSessionId", "resourceCategory", "lockMode"],
        ("get_incident_evidence", "generations") => ["threadId", "generation", "observedAtUtc", "correlationSha256", "supersedesPrevious"],
        ("search_diagnostic_events", "safeMetadata") => [],
        ("get_active_alerts", "items") => ["alertId", "ruleId", "targetId", "ruleName", "state", "firstObservedUtc", "deliverySuppressed"],
        ("get_metric_series", "items") => ["observedAtUtc", "value", "dimensions"],
        ("get_wait_summary", "items") => ["waitType", "waitingTasksCount", "waitTimeMilliseconds", "maximumWaitTimeMilliseconds", "signalWaitTimeMilliseconds", "baselineAvailable", "resetDetected", "observedAtUtc"],
        ("get_active_sessions", "items") => ["sessionId", "status", "isUserProcess", "openTransactionCount", "cpuMilliseconds", "memoryUsagePages", "reads", "writes", "logicalReads", "totalElapsedMilliseconds", "observedAtUtc"],
        ("get_active_requests", "items") => ["sessionId", "requestId", "status", "command", "cpuMilliseconds", "totalElapsedMilliseconds", "reads", "writes", "logicalReads", "rowCount", "percentComplete", "observedAtUtc"],
        ("get_blocking_chain", "items") => ["blockedSessionId", "blockerKind", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        ("get_blocking_history", "items") => ["evidence", "blockedSessionId", "blockerKind", "waitType", "waitingTaskCount", "waitDurationMilliseconds", "observedAtUtc"],
        ("search_deadlocks", "items") => ["targetId", "eventId", "occurredAtUtc", "fingerprint", "participantCount", "relationCount", "parseTruncated", "collectedAtUtc"],
        ("get_top_queries", "items") => ["targetId", "query", "source", "sourceState", "metric", "semantics", "intervalStartUtc", "intervalEndUtc", "coverage", "fresh", "truncated", "contentAvailable"],
        ("get_query_history", "items") => ["targetId", "query", "source", "sourceState", "metrics", "semantics", "intervalStartUtc", "intervalEndUtc", "resetDetected", "fresh", "truncated", "contentAvailable", "coverage"],
        ("get_database_health" or "get_file_io", "items") => ["observation", "collector"],
        ("get_tempdb_health", "items") => ["fileId", "sizeBytes", "usedBytes", "freeBytes", "state"],
        ("get_storage_forecast", "items") => ["metricKey", "horizonStartUtc", "horizonEndUtc", "confidence", "residual", "model", "sourceGeneration", "visibilityState", "dimensionsSha256"],
        ("get_backup_status", "items") => ["backupType", "backupAtUtc", "state", "databaseName"],
        ("get_job_failures", "items") => ["jobName", "failureAtUtc", "state", "reason"],
        ("get_availability_health", "items") => ["availabilityGroup", "role", "synchronizationState", "state", "reason"],
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
