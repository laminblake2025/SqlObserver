using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Append-only administrative audit for authorization denials without a target mutation.</summary>
public sealed class PostgreSqlAdministrativeAuditPort : IAdministrativeAuditPort
{
    private const string AppendDeniedSql = """
        SELECT audit_activity_id, repository_time
        FROM audit.append_denied_m8_administrative_activity(
            @actor_identifier,
            @correlation_id,
            @audit_action,
            @target_id,
            @reason);
        """;
    // Kept as the reviewed denial SQL field for existing security contracts.
    private const string AppendSql = AppendDeniedSql;
    private const string AppendOutcomeSql = """
        SELECT audit_activity_id, repository_time
        FROM audit.append_m8_administrative_activity(
            @actor_identifier,
            @correlation_id,
            @audit_action,
            @target_id,
            @operation_id,
            @authorization_result,
            @operation_outcome,
            @reason,
            @safe_details::jsonb);
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
        bool denied = record.AuthorizationDecision == AdministrativeAuthorizationDecision.Denied;
        (string auditAction, string reason) = ValidateRecord(record, denied);
        // All M8 outcomes, including authorization denials, carry the canonical
        // operation identity through one audited SQL contract.  AppendDeniedSql
        // remains for the legacy static contract but is not used for M8 records.
        bool legacyTargetDenial = denied && record.Envelope.Action is AdministrativeAuditAction.RegisterObservationTarget or AdministrativeAuditAction.UpdateObservationTarget or AdministrativeAuditAction.RetireObservationTarget;
        string appendSql = legacyTargetDenial ? AppendDeniedSql : AppendOutcomeSql;
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(appendSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("actor_identifier", record.Envelope.ActorSid.Value);
            command.Parameters.AddWithValue("correlation_id", record.Envelope.CorrelationId.Value);
            command.Parameters.AddWithValue("audit_action", auditAction);
            command.Parameters.AddWithValue("target_id", record.Envelope.TargetId.Value);
            command.Parameters.AddWithValue("reason", reason);
            if (!legacyTargetDenial && record.OperationId is null) throw new InvalidDataException("M8 outcome audit requires a canonical operation identity.");
            if (!legacyTargetDenial)
            {
                Guid operationId = record.OperationId ?? throw new InvalidDataException("M8 outcome audit requires a canonical operation identity.");
                command.Parameters.AddWithValue("operation_id", operationId);
                command.Parameters.AddWithValue("authorization_result", denied ? "denied" : "allowed");
                command.Parameters.AddWithValue("operation_outcome", record.Outcome switch
                {
                    AdministrativeOperationOutcome.Succeeded => "succeeded",
                    AdministrativeOperationOutcome.Failed => "failed",
                    AdministrativeOperationOutcome.Conflict => "conflict",
                    AdministrativeOperationOutcome.Denied => "rejected",
                    _ => throw new InvalidDataException("The M8 outcome audit contains an unsupported outcome.")
                });
                command.Parameters.AddWithValue("safe_details", System.Text.Json.JsonSerializer.Serialize(new { operationId = operationId.ToString("D"), detail = record.SafeDetails }));
            }

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL administrative audit returned no receipt.");
            }

            AdministrativeAuditReceipt receipt = new(
                new AdministrativeAuditId(reader.GetGuid(0)),
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1));
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return receipt;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("administrative audit append", exception);
        }
    }

    private static (string AuditAction, string Reason) ValidateRecord(
        AdministrativeAuditRecord record,
        bool denied)
    {
        if (denied && record.Outcome != AdministrativeOperationOutcome.Denied)
        {
            throw new InvalidDataException("A denied audit record must have a denied outcome.");
        }
        if (!denied && record.AuthorizationDecision != AdministrativeAuthorizationDecision.Granted)
        {
            throw new InvalidDataException("A non-denied audit record must be granted.");
        }

        if (denied && record.Envelope.Action is AdministrativeAuditAction.RegisterObservationTarget or AdministrativeAuditAction.UpdateObservationTarget or AdministrativeAuditAction.RetireObservationTarget)
        {
            if (record.Reason is not (AdministrativeAuditReason.PrincipalDisabled or AdministrativeAuditReason.RequiredRoleMissing or AdministrativeAuditReason.TargetOutOfScope))
                throw new InvalidDataException("The target denial adapter received a non-denial reason.");
        }
        string auditAction = record.Envelope.Action switch
        {
            AdministrativeAuditAction.RegisterObservationTarget => "register_observation_target",
            AdministrativeAuditAction.UpdateObservationTarget => "update_observation_target",
            AdministrativeAuditAction.RetireObservationTarget => "retire_observation_target",
            AdministrativeAuditAction.RequestCapabilityRediscovery => "request_capability_rediscovery",
            AdministrativeAuditAction.CreateAlertRule => "alert.rule.create",
            AdministrativeAuditAction.UpdateAlertRule => "alert.rule.update",
            AdministrativeAuditAction.RetireAlertRule => "alert.rule.retire",
            AdministrativeAuditAction.CreateMaintenanceWindow => "alert.maintenance.create",
            AdministrativeAuditAction.UpdateMaintenanceWindow => "alert.maintenance.update",
            AdministrativeAuditAction.RetireMaintenanceWindow => "alert.maintenance.retire",
            AdministrativeAuditAction.AcknowledgeAlert => "alert.acknowledge",
            AdministrativeAuditAction.ConfigureAlertDestination => "alert.destination.configure",
            AdministrativeAuditAction.UpdateAlertDestination => "alert.destination.update",
            AdministrativeAuditAction.RetireAlertDestination => "alert.destination.retire",
            AdministrativeAuditAction.CancelAlertDelivery => "alert.delivery.cancel",
            AdministrativeAuditAction.ApproveAlertDestination => "alert.destination.approve",
            _ => throw new InvalidDataException("The denial audit adapter received an unsupported action."),
        };
        string reason = record.Reason switch
        {
            AdministrativeAuditReason.PrincipalDisabled => "principal_disabled",
            AdministrativeAuditReason.RequiredRoleMissing => "required_role_missing",
            AdministrativeAuditReason.TargetOutOfScope => "target_out_of_scope",
            AdministrativeAuditReason.AlreadyExists => "already_exists",
            AdministrativeAuditReason.RevisionConflict => "revision_conflict",
            AdministrativeAuditReason.TargetNotFound => "target_not_found",
            AdministrativeAuditReason.TargetRetired => "target_retired",
            AdministrativeAuditReason.DiscoveryFailed => "discovery_failed",
            AdministrativeAuditReason.RepositoryFailure => "repository_failure",
            AdministrativeAuditReason.InvalidRequest => "invalid_request",
            AdministrativeAuditReason.IdempotentReplay => "idempotent_replay",
            _ => throw new InvalidDataException("The denial audit adapter received an unsupported reason."),
        };
        return (auditAction, reason);
    }
}
