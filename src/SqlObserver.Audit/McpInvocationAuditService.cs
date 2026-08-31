using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Audit;

/// <summary>Safe invocation identity supplied by the MCP adapter before dispatch.</summary>
public sealed class McpInvocationRequest
{
    public McpInvocationRequest(
        McpClientIdentifier actor,
        McpToolName tool,
        McpActionName action,
        JsonElement parameters,
        AuditCorrelationId correlationId,
        RepositoryCallTimeout operationTimeout,
        MonitoredInstanceId? targetId = null,
        Guid? incidentId = null,
        Guid? invocationId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(operationTimeout);
        if (parameters.ValueKind is JsonValueKind.Undefined)
            throw new ArgumentException("MCP parameters must be a JSON value.", nameof(parameters));
        if (incidentId == Guid.Empty) throw new ArgumentException("An incident identifier cannot be empty.", nameof(incidentId));
        if (invocationId == Guid.Empty) throw new ArgumentException("An invocation identifier cannot be empty.", nameof(invocationId));

        Actor = actor;
        Tool = tool;
        Action = action;
        Parameters = parameters.Clone();
        CorrelationId = correlationId;
        OperationTimeout = operationTimeout;
        TargetId = targetId;
        IncidentId = incidentId;
        InvocationId = invocationId ?? Guid.NewGuid();
    }

    public McpClientIdentifier Actor { get; }
    public McpToolName Tool { get; }
    public McpActionName Action { get; }
    public JsonElement Parameters { get; }
    public AuditCorrelationId CorrelationId { get; }
    public RepositoryCallTimeout OperationTimeout { get; }
    public MonitoredInstanceId? TargetId { get; }
    public Guid? IncidentId { get; }
    public Guid InvocationId { get; }
}

public sealed class McpInvocationRejectedException : Exception
{
    public McpInvocationRejectedException(
        McpInvocationOutcome outcome,
        McpInvocationAuditReason reason,
        McpAuthorizationResult authorization = McpAuthorizationResult.NotApplicable)
    {
        if ((outcome is McpInvocationOutcome.Succeeded or McpInvocationOutcome.Denied) && authorization == McpAuthorizationResult.Allowed)
            throw new ArgumentException("The rejection outcome is not compatible with the authorization result.", nameof(outcome));
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (!Enum.IsDefined(authorization)) throw new ArgumentOutOfRangeException(nameof(authorization));
        if (outcome == McpInvocationOutcome.Denied && authorization == McpAuthorizationResult.NotApplicable)
            authorization = McpAuthorizationResult.Denied;
        Outcome = outcome;
        Reason = reason;
        Authorization = authorization;
    }

    public McpInvocationOutcome Outcome { get; }
    public McpInvocationAuditReason Reason { get; }
    public McpAuthorizationResult Authorization { get; }
}

public sealed class McpResponseOversizeException : Exception
{
    public McpResponseOversizeException() { }
}

public sealed class McpInvocationLimitException : Exception
{
    public McpInvocationLimitException() { }
}

public sealed class McpAuditUnavailableException : Exception
{
    public McpAuditUnavailableException(Exception innerException)
        : base("The required MCP audit record could not be appended; the invocation result is withheld.", innerException) { }
}

/// <summary>
/// Owns the exactly-once terminal audit boundary. Audit append uses a short, independent
/// deadline so a cancelled caller cannot suppress the required record.
/// </summary>
public sealed class McpInvocationAuditService : IMcpInvocationAuditService
{
    public static readonly TimeSpan DefaultAuditTimeout = TimeSpan.FromSeconds(2);

    private readonly IMcpInvocationAuditPort _audit;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _auditTimeout;

    public McpInvocationAuditService(
        IMcpInvocationAuditPort audit,
        TimeProvider? clock = null,
        TimeSpan? auditTimeout = null)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? TimeProvider.System;
        _auditTimeout = auditTimeout ?? DefaultAuditTimeout;
        if (_auditTimeout < TimeSpan.FromMilliseconds(100) || _auditTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(auditTimeout));
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(
        McpInvocationRequest request,
        Func<CancellationToken, ValueTask<TResult>> operation,
        Func<TResult, long> responseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(responseBytes);

        long startedTimestamp = _clock.GetTimestamp();
        TimeSpan Elapsed() => _clock.GetElapsedTime(startedTimestamp);
        McpInvocationAuditRecord? terminalRecord = null;
        TResult? result = default;
        Exception? operationError = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await operation(cancellationToken).ConfigureAwait(false);
            long bytes = responseBytes(result);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(responseBytes));
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.Succeeded, McpInvocationAuditReason.Completed, Elapsed(), bytes);
        }
        catch (McpInvocationRejectedException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, exception.Authorization, exception.Outcome, exception.Reason, Elapsed(), 0);
        }
        catch (McpResponseOversizeException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.Oversize, McpInvocationAuditReason.ResponseOversize, Elapsed(), 0);
        }
        catch (McpApplicationResponseOversizeException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.Oversize, McpInvocationAuditReason.ResponseOversize, Elapsed(), 0);
        }
        catch (McpInvocationLimitException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.Limited, McpInvocationAuditReason.ConcurrencyLimit, Elapsed(), 0);
        }
        catch (OperationCanceledException exception)
        {
            operationError = exception;
            McpInvocationOutcome outcome = cancellationToken.IsCancellationRequested ? McpInvocationOutcome.Cancelled : McpInvocationOutcome.Timeout;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, outcome, outcome == McpInvocationOutcome.Cancelled ? McpInvocationAuditReason.Cancelled : McpInvocationAuditReason.Timeout, Elapsed(), 0);
        }
        catch (TimeoutException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.Timeout, McpInvocationAuditReason.Timeout, Elapsed(), 0);
        }
        catch (ArgumentException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.NotApplicable, McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest, Elapsed(), 0);
        }
        catch (InvalidDataException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.NotApplicable, McpInvocationOutcome.Invalid, McpInvocationAuditReason.InvalidRequest, Elapsed(), 0);
        }
        catch (UnauthorizedAccessException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Denied, McpInvocationOutcome.Denied, McpInvocationAuditReason.AuthorizationDenied, Elapsed(), 0);
        }
        catch (KeyNotFoundException exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.NotApplicable, McpInvocationOutcome.UnknownTool, McpInvocationAuditReason.UnknownTool, Elapsed(), 0);
        }
        catch (Exception exception)
        {
            operationError = exception;
            terminalRecord = CreateRecord(request, McpAuthorizationResult.Allowed, McpInvocationOutcome.RepositoryFailure, McpInvocationAuditReason.RepositoryFailure, Elapsed(), 0);
        }

        await AppendTerminalAsync(terminalRecord).ConfigureAwait(false);
        if (operationError is not null) throw operationError;
        return result!;
    }

    private static McpInvocationAuditRecord CreateRecord(
        McpInvocationRequest request,
        McpAuthorizationResult authorization,
        McpInvocationOutcome outcome,
        McpInvocationAuditReason reason,
        TimeSpan elapsed,
        long responseBytes) => new(
            request.InvocationId,
            request.Actor,
            request.Tool,
            request.Action,
            authorization,
            outcome,
            reason,
            request.CorrelationId,
            McpParameterCanonicalizer.Digest(request.Parameters),
            elapsed,
            responseBytes,
            request.TargetId,
            request.IncidentId,
            reason switch
            {
                McpInvocationAuditReason.Completed => "completed",
                McpInvocationAuditReason.AuthorizationDenied => "authorization_denied",
                McpInvocationAuditReason.InvalidRequest => "invalid_request",
                McpInvocationAuditReason.UnknownTool => "unknown_tool",
                McpInvocationAuditReason.Timeout => "timeout",
                McpInvocationAuditReason.Cancelled => "cancelled",
                McpInvocationAuditReason.ConcurrencyLimit => "concurrency_limit",
                McpInvocationAuditReason.ResponseOversize => "response_oversize",
                McpInvocationAuditReason.RepositoryFailure => "repository_failure",
                _ => "unknown"
            });

    /// <summary>Appends one terminal record with an independent bounded audit deadline.</summary>
    public async ValueTask<McpInvocationAuditReceipt> AppendTerminalAsync(McpInvocationAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var timeout = new CancellationTokenSource(_auditTimeout);
        try
        {
            return await _audit.AppendAsync(
                new AppendMcpInvocationAuditRequest(record, new RepositoryCallTimeout(_auditTimeout)),
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new McpAuditUnavailableException(exception);
        }
    }
}
