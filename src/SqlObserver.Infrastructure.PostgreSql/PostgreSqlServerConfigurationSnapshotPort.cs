using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlServerConfigurationSnapshotPort(NpgsqlDataSource dataSource)
    : IServerConfigurationSnapshotRepositoryPort
{
    public async ValueTask<ServerConfigurationSnapshotPage> ReadAsync(
        ServerConfigurationSnapshotRequest request, CancellationToken cancellationToken)
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
                SELECT target_revision,snapshot_run_id,snapshot_observed_at,
                       server_major_version,collector_enabled,last_outcome,
                       has_more,configuration_id,configuration_name,source_type,
                       configured_value,in_use_value,is_dynamic,is_advanced
                FROM reporting.list_server_configuration_snapshot(
                    @target,@max_results,@after_id,@expected_revision,@expected_run);
                """, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("max_results", request.Limit);
            command.Parameters.Add("after_id", NpgsqlDbType.Integer).Value =
                (object?)request.Cursor?.ConfigurationId ?? DBNull.Value;
            command.Parameters.Add("expected_revision", NpgsqlDbType.Bigint).Value =
                (object?)request.Cursor?.TargetRevision ?? DBNull.Value;
            command.Parameters.Add("expected_run", NpgsqlDbType.Uuid).Value =
                (object?)request.Cursor?.RunId ?? DBNull.Value;
            var items = new List<ServerConfigurationSetting>(request.Limit);
            long? revision = null;
            Guid? runId = null;
            DateTimeOffset? observedAt = null;
            short? majorVersion = null;
            bool enabled = false;
            string? lastOutcome = null;
            bool hasMore = false;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    long rowRevision = reader.GetInt64(0);
                    Guid? rowRun = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                    DateTimeOffset? rowObserved = reader.IsDBNull(2) ? null :
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2);
                    short? rowMajor = reader.IsDBNull(3) ? null : reader.GetInt16(3);
                    bool rowEnabled = reader.GetBoolean(4);
                    string? rowOutcome = reader.IsDBNull(5) ? null : reader.GetString(5);
                    bool rowMore = reader.GetBoolean(6);
                    if (revision is not null && (revision != rowRevision || runId != rowRun ||
                        observedAt != rowObserved || majorVersion != rowMajor ||
                        enabled != rowEnabled || lastOutcome != rowOutcome || hasMore != rowMore))
                        throw new InvalidDataException("Configuration snapshot header changed within one read.");
                    revision = rowRevision;
                    runId = rowRun;
                    observedAt = rowObserved;
                    majorVersion = rowMajor;
                    enabled = rowEnabled;
                    lastOutcome = rowOutcome;
                    hasMore = rowMore;
                    if (reader.IsDBNull(7)) continue;
                    if (items.Count == request.Limit)
                        throw new InvalidDataException("Configuration snapshot exceeded its row bound.");
                    items.Add(new ServerConfigurationSetting(reader.GetInt32(7),
                        reader.GetString(8), reader.GetString(9), reader.GetString(10),
                        reader.GetString(11), reader.GetBoolean(12), reader.GetBoolean(13)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            if (revision is null || hasMore && items.Count == 0)
                throw new InvalidDataException("Configuration snapshot header is missing.");
            ServerConfigurationSnapshotCursor? next = hasMore && runId is not null
                ? new ServerConfigurationSnapshotCursor(request.TargetId,
                    revision.Value, runId.Value, items[^1].ConfigurationId)
                : null;
            return new ServerConfigurationSnapshotPage(request.TargetId, revision.Value,
                runId, observedAt, majorVersion, enabled, lastOutcome, items, next);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("Configuration target scope was rejected.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "40001")
        {
            throw new ServerConfigurationSnapshotCursorStaleException(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("configuration snapshot", exception);
        }
    }
}
