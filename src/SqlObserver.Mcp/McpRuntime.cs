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
public sealed record McpToolDefinition(string Name, string Title, string Description, string InputSchemaJson);

/// <summary>Stable M11 catalog: additions require an intentional backlog/ADR change.</summary>
public static class McpCatalog
{
    public const string CurrentProtocolVersion = "2026-07-28";
    public const string DownlevelProtocolVersion = "2025-11-25";
    // Reviewed catalog approval point. Digest is re-derived below and must
    // agree, so changing the catalog cannot silently retain this identity.
    public const string ApprovedCatalogDigest = "984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C";
    public const string ServerVersion = "m11-2.2.0+catalog-984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C";
    private static readonly JsonSerializerOptions DigestJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly IReadOnlyList<McpToolDefinition> Definitions = new[]
    {
        "list_instances", "get_instance_capabilities", "get_instance_health", "get_active_alerts", "get_metric_series",
        "compare_metric_windows", "get_wait_summary", "get_active_sessions", "get_active_requests", "get_blocking_chain",
        "get_blocking_history", "get_deadlock", "search_deadlocks", "get_top_queries", "get_query_history",
        "get_query_plan_metadata", "get_database_health", "get_tempdb_health", "get_file_io", "get_storage_forecast",
        "get_backup_status", "get_job_failures", "get_availability_health", "get_incident_evidence", "search_diagnostic_events",
        "list_metric_catalog", "list_incidents"
    }.Select(static name => new McpToolDefinition(name, McpCatalogDescriptions.Tool(name).Title, McpCatalogDescriptions.Tool(name).Description, SchemaFor(name))).ToArray();

    private static string SchemaFor(string name)
    {
        var properties = new JsonObject();
        void Add(string key, string json)
        {
            JsonObject property = JsonNode.Parse(json)!.AsObject();
            property["description"] = McpCatalogDescriptions.Parameter(name, key);
            if (key == "metricKey") property["enum"] = JsonSerializer.SerializeToNode(MetricCatalogV1.All.Where(static entry => entry.Enabled).Select(static entry => entry.Key).Order(StringComparer.Ordinal).ToArray());
            if (key == "limit") property["default"] = LimitMaximum(name);
            if (key == "horizonDays") property["default"] = 30;
            properties[key] = property;
        }
        if (name is not ("list_instances" or "list_metric_catalog")) Add("instanceId", "{\"type\":\"string\",\"format\":\"uuid\"}");
        bool window = McpCursorContinuation.HasPagedWindow(name);
        if (window) { Add("fromUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); Add("toUtc", "{\"type\":\"string\",\"format\":\"date-time\"}"); }
        // The combined AG view is a bounded summary. Replica/database child
        // streams have independent orderings, so one merged MCP cursor would
        // not be a truthful continuation contract.
        bool paged = name is not ("get_instance_health" or "get_instance_capabilities" or "get_deadlock" or "get_query_plan_metadata" or "compare_metric_windows" or "get_availability_health" or "list_metric_catalog");
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
            "list_instances" or "list_metric_catalog" => [],
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

    internal static TimeSpan WindowMaximum(string name) => name switch
    {
        "get_blocking_history" => TimeSpan.FromHours(24),
        "get_top_queries" or "get_query_history" or "get_job_failures" or "search_diagnostic_events" => TimeSpan.FromDays(7),
        "get_metric_series" or "search_deadlocks" or "list_incidents" => TimeSpan.FromDays(31),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static string WindowLabel(string name) => name == "get_blocking_history" ? "24 hours" : $"{WindowMaximum(name).Days} days";

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
            Title = definition.Title,
            Description = definition.Description,
            InputSchema = document.RootElement.Clone(),
            OutputSchema = McpCatalog.OutputSchema(definition.Name),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                Title = definition.Title
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
    // Capture the SDK's bounded JSON input before applying the stricter tool
    // nesting limit, so rejected nested arguments retain their exact audit digest.
    private static readonly JsonSerializerOptions AuditSnapshotOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
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
        try { ValidateRequest(arguments); McpInputValidation.ValidateArguments(toolName, arguments); }
        catch (McpInputValidationException exception)
        {
            CallToolResult invalid = McpResults.Error("invalid_request", exception.Message);
            return await AuditOrWithholdAsync(toolName, auditSnapshot, user, McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest, TimeSpan.Zero, invalid).ConfigureAwait(false);
        }
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
        catch (McpInputValidationException exception) { outcome = McpInvocationOutcome.Invalid; reason = McpInvocationAuditReason.InvalidRequest; result = McpResults.Error("invalid_request", exception.Message); }
        catch (IncidentListChangedException) { outcome = McpInvocationOutcome.Invalid; reason = McpInvocationAuditReason.InvalidRequest; result = McpResults.Error("cursor_stale", "Incidents changed. Restart list_incidents without a cursor."); }
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
        JsonElement parameters = JsonSerializer.SerializeToElement(arguments, AuditSnapshotOptions).Clone();
        Guid? target = toolName == "list_metric_catalog" ? null : ReadAuditGuid(parameters, "instanceId");
        Guid? incident = toolName == "get_incident_evidence" ? ReadAuditGuid(parameters, "threadId") : null;
        return new McpAuditSnapshot(parameters, target, incident);
    }

    private static void ValidateRequest(IDictionary<string, JsonElement> args)
    {
        foreach (JsonElement value in args.Values) ValidateDepth(value, 1);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(args, JsonOptions);
        if (encoded.Length > 64 * 1024) throw new McpInputValidationException("The request exceeds the 65536-byte limit.");
    }

    private static void ValidateDepth(JsonElement value, int depth)
    {
        // The arguments object already occupies one container level. Empty
        // containers must be checked too; they have no child to trigger a check.
        if (depth > 32 || depth == 32 && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            throw new McpInputValidationException("The request exceeds the maximum nesting depth of 32.");
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
            throw new McpInputValidationException(McpInputValidation.InvalidCursor);
        // Old metric/raw cursors did not retain default bounds. Only an entirely
        // explicit request can safely continue one of those legacy tokens.
        if (continuation is not null && McpCursorContinuation.HasPagedWindow(name) && frozen is null && (requestedFrom is null || requestedTo is null))
            throw new McpInputValidationException(McpInputValidation.InvalidCursor);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset to = requestedTo ?? frozen?.ToUtc ?? now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        DateTimeOffset from = requestedFrom ?? frozen?.FromUtc ?? to.AddDays(-1);
        int limit = ReadLimit(name, args);
        string metric = args.TryGetValue("metricKey", out JsonElement metricValue) ? metricValue.GetString() ?? string.Empty : string.Empty;
        RepositoryCallTimeout timeout = new(TimeSpan.FromSeconds(5));
        if (McpCursorContinuation.HasPagedWindow(name)) McpInputValidation.ValidateWindow(name, from, to);
        if (name == "compare_metric_windows")
        {
            from = ReadUtc(args, "leftFromUtc")!.Value;
            to = ReadUtc(args, "leftToUtc")!.Value;
        }
        object? value = name switch
        {
            "list_metric_catalog" => MetricCatalogQueryService.Get(authorization),
            "list_incidents" when id.HasValue => await _services.GetRequiredService<IIncidentListQueryService>().ListAsync(new IncidentListQuery(authorization, new MonitoredInstanceId(id.Value), from, to, limit, timeout, Cursor: continuation?.Read<IncidentListCursor>(JsonOptions)), token).ConfigureAwait(false),
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
        return McpInputValidation.ParseUtc(value.GetString() ?? string.Empty, name);
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
        if (string.IsNullOrWhiteSpace(text) || signer is null) throw new McpInputValidationException(McpInputValidation.InvalidCursor);
        try { return new McpCursorContinuation(tool, signer.DecodePayload(text, tool, args)); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException or InvalidOperationException) { throw new McpInputValidationException(McpInputValidation.InvalidCursor); }
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
