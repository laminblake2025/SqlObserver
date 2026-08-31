using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>PostgreSQL append-only persistence for MCP terminal audit records.</summary>
public class PostgreSqlMcpInvocationAuditPort : IMcpAuditPort
{
    private const string AppendSql = """
        SELECT invocation_id, recorded_at
        FROM audit.append_mcp_invocation(
            @invocation_id, @actor_identifier, @tool_name, @action_name,
            @authorization_result, @outcome, @reason, @target_id, @incident_id,
            @correlation_id, @parameter_digest, @duration_ms, @response_bytes,
            @safe_detail);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlMcpInvocationAuditPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<McpInvocationAuditReceipt> AppendAsync(
        AppendMcpInvocationAuditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        McpInvocationAuditRecord record = request.Record;

        // Deliberately do not link the caller token: required audit must still be
        // attempted after an MCP caller disconnects. The bounded request timeout
        // remains the only cancellation source for this append.
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            CancellationToken.None);
        await using NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(timeout.Token)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(AppendSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        command.Parameters.AddWithValue("invocation_id", record.InvocationId);
        command.Parameters.AddWithValue("actor_identifier", record.Actor.Value);
        command.Parameters.AddWithValue("tool_name", record.Tool.Value);
        command.Parameters.AddWithValue("action_name", record.Action.Value);
        command.Parameters.AddWithValue("authorization_result", MapAuthorization(record.Authorization));
        command.Parameters.AddWithValue("outcome", MapOutcome(record.Outcome));
        command.Parameters.AddWithValue("reason", MapReason(record.Reason));
        command.Parameters.Add(new NpgsqlParameter<Guid?>("target_id", NpgsqlDbType.Uuid) { TypedValue = record.TargetId?.Value });
        command.Parameters.Add(new NpgsqlParameter<Guid?>("incident_id", NpgsqlDbType.Uuid) { TypedValue = record.IncidentId });
        command.Parameters.AddWithValue("correlation_id", record.CorrelationId.Value);
        command.Parameters.Add(new NpgsqlParameter<byte[]>("parameter_digest", NpgsqlDbType.Bytea) { TypedValue = record.ParameterDigest.Value });
        command.Parameters.AddWithValue("duration_ms", checked((long)Math.Ceiling(record.Duration.TotalMilliseconds)));
        command.Parameters.AddWithValue("response_bytes", record.ResponseBytes);
        command.Parameters.AddWithValue("safe_detail", record.SafeDetail);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(timeout.Token)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            throw new InvalidOperationException("PostgreSQL MCP audit returned no receipt.");

        return new McpInvocationAuditReceipt(
            reader.GetGuid(0),
            PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1));
    }

    private static string MapAuthorization(McpAuthorizationResult value) => value switch
    {
        McpAuthorizationResult.Allowed => "allowed",
        McpAuthorizationResult.Denied => "denied",
        McpAuthorizationResult.NotApplicable => "not_applicable",
        _ => throw new InvalidDataException("Unknown MCP authorization result."),
    };

    private static string MapOutcome(McpInvocationOutcome value) => value switch
    {
        McpInvocationOutcome.Succeeded => "succeeded",
        McpInvocationOutcome.Denied => "denied",
        McpInvocationOutcome.Invalid => "invalid",
        McpInvocationOutcome.UnknownTool => "unknown_tool",
        McpInvocationOutcome.Timeout => "timeout",
        McpInvocationOutcome.Cancelled => "cancelled",
        McpInvocationOutcome.Limited => "limited",
        McpInvocationOutcome.Oversize => "oversize",
        McpInvocationOutcome.RepositoryFailure => "repository_failure",
        _ => throw new InvalidDataException("Unknown MCP invocation outcome."),
    };

    private static string MapReason(McpInvocationAuditReason value) => value switch
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
        _ => throw new InvalidDataException("Unknown MCP invocation audit reason."),
    };
}

/// <summary>Short composition-friendly name for the PostgreSQL MCP audit adapter.</summary>
public sealed class PostgreSqlMcpAuditPort(NpgsqlDataSource dataSource)
    : PostgreSqlMcpInvocationAuditPort(dataSource)
{
}
