using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlPlanCacheIntervalRankingPort(NpgsqlDataSource dataSource)
    : IPlanCacheIntervalRankingRepositoryPort
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async ValueTask<PlanCacheIntervalRankingResult> ReadAsync(PlanCacheIntervalRankingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, request.Timeout, timeout.Token).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", request.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("SELECT reporting.list_plan_cache_interval_queries(@target,@from_utc,@to_utc,@metric,@limit);", connection, transaction)
            { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout) };
            command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("from_utc", request.FromUtc);
            command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("metric", request.MetricName);
            command.Parameters.AddWithValue("limit", request.Limit);
            if (await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false) is not string json || json.Length > 262144)
                throw new InvalidDataException("Cache ranking response exceeded its byte bound or was missing.");
            var result = JsonSerializer.Deserialize<PlanCacheIntervalRankingResult>(json, JsonOptions)
                ?? throw new InvalidDataException("Cache ranking response was missing.");
            await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        { throw new UnauthorizedAccessException("Cache ranking target scope was rejected.", exception); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw PostgreSqlRuntimeSupport.CreateTimeoutException("cache interval ranking", exception); }
    }
}
