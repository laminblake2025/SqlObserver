using System.Data;
using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlBufferNodePleWindowPort(NpgsqlDataSource dataSource)
    : IBufferNodePleWindowRepositoryPort
{
    public async ValueTask<BufferNodePleWindowResult> ReadAsync(
        BufferNodePleWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, timeout.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction,
                request.Timeout, timeout.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target_scope,true);",
                connection, transaction))
            {
                scope.Parameters.AddWithValue("target_scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }

            var nodes = new List<BufferNodePleNode>(128);
            await using (var command = Command("""
                SELECT node_id,sample_count,last_observed_at
                FROM reporting.list_buffer_node_ple_nodes(@target,@from_utc,@to_utc);
                """, connection, transaction, request))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (nodes.Count == 128)
                        throw new InvalidDataException("Buffer Node PLE node list exceeded its bound.");
                    nodes.Add(new BufferNodePleNode(reader.GetInt32(0), reader.GetInt64(1),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 2)));
                }
            }

            int? selected = request.NodeId ?? (nodes.Count == 0 ? null : nodes[0].NodeId);
            var points = new List<BufferNodePlePoint>(169);
            if (selected is int nodeId)
            {
                await using var command = Command("""
                    SELECT bucket_start_utc,sample_count,minimum_seconds,
                           average_seconds,maximum_seconds
                    FROM reporting.get_buffer_node_ple_trend(
                        @target,@node,@from_utc,@to_utc);
                    """, connection, transaction, request);
                command.Parameters.AddWithValue("node", nodeId);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (points.Count == 169)
                        throw new InvalidDataException("Buffer Node PLE trend exceeded its hourly bound.");
                    points.Add(new BufferNodePlePoint(
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0), reader.GetInt64(1),
                        reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
                }
            }

            var aggregatePoints = new List<BufferNodePlePoint>(169);
            await using (var command = Command("""
                SELECT bucket_start_utc,sample_count,minimum_seconds,
                       average_seconds,maximum_seconds
                FROM reporting.get_buffer_manager_ple_trend(@target,@from_utc,@to_utc);
                """, connection, transaction, request))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (aggregatePoints.Count == 169)
                        throw new InvalidDataException("Buffer Manager PLE trend exceeded its hourly bound.");
                    aggregatePoints.Add(new BufferNodePlePoint(
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0), reader.GetInt64(1),
                        reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
                }
            }

            await using var gaps = Command("""
                SELECT reporting.count_buffer_node_ple_gaps(@target,@from_utc,@to_utc);
                """, connection, transaction, request);
            object? count = await gaps.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);
            if (count is not long gapCount)
                throw new InvalidDataException("Buffer Node PLE gap count was missing.");
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new BufferNodePleWindowResult(request.TargetId, request.FromUtc, request.ToUtc,
                selected, gapCount, nodes, points, aggregatePoints);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("The Buffer Node PLE target scope was rejected.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "22023")
        {
            throw new ArgumentException("The Buffer Node PLE window bounds are invalid.", nameof(request), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("Buffer Node PLE window read", exception);
        }
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection,
        NpgsqlTransaction transaction, BufferNodePleWindowRequest request)
    {
        var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        command.Parameters.AddWithValue("target", request.TargetId.Value);
        command.Parameters.AddWithValue("from_utc", request.FromUtc);
        command.Parameters.AddWithValue("to_utc", request.ToUtc);
        return command;
    }
}
