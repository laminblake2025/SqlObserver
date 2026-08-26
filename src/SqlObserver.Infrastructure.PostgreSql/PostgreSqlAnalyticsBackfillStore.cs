using Npgsql;
using System.Text.Json;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Repository-only analytics backfill adapter.  All operations are fixed
/// migration functions; monitored SQL targets are never contacted.
/// </summary>
public sealed class PostgreSqlAnalyticsBackfillStore(NpgsqlDataSource dataSource) : IAnalyticsBackfillStore
{
    private const int CommandTimeoutSeconds = 5;

    public async ValueTask<IReadOnlyList<AnalyticsBackfillJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (maximumJobs is < 1 or > AnalyticsJobBounds.MaximumConcurrency) throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT job_id,instance_id,target_revision,from_utc,to_utc,metric_key,cursor,current_day_utc,cursor_source_kind,cursor_observed_at,cursor_source_id,cursor_metric_key,cursor_dimension_hash,cursor_ordinal,cursor_target_id,cursor_target_revision,cursor_day_utc,cursor_catalog_version FROM control.claim_m10_analytics_jobs(@work_key,@owner_execution_id,@fencing_token,@limit);", connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.AddWithValue("work_key", lease.Key.Value);
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        command.Parameters.AddWithValue("limit", maximumJobs);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<AnalyticsBackfillJob>(maximumJobs);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)) throw new InvalidDataException("Backfill claim returned incomplete metadata.");
            var job = new AnalyticsBackfillJob(reader.GetGuid(0), new(reader.GetGuid(1)), new(reader.GetInt64(2)), Utc(reader, 3), Utc(reader, 4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6))
            {
                CurrentDayUtc = reader.IsDBNull(7) ? null : Utc(reader, 7), CursorSourceKind = reader.IsDBNull(8) ? null : reader.GetString(8), CursorObservedAtUtc = reader.IsDBNull(9) ? null : Utc(reader, 9), CursorSourceId = reader.IsDBNull(10) ? null : reader.GetGuid(10), CursorMetricKey = reader.IsDBNull(11) ? null : reader.GetString(11), CursorDimensionHash = reader.IsDBNull(12) ? null : Convert.ToHexString(reader.GetFieldValue<byte[]>(12)).ToLowerInvariant(), CursorOrdinal = reader.IsDBNull(13) ? null : reader.GetInt32(13), CursorTargetId = reader.IsDBNull(14) ? null : reader.GetGuid(14), CursorTargetRevision = reader.IsDBNull(15) ? null : reader.GetInt64(15), CursorDayUtc = reader.IsDBNull(16) ? null : Utc(reader, 16), CursorCatalogVersion = reader.IsDBNull(17) ? null : reader.GetInt32(17)
            };
            job.Validate(); jobs.Add(job);
        }
        return jobs;
    }

    public async ValueTask<AnalyticsBackfillPage> ReadPageAsync(AnalyticsBackfillJob job, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, int maximumRows, int maximumBytes, CancellationToken cancellationToken)
    {
        job.Validate();
        if (dayStartUtc.Offset != TimeSpan.Zero || dayEndUtc.Offset != TimeSpan.Zero || dayEndUtc <= dayStartUtc || maximumRows is < 1 or > AnalyticsJobBounds.MaximumRows || maximumBytes is < 1 or > AnalyticsJobBounds.MaximumBytes) throw new ArgumentException("Backfill page bounds rejected.");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, new RepositoryCallTimeout(TimeSpan.FromSeconds(CommandTimeoutSeconds)), cancellationToken).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
            {
                scope.Parameters.AddWithValue("scope", job.TargetId.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("SELECT observed_at,metric_key,metric_value,dimensions,next_cursor,has_more,safe_cursor FROM reporting.read_m10_backfill_page(@instance_id,@target_revision,@from_utc,@to_utc,@metric_key,@cursor,@max_rows,@max_bytes,@job_id,@source_catalog_version);", connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.AddWithValue("instance_id", job.TargetId.Value);
            command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value);
            command.Parameters.AddWithValue("from_utc", dayStartUtc);
            command.Parameters.AddWithValue("to_utc", dayEndUtc);
            command.Parameters.AddWithValue("metric_key", (object?)job.MetricKey ?? DBNull.Value);
            command.Parameters.AddWithValue("cursor", (object?)job.Cursor ?? DBNull.Value);
            command.Parameters.AddWithValue("max_rows", maximumRows);
            command.Parameters.AddWithValue("max_bytes", maximumBytes);
            command.Parameters.AddWithValue("job_id", job.JobId);
            command.Parameters.AddWithValue("source_catalog_version", 1);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var points = new List<MetricPoint>();
            string? nextCursor = null;
            string? safeCursor = null;
            bool more = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyDictionary<string, string>? dimensions = reader.IsDBNull(3) ? null : ParseDimensions(reader.GetFieldValue<string>(3));
                points.Add(new MetricPoint(Utc(reader, 0), reader.GetString(1), reader.GetDouble(2), dimensions: dimensions));
                if (!reader.IsDBNull(4)) nextCursor = reader.GetString(4);
                more = !reader.IsDBNull(5) && reader.GetBoolean(5);
                if (!reader.IsDBNull(6)) safeCursor = reader.GetString(6);
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AnalyticsBackfillPage(points, nextCursor, more, safeCursor);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    public async ValueTask EnsureHistoricalPartitionAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, DateTimeOffset dayStartUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease); job.Validate();
        if (dayStartUtc.Offset != TimeSpan.Zero || dayStartUtc.TimeOfDay != TimeSpan.Zero) throw new ArgumentException("Historical partition requests require a UTC day boundary.", nameof(dayStartUtc));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, new RepositoryCallTimeout(TimeSpan.FromSeconds(CommandTimeoutSeconds)), cancellationToken).ConfigureAwait(false);
            await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction) { CommandTimeout = CommandTimeoutSeconds })
            {
                scope.Parameters.AddWithValue("scope", job.TargetId.Value.ToString("D"));
                await scope.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var command = new NpgsqlCommand("SELECT control.ensure_m10_backfill_partition(@instance_id,@target_revision,@day_start_utc,@owner_execution_id,@fencing_token);", connection, transaction) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.AddWithValue("instance_id", job.TargetId.Value); command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value); command.Parameters.AddWithValue("day_start_utc", dayStartUtc);
            command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value); command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
            if (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidOperationException("Historical backfill partition was not accepted.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    public async ValueTask SaveCursorAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, DateTimeOffset dayStartUtc, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease); job.Validate();
        if (dayStartUtc.Offset != TimeSpan.Zero || cursor is not null && cursor.Length > 4096) throw new ArgumentException("Backfill cursor bounds rejected.");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT control.advance_m10_backfill_cursor(@job_id,@day_start_utc,@cursor,@owner_execution_id,@fencing_token);", connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.AddWithValue("job_id", job.JobId);
        command.Parameters.AddWithValue("day_start_utc", dayStartUtc);
        command.Parameters.AddWithValue("cursor", (object?)cursor ?? DBNull.Value);
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(AnalyticsBackfillJob job, WorkerLeaseIdentity lease, AnalyticsBackfillCompletion completion, string? failureDetail, CancellationToken cancellationToken)
    {
        if (failureDetail is not null && failureDetail.Length > 1024) failureDetail = "bounded_backfill_failure";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(lease); job.Validate();
        await using var command = new NpgsqlCommand("SELECT control.complete_m10_analytics_job(@job_id,@status,@error,@owner_execution_id,@fencing_token);", connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.AddWithValue("job_id", job.JobId);
        command.Parameters.AddWithValue("status", completion switch { AnalyticsBackfillCompletion.Succeeded => "succeeded", AnalyticsBackfillCompletion.Partial => "partial", AnalyticsBackfillCompletion.Cancelled => "cancelled", _ => "failed" });
        command.Parameters.AddWithValue("error", (object?)failureDetail ?? DBNull.Value);
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecordReplayAsync(Guid operationId, AnalyticsBackfillJob job, WorkerLeaseIdentity lease, ReadOnlyMemory<byte> requestDigest, ReadOnlyMemory<byte> resultDigest, string resultJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease); job.Validate();
        if (operationId == Guid.Empty || requestDigest.Length != 32 || resultDigest.Length != 32 || System.Text.Encoding.UTF8.GetByteCount(resultJson) > AnalyticsJobBounds.MaximumBytes) throw new ArgumentException("Backfill replay bounds rejected.");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT control.replay_m10_analytics_job(@operation_id,@job_id,@instance_id,@target_revision,@owner_execution_id,@fencing_token,@request_digest,@result_digest,@result);", connection) { CommandTimeout = CommandTimeoutSeconds };
        command.Parameters.AddWithValue("operation_id", operationId);
        command.Parameters.AddWithValue("job_id", job.JobId);
        command.Parameters.AddWithValue("instance_id", job.TargetId.Value);
        command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value);
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        command.Parameters.AddWithValue("request_digest", requestDigest.ToArray());
        command.Parameters.AddWithValue("result_digest", resultDigest.ToArray());
        command.Parameters.Add("result", NpgsqlTypes.NpgsqlDbType.Jsonb).Value = resultJson;
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset Utc(NpgsqlDataReader reader, int ordinal) => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    private static Dictionary<string, string> ParseDimensions(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Backfill dimensions are not an object.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Backfill dimensions are not strings.");
            result.Add(property.Name, property.Value.GetString()!);
        }
        return result;
    }
}
