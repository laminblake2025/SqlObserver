using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryPlanWaitReadRepositoryPort(NpgsqlDataSource dataSource)
    : IQueryPlanWaitReadRepositoryPort
{
    public async ValueTask<QueryPlanWaitSnapshot?> ReadAsync(
        QueryPlanReadRequest request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target,false);", connection))
        {
            scope.Parameters.AddWithValue("target", request.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(timeout.Token);
        }
        await using var command = new NpgsqlCommand("""
            SELECT categories::text,captured_at
            FROM control.get_query_plan_wait_snapshot(@target,@run,@database,@query,@plan);
            """, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        command.Parameters.AddWithValue("target", request.TargetId.Value);
        command.Parameters.AddWithValue("run", request.CollectionRunId);
        command.Parameters.AddWithValue("database", request.Plan.Query.DatabaseId);
        command.Parameters.AddWithValue("query", Convert.FromHexString(request.Plan.Query.QueryFingerprint));
        command.Parameters.AddWithValue("plan", Convert.FromHexString(request.Plan.PlanFingerprint));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token);
        if (!await reader.ReadAsync(timeout.Token)) return null;
        string json = reader.GetString(0);
        if (json.Length > 4096) throw new InvalidDataException("Query wait categories exceed their bound.");
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() > QueryStoreWaitSnapshot.MaximumCategories)
            throw new InvalidDataException("Query wait categories have an invalid shape.");
        var categories = new List<QueryWaitCategory>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("category", out JsonElement category) ||
                !item.TryGetProperty("waitMilliseconds", out JsonElement milliseconds) ||
                !category.TryGetInt32(out int number) ||
                !milliseconds.TryGetInt64(out long value))
                throw new InvalidDataException("Query wait category row is invalid.");
            categories.Add(new QueryWaitCategory(number, value));
        }
        var snapshot = new QueryStoreWaitSnapshot(categories);
        DateTimeOffset captured = PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 1);
        if (await reader.ReadAsync(timeout.Token))
            throw new InvalidDataException("Query wait lookup returned more than one snapshot.");
        return new QueryPlanWaitSnapshot(snapshot.Categories, captured);
    }
}
