using System.Globalization;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryStoreWaitRankingPort(NpgsqlDataSource dataSource)
    : IQueryStoreWaitRankingRepositoryPort
{
    private const string ReadSql = """
        SELECT database_id,query_fingerprint,plan_fingerprint,
               wait_milliseconds,comparable_rows,source_rows,last_observed_at
        FROM reporting.list_top_query_store_waits_for_category(
            @target,@from_utc,@to_utc,@max_results,@category);
        """;

    public async ValueTask<QueryStoreWaitRankingResult> ReadAsync(
        QueryStoreWaitRankingRequest request, CancellationToken cancellationToken)
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
            command.Parameters.Add("category", NpgsqlTypes.NpgsqlDbType.Integer).Value =
                (object?)request.Category ?? DBNull.Value;
            var items = new List<QueryStoreWaitRankedPlan>(request.Limit);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                {
                    if (items.Count == request.Limit)
                        throw new InvalidDataException("Query wait ranking exceeded its page bound.");
                    var plan = new PlanOpaqueIdentity(
                        new QueryOpaqueIdentity(reader.GetInt32(0), reader.GetString(1)),
                        reader.GetString(2));
                    items.Add(new QueryStoreWaitRankedPlan(plan,
                        reader.GetDecimal(3).ToString("0", CultureInfo.InvariantCulture),
                        reader.GetInt64(4), reader.GetInt64(5),
                        PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 6)));
                }
            }
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return new QueryStoreWaitRankingResult(request.TargetId, request.FromUtc,
                request.ToUtc, items, request.Category);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            throw new UnauthorizedAccessException("Query wait target scope was rejected.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("query wait ranking", exception);
        }
    }
}
