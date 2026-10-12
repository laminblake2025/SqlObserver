using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed partial class PostgreSqlAnalyticsRepositoryPort
{
    public async ValueTask<IReadOnlyList<SqlMetricPoint>> ReadSqlRollupInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        if (job.SourceCatalogVersion != MetricCatalogV2.Version) throw new ArgumentException("A catalog-v2 SQL job is required.", nameof(job));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, """
            SELECT source_run_id,observed_at,metric_key,metric_value,aggregation_weight,complete
            FROM reporting.read_sql_metric_analytics_inputs(@target,@revision,@from,@to,@cutoff,@metric,@limit,@bytes);
            """, FiveSecondTimeout);
        command.Parameters.AddWithValue("target", job.TargetId.Value); command.Parameters.AddWithValue("revision", job.TargetRevision.Value);
        command.Parameters.AddWithValue("from", job.FromUtc); command.Parameters.AddWithValue("to", job.ToUtc);
        command.Parameters.AddWithValue("cutoff", job.SourceCutoffUtc); command.Parameters.AddWithValue("metric", job.MetricKey!);
        command.Parameters.AddWithValue("limit", AnalyticsJobBounds.MaximumRows); command.Parameters.AddWithValue("bytes", AnalyticsJobBounds.MaximumBytes);
        var points = new List<SqlMetricPoint>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            points.Add(new SqlMetricPoint(reader.GetGuid(0), ReadUtc(reader, 1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetBoolean(5)));
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return points.AsReadOnly();
    }

    public async ValueTask StoreSqlRollupAsync(AnalyticsJobRequest request, SqlMetricRollupResult rollup, CancellationToken cancellationToken)
    {
        AnalyticsJobRequest.Validate(request); ArgumentNullException.ThrowIfNull(rollup); rollup.Validate();
        if (request.TargetId is null || request.TargetRevision is null) throw new ArgumentException("A SQL rollup requires an exact target revision.", nameof(request));
        JsonObject payload = JsonSerializer.SerializeToNode(ToPersistence(rollup.Statistics), CamelCaseJson)!.AsObject();
        payload["catalogVersion"] = rollup.CatalogVersion; payload["catalogChecksum"] = rollup.CatalogChecksum;
        payload["algorithmVersion"] = rollup.AlgorithmVersion; payload["weightSum"] = rollup.WeightSum;
        payload["weightedValueSum"] = rollup.WeightedValueSum; payload["minimumValue"] = rollup.Statistics.Min;
        payload["maximumValue"] = rollup.Statistics.Max; payload["sampleValueSum"] = rollup.Statistics.Sum;
        string json = payload.ToJsonString(CamelCaseJson);
        AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create("rollup", request, json, json);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, """
            SELECT analytics.commit_sql_metric_rollup(@operation,@job,@target,@revision,@work,@owner,@fence,@request,@row,@result);
            """, FiveSecondTimeout);
        command.Parameters.AddWithValue("operation", replay.OperationId); command.Parameters.AddWithValue("job", request.JobId);
        command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("revision", request.TargetRevision.Value);
        command.Parameters.AddWithValue("work", request.WorkKey); command.Parameters.AddWithValue("owner", request.Lease.Owner.Value);
        command.Parameters.AddWithValue("fence", request.Lease.FencingToken.Value); command.Parameters.AddWithValue("request", replay.RequestDigest);
        command.Parameters.AddWithValue("row", NpgsqlDbType.Jsonb, json); command.Parameters.AddWithValue("result", replay.ResultDigest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
