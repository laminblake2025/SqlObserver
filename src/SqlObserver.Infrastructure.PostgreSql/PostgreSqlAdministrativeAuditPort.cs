using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Append-only administrative audit for authorization denials without a target mutation.</summary>
public sealed class PostgreSqlAdministrativeAuditPort : IAdministrativeAuditPort
{
    private const string AppendSql = """
        SELECT audit_activity_id, repository_time
        FROM audit.append_denied_administrative_activity(
            @actor_identifier,
            @correlation_id,
            @audit_action,
            @target_id,
            @reason);
        """;

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlAdministrativeAuditPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<AdministrativeAuditReceipt> AppendAsync(
        AppendAdministrativeAuditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AdministrativeAuditRecord record = request.Record;
        (string auditAction, string reason) = ValidateDeniedRecord(record);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var command = new NpgsqlCommand(AppendSql, connection)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("actor_identifier", record.Envelope.ActorSid.Value);
            command.Parameters.AddWithValue("correlation_id", record.Envelope.CorrelationId.Value);
            command.Parameters.AddWithValue("audit_action", auditAction);
            command.Parameters.AddWithValue("target_id", record.Envelope.TargetId.Value);
            command.Parameters.AddWithValue("reason", reason);

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL administrative audit returned no receipt.");
            }

            return new AdministrativeAuditReceipt(
                new AdministrativeAuditId(reader.GetGuid(0)),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("administrative audit append", exception);
        }
    }

    private static (string AuditAction, string Reason) ValidateDeniedRecord(
        AdministrativeAuditRecord record)
    {
        if (record.AuthorizationDecision != AdministrativeAuthorizationDecision.Denied ||
            record.Outcome != AdministrativeOperationOutcome.Denied)
        {
            throw new InvalidDataException("Only denied administrative activity may use the denial audit adapter.");
        }

        string auditAction = record.Envelope.Action switch
        {
            AdministrativeAuditAction.RegisterObservationTarget => "register_observation_target",
            AdministrativeAuditAction.UpdateObservationTarget => "update_observation_target",
            AdministrativeAuditAction.RetireObservationTarget => "retire_observation_target",
            AdministrativeAuditAction.RequestCapabilityRediscovery => "request_capability_rediscovery",
            _ => throw new InvalidDataException("The denial audit adapter received an unsupported action."),
        };
        string reason = record.Reason switch
        {
            AdministrativeAuditReason.PrincipalDisabled => "principal_disabled",
            AdministrativeAuditReason.RequiredRoleMissing => "required_role_missing",
            AdministrativeAuditReason.TargetOutOfScope => "target_out_of_scope",
            _ => throw new InvalidDataException("The denial audit adapter received an unsupported reason."),
        };
        return (auditAction, reason);
    }
}
