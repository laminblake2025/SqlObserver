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
    public async ValueTask<IReadOnlyList<SqlMetricRollupResult>> ReadSqlBaselineInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        if (job.SourceCatalogVersion != MetricCatalogV2.Version || job.JobKind != "baseline")
            throw new ArgumentException("A catalog-v2 SQL baseline job is required.", nameof(job));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, """
            SELECT bucket_start,metric_key,sample_count,expected_count,value,minimum_value,maximum_value,sample_value_sum,
                last_value,gap_count,visibility_state,source_cutoff_utc,generation,weight_sum,weighted_value_sum,
                reset_count,truncated,counter_delta,rate_per_second
            FROM reporting.read_sql_baseline_inputs(@target,@revision,@metric,@from,@to,@cutoff,@generation);
            """, FiveSecondTimeout);
        command.Parameters.AddWithValue("target", job.TargetId.Value); command.Parameters.AddWithValue("revision", job.TargetRevision.Value);
        command.Parameters.AddWithValue("metric", job.MetricKey!); command.Parameters.AddWithValue("from", job.FromUtc);
        command.Parameters.AddWithValue("to", job.ToUtc); command.Parameters.AddWithValue("cutoff", job.SourceCutoffUtc);
        command.Parameters.AddWithValue("generation", job.Generation);
        var rows = new List<SqlMetricRollupResult>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset start = ReadUtc(reader, 0);
            var statistics = new RollupResult
            {
                Interval = RollupInterval.Hour, BucketStartUtc = start, BucketEndUtc = start.AddHours(1), MetricKey = reader.GetString(1),
                DimensionsSha256 = CanonicalDimensions.Sha256(null), Count = reader.GetInt32(2), Expected = reader.GetInt32(3),
                Mean = Number(reader, 4), Min = Number(reader, 5), Max = Number(reader, 6), Sum = Number(reader, 7), Last = Number(reader, 8),
                GapCount = reader.GetInt32(9), VisibilityState = reader.GetString(10), SourceCutoffUtc = ReadUtc(reader, 11), Generation = reader.GetInt64(12),
                ResetCount = reader.GetInt32(15), Truncated = reader.GetBoolean(16), CounterDelta = Number(reader, 17), RatePerSecond = Number(reader, 18),
            };
            var result = new SqlMetricRollupResult(statistics, reader.GetDouble(13), Number(reader, 14)); result.Validate(); rows.Add(result);
        }
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.AsReadOnly();
    }

    public async ValueTask StoreSqlBaselinesAsync(AnalyticsJobRequest request, IReadOnlyList<SqlMetricBaselineResult> baselines, CancellationToken cancellationToken)
    {
        AnalyticsJobRequest.Validate(request); ArgumentNullException.ThrowIfNull(baselines);
        if (request.TargetId is null || request.TargetRevision is null || baselines.Count != 168)
            throw new ArgumentException("A SQL baseline requires a scoped, complete hour-of-week result.", nameof(baselines));
        var payload = new JsonArray();
        foreach (SqlMetricBaselineResult baseline in baselines.OrderBy(row => row.Statistics.HourOfWeek))
        {
            baseline.Validate();
            JsonObject row = JsonSerializer.SerializeToNode(ToPersistence(baseline.Statistics), CamelCaseJson)!.AsObject();
            row["catalogVersion"] = baseline.CatalogVersion; row["catalogChecksum"] = baseline.CatalogChecksum;
            row["algorithmVersion"] = baseline.AlgorithmVersion; row["sourceAlgorithmVersion"] = baseline.SourceAlgorithmVersion;
            row["sourceCutoffUtc"] = JsonSerializer.SerializeToNode(baseline.SourceCutoffUtc, CamelCaseJson); payload.Add(row);
        }
        string json = payload.ToJsonString(CamelCaseJson);
        AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create("baseline", request, json, json);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, transaction, """
            SELECT analytics.commit_sql_metric_baselines(@operation,@job,@target,@revision,@work,@owner,@fence,@request,@rows,@result);
            """, FiveSecondTimeout);
        command.Parameters.AddWithValue("operation", replay.OperationId); command.Parameters.AddWithValue("job", request.JobId);
        command.Parameters.AddWithValue("target", request.TargetId.Value); command.Parameters.AddWithValue("revision", request.TargetRevision.Value);
        command.Parameters.AddWithValue("work", request.WorkKey); command.Parameters.AddWithValue("owner", request.Lease.Owner.Value);
        command.Parameters.AddWithValue("fence", request.Lease.FencingToken.Value); command.Parameters.AddWithValue("request", replay.RequestDigest);
        command.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, json); command.Parameters.AddWithValue("result", replay.ResultDigest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static double? Number(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
