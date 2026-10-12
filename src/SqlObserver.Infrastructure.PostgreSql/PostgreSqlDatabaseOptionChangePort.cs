using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlDatabaseOptionChangePort(NpgsqlDataSource dataSource)
    : IDatabaseOptionChangeRepositoryPort
{
    public async ValueTask<DatabaseOptionChangeResult> ReadAsync(
        DatabaseOptionChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(timeout.Token)
                .ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction,
                request.Timeout, timeout.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("""
                SELECT database_id,database_name,option_key,previous_value,current_value,
                       previous_observed_at,observed_at,detected_at,run_id
                FROM reporting.list_database_option_changes(
                    @target,@from_utc,@to_utc,@max_results);
                """, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            command.Parameters.AddWithValue("max_results", request.Limit);
            var items = new List<DatabaseOptionChange>(request.Limit);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    items.Add(new DatabaseOptionChange(reader.GetInt32(0), reader.GetString(1),
                        reader.GetString(2), reader.GetString(3), reader.GetString(4),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 5),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 6),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 7), reader.GetGuid(8)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new DatabaseOptionChangeResult(request.TargetId, request.FromUtc,
                request.ToUtc, items);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("Database option target scope was rejected.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("database option history", exception);
        }
    }
}
