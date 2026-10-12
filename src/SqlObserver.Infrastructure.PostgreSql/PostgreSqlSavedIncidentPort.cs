using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlSavedIncidentPort(NpgsqlDataSource dataSource) : ISavedIncidentRepositoryPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async ValueTask<SavedIncident> SaveAsync(SaveIncidentRequest request, string actorSid, Guid correlationId,
        RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SavedIncident? result = await ExecuteAsync<SavedIncident>(request.TargetId, timeout, command =>
        {
            command.CommandText = """
                SELECT control.save_incident(@target,@revision,@operation,@from,@to,@title,@notes,@actor,@correlation);
                """;
            command.Parameters.AddWithValue("revision", request.ExpectedTargetRevision);
            command.Parameters.AddWithValue("operation", request.OperationId);
            command.Parameters.AddWithValue("from", request.FromUtc);
            command.Parameters.AddWithValue("to", request.ToUtc);
            command.Parameters.AddWithValue("title", request.Title);
            command.Parameters.AddWithValue("notes", request.Notes);
            command.Parameters.AddWithValue("actor", actorSid);
            command.Parameters.AddWithValue("correlation", correlationId);
        }, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidDataException("The incident save returned no receipt.");
    }

    public ValueTask<SavedIncident?> ReadAsync(Guid targetId, Guid threadId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken) => ExecuteAsync<SavedIncident>(targetId, timeout, command =>
        {
            command.CommandText = "SELECT reporting.read_saved_incident(@target,@thread);";
            command.Parameters.AddWithValue("thread", threadId);
        }, cancellationToken);

    public async ValueTask<SavedIncidentListPage> ListAsync(SavedIncidentListRequest request, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken) => await ExecuteAsync<SavedIncidentListPage>(request.TargetId, timeout, command =>
        {
            command.CommandText = "SELECT reporting.list_saved_incidents(@target,@limit,@revision,@snapshot,@at,@thread);";
            command.Parameters.AddWithValue("limit", request.Limit);
            command.Parameters.AddWithValue("revision", NpgsqlTypes.NpgsqlDbType.Bigint, (object?)request.Cursor?.PublicationRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("snapshot", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)request.Cursor?.SnapshotUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("at", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)request.Cursor?.SavedAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("thread", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)request.Cursor?.ThreadId ?? DBNull.Value);
        }, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The saved incident list returned no page.");

    private async ValueTask<T?> ExecuteAsync<T>(Guid targetId, RepositoryCallTimeout timeout,
        Action<NpgsqlCommand> configure, CancellationToken cancellationToken) where T : class
    {
        using var deadline = PostgreSqlRuntimeSupport.CreateTimeoutScope(timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, deadline.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout);
                scope.Parameters.AddWithValue("scope", targetId.ToString("D"));
                await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction,
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
            command.Parameters.AddWithValue("target", targetId);
            configure(command);
            object? value = await command.ExecuteScalarAsync(deadline.Token).ConfigureAwait(false);
            T? result = value is null or DBNull ? null : JsonSerializer.Deserialize<T>((string)value, JsonOptions)
                ?? throw new InvalidDataException("The saved incident response is invalid.");
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.SerializationFailure)
        { throw new SavedIncidentConflictException(); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        { throw new UnauthorizedAccessException("Incident target scope was denied."); }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InvalidParameterValue)
        { throw new ArgumentException("The saved incident request is invalid."); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("saved incident", error); }
    }
}
