using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryStorePlanRegressionPort(NpgsqlDataSource dataSource)
    : IQueryStorePlanRegressionRepositoryPort
{
    private const string ReadSql = """
        SELECT database_id,query_fingerprint,previous_plan_fingerprint,
               current_plan_fingerprint,previous_average_ms,current_average_ms,
               slowdown_ratio,previous_executions,current_executions,
               previous_comparable_rows,previous_source_rows,
               current_comparable_rows,current_source_rows,last_observed_at
        FROM reporting.list_query_store_plan_regressions(
            @target,@from_utc,@to_utc,@max_results);
        """;

    public async ValueTask<QueryStorePlanRegressionResult> ReadAsync(
        QueryStorePlanRegressionRequest request, CancellationToken cancellationToken)
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
            await using var command = new NpgsqlCommand(ReadSql, connection, transaction)
            {
                CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
            };
            command.Parameters.AddWithValue("target", request.TargetId.Value);
            command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc);
            command.Parameters.AddWithValue("max_results", request.Limit);
            var items = new List<QueryStorePlanRegression>(request.Limit);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (items.Count == request.Limit)
                        throw new InvalidDataException("Plan regression result exceeded its page bound.");
                    items.Add(new QueryStorePlanRegression(
                        new QueryOpaqueIdentity(reader.GetInt32(0), reader.GetString(1)),
                        reader.GetString(2), reader.GetString(3),
                        Number(reader, 4), Number(reader, 5), Number(reader, 6),
                        Number(reader, 7), Number(reader, 8),
                        reader.GetInt64(9), reader.GetInt64(10),
                        reader.GetInt64(11), reader.GetInt64(12),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 13)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new QueryStorePlanRegressionResult(request.TargetId, request.FromUtc,
                request.ToUtc, items);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("Plan regression target scope was rejected.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("plan regressions", exception);
        }
    }

    private static string Number(NpgsqlDataReader reader, int ordinal) =>
        reader.GetDecimal(ordinal).ToString("0.##########################", CultureInfo.InvariantCulture);
}
