using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlServerConfigurationChangePort(NpgsqlDataSource dataSource)
    : IServerConfigurationChangeRepositoryPort
{
    public async ValueTask<ServerConfigurationChangePage> ReadAsync(
        ServerConfigurationChangeRequest request, CancellationToken cancellationToken)
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
                SELECT target_revision,publication_generation,has_more,
                       configuration_id,configuration_name,source_type,
                       previous_configured_value,configured_value,
                       previous_in_use_value,in_use_value,configured_changed,
                       in_use_changed,previous_observed_at,observed_at,
                       detected_at,run_id
                FROM reporting.list_server_configuration_changes(
                    @target,@from_utc,@to_utc,@max_results,@after_at,@after_run,
                    @after_id,@expected_revision,@expected_generation);
                """, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            command.Parameters.AddWithValue("max_results", request.Limit);
            command.Parameters.Add("after_at", NpgsqlDbType.TimestampTz).Value =
                (object?)request.Cursor?.DetectedAtUtc ?? DBNull.Value;
            command.Parameters.Add("after_run", NpgsqlDbType.Uuid).Value =
                (object?)request.Cursor?.RunId ?? DBNull.Value;
            command.Parameters.Add("after_id", NpgsqlDbType.Integer).Value =
                (object?)request.Cursor?.ConfigurationId ?? DBNull.Value;
            command.Parameters.Add("expected_revision", NpgsqlDbType.Bigint).Value =
                (object?)request.Cursor?.TargetRevision ?? DBNull.Value;
            command.Parameters.Add("expected_generation", NpgsqlDbType.Bigint).Value =
                (object?)request.Cursor?.PublicationGeneration ?? DBNull.Value;
            var items = new List<ServerConfigurationChange>(request.Limit);
            long? revision = null;
            long? generation = null;
            bool hasMore = false;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    long rowRevision = reader.GetInt64(0);
                    long rowGeneration = reader.GetInt64(1);
                    bool rowHasMore = reader.GetBoolean(2);
                    if (revision is not null && (revision != rowRevision ||
                        generation != rowGeneration || hasMore != rowHasMore))
                        throw new InvalidDataException("Configuration page header changed within one read.");
                    revision = rowRevision;
                    generation = rowGeneration;
                    hasMore = rowHasMore;
                    if (reader.IsDBNull(3)) continue;
                    if (items.Count == request.Limit)
                        throw new InvalidDataException("Configuration page exceeded its row bound.");
                    items.Add(new ServerConfigurationChange(reader.GetInt32(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        reader.GetString(7), reader.GetString(8), reader.GetString(9),
                        reader.GetBoolean(10), reader.GetBoolean(11),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 12),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 13),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 14),
                        reader.GetGuid(15)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            if (revision is null || generation is null || hasMore && items.Count == 0)
                throw new InvalidDataException("Configuration page header is missing.");
            ServerConfigurationChange? last = items.Count == 0 ? null : items[^1];
            ServerConfigurationChangeCursor? next = hasMore && last is not null
                ? new ServerConfigurationChangeCursor(request.TargetId, request.FromUtc,
                    request.ToUtc, revision.Value, generation.Value,
                    last.DetectedAtUtc, last.RunId, last.ConfigurationId)
                : null;
            return new ServerConfigurationChangePage(request.TargetId, request.FromUtc,
                request.ToUtc, revision.Value, generation.Value, items, next);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("Configuration target scope was rejected.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "40001")
        {
            throw new ServerConfigurationCursorStaleException(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("configuration history", exception);
        }
    }
}
