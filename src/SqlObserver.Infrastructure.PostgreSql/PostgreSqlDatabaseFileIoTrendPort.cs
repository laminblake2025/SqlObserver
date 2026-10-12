using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlDatabaseFileIoTrendPort(NpgsqlDataSource dataSource)
    : IDatabaseFileIoTrendRepositoryPort
{
    private const string ReadSql = """
        SELECT bucket_start_utc,source_rows,comparable_rows,baseline_rows,
               reset_rows,gap_rows,legacy_rows,read_ops,read_stall_ms,
               write_ops,write_stall_ms
        FROM reporting.get_database_file_io_trend(
            @target,@database,@file,@from_utc,@to_utc);
        """;

    public async ValueTask<DatabaseFileIoTrendResult> ReadAsync(
        DatabaseFileIoTrendRequest request, CancellationToken cancellationToken)
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
                "SELECT set_config('sqlobserver.target_scope',@target_scope,true);",
                connection, transaction))
            {
                scope.Parameters.AddWithValue("target_scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand(ReadSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("database", request.DatabaseId);
            command.Parameters.AddWithValue("file", request.FileId);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            var points = new List<DatabaseFileIoTrendPoint>(169);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (points.Count == 169)
                        throw new InvalidDataException("File I/O trend exceeded its hourly bound.");
                    points.Add(new DatabaseFileIoTrendPoint(
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0),
                        reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                        reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                        Metric(reader, 7), Metric(reader, 8), Metric(reader, 9), Metric(reader, 10)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new DatabaseFileIoTrendResult(request.TargetId, request.DatabaseId,
                request.FileId, request.FromUtc, request.ToUtc, points);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("The file I/O trend target scope was rejected.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == "22023")
        {
            throw new ArgumentException("The file I/O trend bounds are invalid.", nameof(request), exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("File I/O trend read", exception);
        }
    }

    private static string? Metric(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null :
            reader.GetDecimal(ordinal).ToString("0", CultureInfo.InvariantCulture);
}
