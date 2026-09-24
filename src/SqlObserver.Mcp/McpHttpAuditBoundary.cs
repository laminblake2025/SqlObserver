using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using SqlObserver.Audit;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Mcp;

/// <summary>Audits direct MCP protocol failures while application-routed calls mark themselves as handled.</summary>
public sealed class McpHttpAuditBoundaryMiddleware
{
    public const string HandledItemKey = "SqlObserver.Mcp.AuditHandled";
    public const string BoundaryActiveItemKey = "SqlObserver.Mcp.AuditBoundaryActive";
    public const string PendingAuditItemKey = "SqlObserver.Mcp.PendingAudit";
    private const int MaximumRequestBytes = 65_536;
    private readonly RequestDelegate _next;

    public McpHttpAuditBoundaryMiddleware(RequestDelegate next) => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Request.Path.Value, "/mcp", StringComparison.OrdinalIgnoreCase) || !HttpMethods.IsPost(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        ProtocolMetadata metadata;
        Exception? metadataFailure = null;
        try { metadata = await ReadMetadataAsync(context.Request, context.RequestAborted).ConfigureAwait(false); }
        catch (Exception exception)
        {
            // Body/transport failures happen before the SDK can set its marker.
            metadataFailure = exception;
            metadata = ProtocolMetadata.ReadFailureUnknown;
        }

        Stream originalBody = context.Response.Body;
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        try
        {
            if (metadataFailure is not null)
            {
                if (metadata.Invocation)
                    await AuditAndFailClosedAsync(context, originalBody, metadata, ClassifyFailure(context, metadataFailure)).ConfigureAwait(false);
                else
                    await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, ClassifyFailure(context, metadataFailure)).ConfigureAwait(false);
                return;
            }
            if (metadata.ReadFailed)
            {
                if (metadata.Invocation)
                    await AuditAndFailClosedAsync(context, originalBody, metadata, metadata.ReadCancelled
                        ? (McpInvocationOutcome.Cancelled, McpInvocationAuditReason.Cancelled)
                        : (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure)).ConfigureAwait(false);
                else
                    await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, metadata.ReadCancelled
                        ? (McpInvocationOutcome.Cancelled, McpInvocationAuditReason.Cancelled)
                        : (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure)).ConfigureAwait(false);
                return;
            }
            if (metadata.Oversized)
            {
                // Do not hand an unbounded/chunked body to the SDK after the
                // bounded reader has detected overflow.
                await AuditAndFailClosedAsync(context, originalBody, metadata, Classify(context, metadata), StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
                return;
            }

            if (metadata.Invocation) context.Items[BoundaryActiveItemKey] = true;

            Exception? downstreamFailure = null;
            try { await _next(context).ConfigureAwait(false); }
            catch (Exception exception) { downstreamFailure = exception; }

            // McpCallHandler owns SDK-routed calls, including application
            // outcomes until the SDK has completed writing the wire response.
            if (context.Items.TryGetValue(PendingAuditItemKey, out object? pendingValue) && pendingValue is McpPendingAudit pending)
            {
                (McpInvocationOutcome outcome, McpInvocationAuditReason reason) classification = downstreamFailure is not null
                    ? ClassifyFailure(context, downstreamFailure)
                    : (pending.Outcome, pending.Reason);
                if (downstreamFailure is null && captured.Length > McpQueryBounds.MaximumResponseBytes)
                {
                    classification = (McpInvocationOutcome.Oversize, McpInvocationAuditReason.ResponseOversize);
                    ReplaceWithOversizeResponse(context, captured, metadata.RequestId);
                }
                try { await AppendPendingAsync(context, pending, classification, classification.Item1 == McpInvocationOutcome.Succeeded ? captured.Length : 0).ConfigureAwait(false); }
                catch
                {
                    await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure), auditUnavailable: true).ConfigureAwait(false);
                    return;
                }
                if (downstreamFailure is not null)
                {
                    await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, classification).ConfigureAwait(false);
                    return;
                }
                await CopyResponseAsync(captured, originalBody, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // A marker without a pending snapshot is retained for compatibility
            // with older callers; it still must never be double-audited.
            if (context.Items.ContainsKey(HandledItemKey))
            {
                if (downstreamFailure is null) await CopyResponseAsync(captured, originalBody, CancellationToken.None).ConfigureAwait(false);
                else await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, ClassifyFailure(context, downstreamFailure)).ConfigureAwait(false);
                return;
            }

            if (!metadata.Invocation)
            {
                // initialize, tools/list, notifications, and other protocol
                // control methods deliberately have no tool-audit row.
                if (downstreamFailure is null) await CopyResponseAsync(captured, originalBody, context.RequestAborted).ConfigureAwait(false);
                else await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, ClassifyFailure(context, downstreamFailure)).ConfigureAwait(false);
                return;
            }

            (McpInvocationOutcome outcome, McpInvocationAuditReason reason) = downstreamFailure is not null
                ? ClassifyFailure(context, downstreamFailure)
                : Classify(context, metadata);
            if (downstreamFailure is null && captured.Length > McpQueryBounds.MaximumResponseBytes)
            {
                outcome = McpInvocationOutcome.Oversize;
                reason = McpInvocationAuditReason.ResponseOversize;
                ReplaceWithOversizeResponse(context, captured, metadata.RequestId);
            }
            try { await AppendAsync(context, metadata, outcome, reason).ConfigureAwait(false); }
            catch
            {
                // Never copy a captured response when required auditing fails.
                await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure), auditUnavailable: true).ConfigureAwait(false);
                return;
            }

            if (downstreamFailure is not null)
            {
                await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure)).ConfigureAwait(false);
                return;
            }

            await CopyResponseAsync(captured, originalBody, CancellationToken.None).ConfigureAwait(false);
        }
        finally { context.Response.Body = originalBody; }
    }

    private static async ValueTask<ProtocolMetadata> ReadMetadataAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumRequestBytes) return ProtocolMetadata.OversizedRequest;
        request.EnableBuffering(MaximumRequestBytes);
        await using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumRequestBytes)
                {
                    request.Body.Position = 0;
                    return ProtocolMetadata.OversizedRequest;
                }
                // Preserve bytes already received even if the caller cancels
                // between the stream read and local bounded buffering.
                await buffer.WriteAsync(chunk.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
            }
            request.Body.Position = 0;
        }
        catch
        {
            // Keep the buffering stream at a deterministic position for outer
            // middleware. The SDK is not called after a read failure.
            try { request.Body.Position = 0; } catch (Exception) { }
            bool cancelled = cancellationToken.IsCancellationRequested;
            return RecognizePartialReadFailure(buffer, cancelled);
        }

        if (buffer.Length == 0) return ProtocolMetadata.InvalidRequest;
        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ProtocolMetadata.InvalidRequest;
            // Match the SDK's string/Int64 identity representation. An invalid
            // or absent ID cannot be safely correlated in a generated error.
            JsonElement requestId = root.TryGetProperty("id", out JsonElement id) &&
                (id.ValueKind == JsonValueKind.String || (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)))
                ? id.Clone() : JsonSerializer.SerializeToElement<object?>(null);
            if (!root.TryGetProperty("method", out JsonElement methodElement) || methodElement.ValueKind != JsonValueKind.String)
                return ProtocolMetadata.InvalidRequest with { RequestId = requestId };
            string method = methodElement.GetString() ?? string.Empty;
            if (!string.Equals(method, "tools/call", StringComparison.Ordinal)) return ProtocolMetadata.Control with { RequestId = requestId };

            JsonElement empty = EmptyParameters();
            if (!root.TryGetProperty("params", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                return new ProtocolMetadata(false, true, true, null, empty, null, null, requestId);

            string? toolName = name.GetString();
            bool invalid = false;
            JsonElement arguments = empty;
            if (!parameters.TryGetProperty("arguments", out JsonElement argumentElement)) invalid = true;
            else if (argumentElement.ValueKind != JsonValueKind.Object) invalid = true;
            else arguments = argumentElement.Clone();
            (Guid? targetId, Guid? incidentId) = ReadResourceIdentity(arguments);
            return new ProtocolMetadata(false, invalid, true, toolName, arguments, targetId, incidentId, requestId);
        }
        catch (JsonException) { return ProtocolMetadata.InvalidRequest; }
    }

    private static ProtocolMetadata RecognizePartialReadFailure(MemoryStream buffer, bool cancelled)
    {
        string? method = null;
        bool sawToolsCall = false;
        try
        {
            var reader = new Utf8JsonReader(buffer.ToArray(), isFinalBlock: false, state: default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !string.Equals(reader.GetString(), "method", StringComparison.Ordinal)) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) break;
                method = reader.GetString();
                sawToolsCall |= string.Equals(method, "tools/call", StringComparison.Ordinal);
            }
        }
        catch (JsonException) { }

        // A duplicate method key is malformed but must not turn a proven
        // tools/call frame into an unaudited control frame.
        bool invocation = sawToolsCall || string.Equals(method, "tools/call", StringComparison.Ordinal);
        return new ProtocolMetadata(false, true, invocation, null, EmptyParameters(), null, null, JsonSerializer.SerializeToElement<object?>(null), true, cancelled);
    }

    private static (McpInvocationOutcome Outcome, McpInvocationAuditReason Reason) Classify(HttpContext context, ProtocolMetadata metadata)
    {
        if (context.Response.StatusCode is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
            return (McpInvocationOutcome.Denied, McpInvocationAuditReason.AuthorizationDenied);
        if (metadata.Oversized || context.Response.StatusCode == StatusCodes.Status413PayloadTooLarge)
            return (McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest);
        if (metadata.ToolName is not null && !McpCatalog.Definitions.Any(d => d.Name == metadata.ToolName))
            return (McpInvocationOutcome.UnknownTool, McpInvocationAuditReason.UnknownTool);
        if (metadata.Invalid || context.Response.StatusCode >= 400)
            return (McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest);
        return (McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest);
    }

    private static (McpInvocationOutcome Outcome, McpInvocationAuditReason Reason) ClassifyFailure(HttpContext context, Exception exception)
    {
        if (exception is UnauthorizedAccessException)
            return (McpInvocationOutcome.Denied, McpInvocationAuditReason.AuthorizationDenied);
        if (exception is ArgumentException or BadHttpRequestException or InvalidDataException)
            return (McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest);
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return (McpInvocationOutcome.Cancelled, McpInvocationAuditReason.Cancelled);
        if (exception is OperationCanceledException or TimeoutException)
            return (McpInvocationOutcome.Timeout, McpInvocationAuditReason.Timeout);
        return (McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure);
    }

    private static async ValueTask AuditAndFailClosedAsync(HttpContext context, Stream originalBody, ProtocolMetadata metadata, (McpInvocationOutcome Outcome, McpInvocationAuditReason Reason) classification, int? statusOverride = null)
    {
        bool auditFailed = false;
        try { await AppendAsync(context, metadata, classification.Outcome, classification.Reason).ConfigureAwait(false); }
        catch { auditFailed = true; }
        await WriteSafeFailureAsync(context, originalBody, metadata.RequestId, classification, auditUnavailable: auditFailed, statusOverride).ConfigureAwait(false);
    }

    private static async ValueTask AppendAsync(HttpContext context, ProtocolMetadata metadata, McpInvocationOutcome outcome, McpInvocationAuditReason reason)
    {
        IMcpInvocationAuditService audit = context.RequestServices.GetRequiredService<IMcpInvocationAuditService>();
        string actor = context.User.FindFirstValue(ClaimTypes.PrimarySid) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        if (actor.Length is 0 or > McpClientIdentifier.MaximumLength) actor = "authenticated";
        McpClientIdentifier client;
        try { client = new McpClientIdentifier(actor); } catch (ArgumentException) { client = new McpClientIdentifier("authenticated"); }

        string tool = metadata.ToolName is not null && McpCatalog.Definitions.Any(d => d.Name == metadata.ToolName) ? metadata.ToolName : "unknown_tool";
        McpAuthorizationResult authorization = outcome == McpInvocationOutcome.Denied
            ? McpAuthorizationResult.Denied
            : outcome is McpInvocationOutcome.Succeeded or McpInvocationOutcome.Oversize or McpInvocationOutcome.Timeout or McpInvocationOutcome.Cancelled or McpInvocationOutcome.Limited or McpInvocationOutcome.RepositoryFailure
                ? McpAuthorizationResult.Allowed
                : McpAuthorizationResult.NotApplicable;
        var record = new McpInvocationAuditRecord(
            Guid.NewGuid(), client, new McpToolName(tool), new McpActionName("mcp.tool." + tool), authorization,
            outcome, reason, new AuditCorrelationId(Guid.NewGuid()), McpParameterCanonicalizer.Digest(metadata.Parameters),
            TimeSpan.Zero, 0, metadata.TargetId.HasValue ? new MonitoredInstanceId(metadata.TargetId.Value) : null, metadata.IncidentId);
        await audit.AppendTerminalAsync(record).ConfigureAwait(false);
    }

    private static ValueTask AppendPendingAsync(HttpContext context, McpPendingAudit pending, (McpInvocationOutcome Outcome, McpInvocationAuditReason Reason) classification, long responseBytes) =>
        AppendRecordAsync(context, new McpInvocationAuditRecord(
            pending.InvocationId,
            CreateClientIdentifier(pending.Actor),
            new McpToolName(pending.ToolName),
            new McpActionName("mcp.tool." + pending.ToolName),
            AuthorizationFor(classification.Outcome),
            classification.Outcome,
            classification.Reason,
            new AuditCorrelationId(Guid.NewGuid()),
            pending.ParameterDigest,
            pending.Duration,
            classification.Outcome == McpInvocationOutcome.Succeeded ? responseBytes : 0,
            pending.TargetId.HasValue ? new MonitoredInstanceId(pending.TargetId.Value) : null,
            pending.IncidentId));

    private static async ValueTask AppendRecordAsync(HttpContext context, McpInvocationAuditRecord record)
    {
        IMcpInvocationAuditService audit = context.RequestServices.GetRequiredService<IMcpInvocationAuditService>();
        await audit.AppendTerminalAsync(record).ConfigureAwait(false);
    }

    private static McpClientIdentifier CreateClientIdentifier(string actor)
    {
        if (actor.Length is 0 or > McpClientIdentifier.MaximumLength) actor = "authenticated";
        try { return new McpClientIdentifier(actor); }
        catch (ArgumentException) { return new McpClientIdentifier("authenticated"); }
    }

    private static McpAuthorizationResult AuthorizationFor(McpInvocationOutcome outcome) => outcome == McpInvocationOutcome.Denied
        ? McpAuthorizationResult.Denied
        : outcome is McpInvocationOutcome.Succeeded or McpInvocationOutcome.Oversize or McpInvocationOutcome.Timeout or McpInvocationOutcome.Cancelled or McpInvocationOutcome.Limited or McpInvocationOutcome.RepositoryFailure
            ? McpAuthorizationResult.Allowed
            : McpAuthorizationResult.NotApplicable;

    private static void ReplaceWithOversizeResponse(HttpContext context, MemoryStream captured, JsonElement requestId)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        captured.SetLength(0);
        captured.Position = 0;
        captured.Write(SerializeError(requestId, McpErrorCode.InternalError, "response_too_large", "The response exceeds its byte limit."));
    }

    private static async ValueTask WriteSafeFailureAsync(HttpContext context, Stream originalBody, JsonElement requestId, (McpInvocationOutcome Outcome, McpInvocationAuditReason Reason) classification, bool auditUnavailable = false, int? statusOverride = null)
    {
        context.Response.Body = originalBody;
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.Clear();
        context.Response.StatusCode = auditUnavailable ? StatusCodes.Status503ServiceUnavailable : statusOverride ?? classification.Outcome switch
        {
            McpInvocationOutcome.Denied => StatusCodes.Status403Forbidden,
            McpInvocationOutcome.Cancelled => StatusCodes.Status408RequestTimeout,
            McpInvocationOutcome.Invalid or McpInvocationOutcome.UnknownTool => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        context.Response.ContentType = "application/json";
        (string reason, string message) = auditUnavailable
            ? ("audit_unavailable", "The required invocation audit could not be recorded.")
            : classification.Outcome switch
        {
            McpInvocationOutcome.Denied => ("forbidden", "The caller is not authorized for this projection."),
            McpInvocationOutcome.Cancelled => ("request_cancelled", "The request was cancelled by the caller."),
            McpInvocationOutcome.Invalid => ("invalid_request", "The request is invalid."),
            McpInvocationOutcome.UnknownTool => ("unknown_tool", "The requested tool is not available."),
            McpInvocationOutcome.Timeout => ("request_timed_out", "The request exceeded its execution limit."),
            _ => ("request_failed", "The request could not be completed."),
        };
        McpErrorCode code = auditUnavailable ? McpErrorCode.InternalError : classification.Outcome switch
        {
            McpInvocationOutcome.Invalid => McpErrorCode.InvalidRequest,
            McpInvocationOutcome.UnknownTool => McpErrorCode.InvalidParams,
            _ => McpErrorCode.InternalError,
        };
        byte[] safe = SerializeError(requestId, code, reason, message);
        try { await originalBody.WriteAsync(safe, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
    }

    private static byte[] SerializeError(JsonElement requestId, McpErrorCode code, string reason, string message)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            requestId.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", (int)code);
            writer.WriteString("message", message);
            writer.WriteStartObject("data");
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return body.ToArray();
    }

    private static async ValueTask CopyResponseAsync(MemoryStream captured, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            captured.Position = 0;
            await captured.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The terminal audit is already committed. A transport-copy
            // failure must not trigger a second immutable audit attempt.
        }
    }

    private static JsonElement EmptyParameters() => JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>());

    private static (Guid? TargetId, Guid? IncidentId) ReadResourceIdentity(JsonElement arguments)
    {
        Guid? target = ReadGuid(arguments, "instanceId") ?? ReadGuid(arguments, "targetId");
        Guid? incident = ReadGuid(arguments, "incidentId") ?? ReadGuid(arguments, "threadId");
        return (target, incident);
    }

    private static Guid? ReadGuid(JsonElement arguments, string propertyName) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid parsed) && parsed != Guid.Empty ? parsed : null;

    private readonly record struct ProtocolMetadata(bool Oversized, bool Invalid, bool Invocation, string? ToolName, JsonElement Parameters, Guid? TargetId, Guid? IncidentId, JsonElement RequestId, bool ReadFailed = false, bool ReadCancelled = false)
    {
        public static ProtocolMetadata Control => new(false, false, false, null, EmptyParameters(), null, null, JsonSerializer.SerializeToElement<object?>(null));
        public static ProtocolMetadata InvalidRequest => new(false, true, true, null, EmptyParameters(), null, null, JsonSerializer.SerializeToElement<object?>(null));
        public static ProtocolMetadata OversizedRequest => new(true, false, true, null, EmptyParameters(), null, null, JsonSerializer.SerializeToElement<object?>(null));
        public static ProtocolMetadata ReadFailureUnknown => new(false, true, false, null, EmptyParameters(), null, null, JsonSerializer.SerializeToElement<object?>(null), true, false);
    }
}

internal sealed record McpPendingAudit(
    Guid InvocationId,
    string Actor,
    string ToolName,
    McpParameterDigest ParameterDigest,
    Guid? TargetId,
    Guid? IncidentId,
    McpInvocationOutcome Outcome,
    McpInvocationAuditReason Reason,
    TimeSpan Duration);
