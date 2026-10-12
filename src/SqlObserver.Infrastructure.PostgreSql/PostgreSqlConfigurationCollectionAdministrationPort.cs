using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlConfigurationCollectionAdministrationPort(
    NpgsqlDataSource dataSource) : IConfigurationCollectionAdministrationPort
{
    public async ValueTask<ConfigurationCollectionMutationResult> SetAsync(
        SetConfigurationCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Audit.Action != SqlObserver.Domain.Auditing.AdministrativeAuditAction.UpdateObservationTarget ||
            request.Audit.TargetId != request.TargetId || request.ExpectedTargetRevision < 1)
            throw new ArgumentException("Configuration collection audit or target is invalid.", nameof(request));
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT result_status,schedule_revision,collector_enabled
                FROM control.set_configuration_collection_enabled(
                    @target,@revision,@expected,@enabled,@actor,@correlation);
                """);
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("revision", request.ExpectedTargetRevision);
            command.Parameters.AddWithValue("expected", request.ExpectedEnabled);
            command.Parameters.AddWithValue("enabled", request.Enabled);
            command.Parameters.AddWithValue("actor", request.Audit.ActorSid.Value);
            command.Parameters.AddWithValue("correlation", request.Audit.CorrelationId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                throw new InvalidDataException("Configuration collection mutation returned no status.");
            string status = reader.GetString(0);
            long? revision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            bool? enabled = reader.IsDBNull(2) ? null : reader.GetBoolean(2);
            if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                throw new InvalidDataException("Configuration collection mutation returned multiple statuses.");
            return new ConfigurationCollectionMutationResult(status switch
            {
                "applied" => ConfigurationCollectionMutationStatus.Applied,
                "not_found" => ConfigurationCollectionMutationStatus.NotFound,
                "inactive" => ConfigurationCollectionMutationStatus.Inactive,
                "revision_conflict" => ConfigurationCollectionMutationStatus.RevisionConflict,
                _ => throw new InvalidDataException("Configuration collection mutation returned an unknown status."),
            }, revision, enabled);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("configuration collection mutation", exception);
        }
    }
}
