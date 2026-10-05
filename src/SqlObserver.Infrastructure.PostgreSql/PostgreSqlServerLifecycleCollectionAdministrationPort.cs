using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlServerLifecycleCollectionAdministrationPort(
    NpgsqlDataSource dataSource) : IServerLifecycleCollectionAdministrationPort
{
    public async ValueTask<ServerLifecycleCollectionState?> GetAsync(SqlObserver.Domain.Telemetry.MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(timeout);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(targetRevision);
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
        await using var connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, deadline.Token).ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true)", connection, transaction))
        { scope.Parameters.AddWithValue("scope", targetId.Value.ToString("D")); await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false); }
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.read_server_lifecycle_collection_state(@target,@revision)", connection, transaction)
        { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
        command.Parameters.AddWithValue("target",targetId.Value); command.Parameters.AddWithValue("revision",targetRevision);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false);
        if (!await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) return null;
        var result = new ServerLifecycleCollectionState(reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2));
        if (await reader.ReadAsync(deadline.Token).ConfigureAwait(false)) throw new InvalidDataException("Duplicate Lifecycle schedule state.");
        await reader.DisposeAsync().ConfigureAwait(false); await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
        return result;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new ServerLifecycleCollectionException("history_request_timed_out", "Lifecycle collection state request timed out.",503,error); }
        catch (NpgsqlException error)
        { throw new ServerLifecycleCollectionException("history_repository_unavailable", "Lifecycle collection state is temporarily unavailable.",503,error); }
    }

    public async ValueTask<ServerLifecycleCollectionMutationResult> SetAsync(
        SetServerLifecycleCollectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TargetId);
        ArgumentNullException.ThrowIfNull(request.Audit);
        ArgumentNullException.ThrowIfNull(request.Timeout);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(request.ExpectedTargetRevision);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(request.ExpectedScheduleRevision);
        if (request.Audit.Action != SqlObserver.Domain.Auditing.AdministrativeAuditAction.UpdateObservationTarget ||
            request.Audit.TargetId != request.TargetId || request.ExpectedTargetRevision < 1 ||
            request.ExpectedEnabled != request.Enabled && request.ExpectedScheduleRevision == SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.MaximumRevision)
            throw new ArgumentException("Lifecycle collection audit or target is invalid.", nameof(request));
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT result_status,schedule_revision,collector_enabled
                FROM control.set_server_lifecycle_collection_enabled(
                    @target,@revision,@schedule,@expected,@enabled,@actor,@correlation);
                """);
            command.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout);
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("revision", request.ExpectedTargetRevision);
            command.Parameters.AddWithValue("schedule", request.ExpectedScheduleRevision);
            command.Parameters.AddWithValue("expected", request.ExpectedEnabled);
            command.Parameters.AddWithValue("enabled", request.Enabled);
            command.Parameters.AddWithValue("actor", request.Audit.ActorSid.Value);
            command.Parameters.AddWithValue("correlation", request.Audit.CorrelationId.Value);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                throw new InvalidDataException("Lifecycle collection mutation returned no status.");
            string status = reader.GetString(0);
            long? revision = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            bool? enabled = reader.IsDBNull(2) ? null : reader.GetBoolean(2);
            if (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                throw new InvalidDataException("Lifecycle collection mutation returned multiple statuses.");
            return new ServerLifecycleCollectionMutationResult(status switch
            {
                "applied" => ServerLifecycleCollectionMutationStatus.Applied,
                "not_found" => ServerLifecycleCollectionMutationStatus.NotFound,
                "inactive" => ServerLifecycleCollectionMutationStatus.Inactive,
                "revision_conflict" => ServerLifecycleCollectionMutationStatus.RevisionConflict,
                _ => throw new InvalidDataException("Lifecycle collection mutation returned an unknown status."),
            }, revision, enabled);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServerLifecycleCollectionException("history_request_timed_out", "Lifecycle collection change request timed out.",503,exception);
        }
        catch (NpgsqlException error)
        { throw new ServerLifecycleCollectionException("history_repository_unavailable", "Lifecycle collection change is temporarily unavailable.",503,error); }
    }
}
