using System.Globalization;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlQueryStoreGroupPlanTrendPort(NpgsqlDataSource dataSource)
    : IQueryStoreGroupPlanTrendRepositoryPort
{
    private const string ReadSql = """
        SELECT bucket_start_utc,source_rows,runtime_comparable_rows,
               runtime_baseline_rows,runtime_reset_rows,runtime_ambiguous_rows,
               cpu_ms,duration_ms,execution_count,logical_reads,writes,
               rows_processed,wait_comparable_rows,wait_baseline_rows,
               wait_reset_rows,wait_unavailable_rows,wait_stale_rows,
               wait_categories::text
        FROM control.get_query_store_group_plan_trend(
            @target,@database,@query,@plan,@from_utc,@to_utc);
        """;

    public async ValueTask<QueryStoreGroupPlanTrendResult> ReadAsync(
        QueryStoreGroupPlanTrendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout =
            PostgreSqlRuntimeSupport.CreateTimeoutScope(request.Timeout, cancellationToken);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(timeout.Token)
            .ConfigureAwait(false);
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@target_scope,false);", connection))
        {
            scope.Parameters.AddWithValue("target_scope", request.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync(timeout.Token).ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand(ReadSql, connection)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
        };
        command.Parameters.AddWithValue("target", request.TargetId.Value);
        command.Parameters.AddWithValue("database", request.Plan.Query.DatabaseId);
        command.Parameters.AddWithValue("query",
            Convert.FromHexString(request.Plan.Query.QueryFingerprint));
        command.Parameters.AddWithValue("plan",
            Convert.FromHexString(request.Plan.PlanFingerprint));
        command.Parameters.AddWithValue("from_utc", request.FromUtc);
        command.Parameters.AddWithValue("to_utc", request.ToUtc);
        var points = new List<QueryStoreGroupPlanTrendPoint>(169);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(timeout.Token)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
        {
            if (points.Count == 169)
                throw new InvalidDataException("Query Store group trend exceeded its hourly bound.");
            points.Add(new QueryStoreGroupPlanTrendPoint(
                PostgreSqlRuntimeSupport.ReadUtcTimestamp(reader, 0),
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5),
                Metric(reader, 6), Metric(reader, 7), Metric(reader, 8),
                Metric(reader, 9), Metric(reader, 10), Metric(reader, 11),
                reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14),
                reader.GetInt64(15), reader.GetInt64(16),
                ReadCategories(reader.GetString(17))));
        }
        return new QueryStoreGroupPlanTrendResult(request.TargetId, request.Plan,
            request.FromUtc, request.ToUtc, points);
    }

    private static string? Metric(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null :
            reader.GetDecimal(ordinal).ToString("0", CultureInfo.InvariantCulture);

    private static Dictionary<int, string> ReadCategories(string json)
    {
        if (json.Length > 4096)
            throw new InvalidDataException("Query Store group wait categories exceed their byte bound.");
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Count() > QueryStoreWaitSnapshot.MaximumCategories)
            throw new InvalidDataException("Query Store group wait categories are invalid.");
        var categories = new Dictionary<int, string>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int category) ||
                category is < 0 or > 31 ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !decimal.TryParse(property.Value.GetRawText(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out decimal milliseconds) ||
                milliseconds < 0 || decimal.Truncate(milliseconds) != milliseconds ||
                !categories.TryAdd(category,
                    milliseconds.ToString("0", CultureInfo.InvariantCulture)))
                throw new InvalidDataException("Query Store group wait category is invalid.");
        }
        return categories;
    }
}
