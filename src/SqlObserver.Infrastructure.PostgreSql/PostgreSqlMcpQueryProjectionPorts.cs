using System.Globalization;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Fixed, target-scoped PostgreSQL projections used by read-only MCP
/// application services. No table, SQL text, payload, or provider content is
/// exposed through these ports.
/// </summary>
public sealed partial class PostgreSqlAnalyticsRepositoryPort
{
    public async ValueTask<MetricSeriesPage> ReadMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, query.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long revision = await ResolveTargetRevisionAsync(connection, transaction, query.TargetId.Value, query.Cursor?.TargetRevision ?? query.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        DateTimeOffset snapshot = query.Cursor?.SnapshotUtc ?? query.SnapshotUtc ?? await ReadRepositoryClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (query.Cursor is { } cursor && cursor.RunId == Guid.Empty) throw new InvalidDataException("Metric-series continuation is missing its tie key.");
        const string sql = "SELECT observed_at,run_id,metric_key,metric_value,dimensions FROM reporting.list_metric_series(@instance_id,@target_revision,@from_utc,@to_utc,@metric_key,@limit,@snapshot_utc,@cursor_at,@cursor_run_id,@cursor_metric_key,@cursor_dimensions);";
        await using var command = Command(connection, transaction, sql, FiveSecondTimeout);
        string? cursorDimensions = query.Cursor is null ? null : CanonicalDimensions.Json(ParseMcpDimensions(query.Cursor.DimensionsKey));
        command.Parameters.AddWithValue("instance_id", query.TargetId.Value); command.Parameters.AddWithValue("target_revision", revision); command.Parameters.AddWithValue("from_utc", query.FromUtc); command.Parameters.AddWithValue("to_utc", query.ToUtc); command.Parameters.AddWithValue("metric_key", query.MetricKey); command.Parameters.AddWithValue("limit", query.Limit + 1); command.Parameters.AddWithValue("snapshot_utc", snapshot);
        command.Parameters.AddWithValue("cursor_at", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)query.Cursor?.ObservedAtUtc ?? DBNull.Value); command.Parameters.AddWithValue("cursor_run_id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)query.Cursor?.RunId ?? DBNull.Value); command.Parameters.AddWithValue("cursor_metric_key", NpgsqlTypes.NpgsqlDbType.Text, (object?)query.Cursor?.MetricKey ?? DBNull.Value); command.Parameters.AddWithValue("cursor_dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb, (object?)cursorDimensions ?? DBNull.Value);
        var rows = new List<(MetricSeriesItem Item, Guid RunId, string DimensionsKey)>(query.Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset observed = ReadUtc(reader, 0);
            Guid runId = reader.GetGuid(1);
            string metric = reader.GetString(2);
            string canonicalDimensions = CanonicalDimensions.Json(ParseMcpDimensions(reader.GetString(4)));
            // PostgreSQL owns the complete ORDER BY because jsonb btree order
            // is not the same as ordinal .NET JSON text order (for example,
            // '{}' sorts before '{"volume":"C"}' in PostgreSQL). Validate
            // only the non-json prefix here; an equal prefix is valid when a
            // later dimensions value is selected by the database tuple.
            if (query.Cursor is not null && (observed, runId, metric).CompareTo((query.Cursor.ObservedAtUtc, query.Cursor.RunId, query.Cursor.MetricKey)) < 0) throw new InvalidDataException("Metric-series cursor was not honored.");
            if (!string.Equals(metric, query.MetricKey, StringComparison.Ordinal)) throw new InvalidDataException("Metric-series projection returned a different metric.");
            rows.Add((new MetricSeriesItem(observed, reader.GetDouble(3), ParseMcpDimensions(canonicalDimensions)), runId, canonicalDimensions));
        }
        bool hasMore = rows.Count > query.Limit;
        if (hasMore) rows.RemoveRange(query.Limit, rows.Count - query.Limit);
        var items = rows.Select(static row => row.Item).ToList();
        MetricSeriesCursor? next = hasMore && rows.Count > 0 ? new MetricSeriesCursor(query.TargetId, query.MetricKey, rows[^1].Item.ObservedAtUtc, rows[^1].RunId, rows[^1].DimensionsKey, snapshot, new ObservationTargetRevision(revision)) : null;
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MetricSeriesPage(query.TargetId, query.MetricKey, query.FromUtc, query.ToUtc, items, items.Count == 0 ? "no_data" : "complete", new ObservationTargetRevision(revision), snapshot, next) { HasMore = hasMore };
    }

    public async ValueTask<StorageForecastPage> ReadStorageForecastAsync(StorageForecastQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, query.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long revision = await ResolveTargetRevisionAsync(connection, transaction, query.TargetId.Value, query.Cursor?.TargetRevision ?? query.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        DateTimeOffset snapshot = query.Cursor?.SnapshotUtc ?? query.SnapshotUtc ?? await ReadRepositoryClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        string dimensions = CanonicalDimensions.Json(query.Dimensions);
        const string sql = "SELECT forecast_id,metric_key,dimension_hash,dimensions,horizon_start,horizon_end,predicted_value,lower_bound,upper_bound,confidence,source_generation,residual,slope_per_day,visibility_state,model FROM reporting.get_m10_forecast_scoped(@instance_id,@target_revision,@metric_key,@dimensions,@horizon,@snapshot_utc,@limit,@cursor_horizon_start,@cursor_forecast_id);";
        await using var command = Command(connection, transaction, sql, FiveSecondTimeout);
        command.Parameters.AddWithValue("instance_id", query.TargetId.Value); command.Parameters.AddWithValue("target_revision", revision); command.Parameters.AddWithValue("metric_key", query.MetricKey); command.Parameters.AddWithValue("dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb, dimensions); command.Parameters.AddWithValue("horizon", query.Horizon); command.Parameters.AddWithValue("snapshot_utc", snapshot); command.Parameters.AddWithValue("limit", query.Limit + 1); command.Parameters.AddWithValue("cursor_horizon_start", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)query.Cursor?.HorizonStartUtc ?? DBNull.Value); command.Parameters.AddWithValue("cursor_forecast_id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)query.Cursor?.ForecastId ?? DBNull.Value);
        var items = new List<StorageForecastItem>(query.Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] hash = reader.GetFieldValue<byte[]>(2);
            if (hash.Length != 32) throw new InvalidDataException("Forecast dimension digest is invalid.");
            items.Add(new StorageForecastItem(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1), ReadUtc(reader, 4), ReadUtc(reader, 5), reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetDouble(7), reader.IsDBNull(8) ? null : reader.GetDouble(8), reader.IsDBNull(12) ? null : reader.GetDouble(12), reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture), reader.IsDBNull(11) ? 0 : reader.GetDouble(11), reader.GetString(15), reader.GetInt64(10), reader.GetString(13), ParseMcpDimensions(reader.GetString(3)), Convert.ToHexString(hash).ToLowerInvariant()));
        }
        bool hasMore = items.Count > query.Limit;
        if (hasMore) items.RemoveRange(query.Limit, items.Count - query.Limit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        StorageForecastCursor? next = hasMore && items.Count > 0 && items[^1].ForecastId is { } forecastId
            ? new StorageForecastCursor(query.TargetId, new ObservationTargetRevision(revision), query.MetricKey, CanonicalDimensions.Sha256(query.Dimensions), query.Horizon, snapshot, items[^1].HorizonStartUtc, forecastId)
            : null;
        if (hasMore && next is null) throw new InvalidDataException("Forecast continuation is missing its tie identifier.");
        return new StorageForecastPage(query.TargetId, query.MetricKey, query.Horizon, items, items.Count == 0 ? "no_data" : items.Any(x => x.VisibilityState == "unsupported") ? "unsupported" : items.Any(x => x.VisibilityState != "complete" || x.Confidence < .5) ? "partial" : "complete", new ObservationTargetRevision(revision), snapshot, next) { HasMore = hasMore };
    }

    public async ValueTask<DiagnosticEventSearchPage> SearchDiagnosticEventsAsync(DiagnosticEventSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, query.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long revision = await ResolveTargetRevisionAsync(connection, transaction, query.TargetId.Value, query.Cursor?.TargetRevision ?? query.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        DateTimeOffset snapshot = query.Cursor?.SnapshotUtc ?? query.SnapshotUtc ?? await ReadRepositoryClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT occurred_at,event_id,event_kind,severity,safe_metadata,collected_at,target_revision FROM reporting.search_m10_diagnostics(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_event_id,@snapshot_utc);";
        await using var command = Command(connection, transaction, sql, FiveSecondTimeout);
        command.Parameters.AddWithValue("instance_id", query.TargetId.Value); command.Parameters.AddWithValue("target_revision", revision); command.Parameters.AddWithValue("from_utc", query.FromUtc); command.Parameters.AddWithValue("to_utc", query.ToUtc); command.Parameters.AddWithValue("limit", query.Limit + 1); command.Parameters.AddWithValue("cursor_at", (object?)query.Cursor?.OccurredAtUtc ?? DBNull.Value); command.Parameters.AddWithValue("cursor_event_id", (object?)query.Cursor?.EventId ?? DBNull.Value); command.Parameters.AddWithValue("snapshot_utc", snapshot);
        var items = new List<DiagnosticEventItem>(query.Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(reader.GetString(4));
            items.Add(new DiagnosticEventItem(ReadUtc(reader, 0), reader.GetGuid(1), reader.GetString(2), reader.GetInt16(3), ParseSafeMetadata(document.RootElement), ReadUtc(reader, 5), new ObservationTargetRevision(reader.GetInt64(6))));
        }
        bool more = items.Count > query.Limit;
        if (more) items.RemoveRange(query.Limit, items.Count - query.Limit);
        DiagnosticEventCursor? next = more && items.Count > 0 ? new DiagnosticEventCursor(query.TargetId, query.FromUtc, query.ToUtc, items[^1].OccurredAtUtc, items[^1].EventId, snapshot, new ObservationTargetRevision(revision)) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DiagnosticEventSearchPage(query.TargetId, query.FromUtc, query.ToUtc, items, more, next, new ObservationTargetRevision(revision), snapshot);
    }

    public async ValueTask<IncidentEvidencePage?> GetIncidentEvidenceAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, query.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long revision = await ResolveTargetRevisionAsync(connection, transaction, query.TargetId.Value, query.Cursor?.TargetRevision ?? query.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        DateTimeOffset snapshot = query.Cursor?.SnapshotUtc ?? query.SnapshotUtc ?? await ReadRepositoryClockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        const string evidenceSql = "SELECT occurred_at,packet_id,evidence_kind,source_run_id,source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,confidence,visibility_state FROM reporting.get_m10_incident_evidence(@instance_id,@target_revision,@thread_id,@limit,@snapshot_utc,@cursor_occurred_at,@cursor_packet_id);";
        await using var evidenceCommand = Command(connection, transaction, evidenceSql, FiveSecondTimeout);
        evidenceCommand.Parameters.AddWithValue("instance_id", query.TargetId.Value); evidenceCommand.Parameters.AddWithValue("target_revision", revision); evidenceCommand.Parameters.AddWithValue("thread_id", query.ThreadId); evidenceCommand.Parameters.AddWithValue("limit", query.Limit + 1); evidenceCommand.Parameters.AddWithValue("snapshot_utc", snapshot); evidenceCommand.Parameters.Add("cursor_occurred_at", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = (object?)query.Cursor?.EvidenceOccurredAtUtc ?? DBNull.Value; evidenceCommand.Parameters.Add("cursor_packet_id", NpgsqlTypes.NpgsqlDbType.Uuid).Value = (object?)query.Cursor?.EvidencePacketId ?? DBNull.Value;
        var items = new List<IncidentEvidenceItem>(query.Limit + 1);
        await using (NpgsqlDataReader reader = await evidenceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new IncidentEvidenceItem(ReadUtc(reader, 0), reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), Digest(reader, 4), Digest(reader, 5), Digest(reader, 6), reader.IsDBNull(7) ? null : ReadUtc(reader, 7), Convert.ToDouble(reader.GetValue(8), CultureInfo.InvariantCulture), reader.GetString(9)));
            }
        const string generationSql = "SELECT thread_id,generation,observed_at,correlation_digest,supersedes_previous,evidence_packet_id FROM reporting.get_m10_incident_generations(@instance_id,@target_revision,@thread_id,@limit,@snapshot_utc,@cursor_generation);";
        await using var generationCommand = Command(connection, transaction, generationSql, FiveSecondTimeout);
        generationCommand.Parameters.AddWithValue("instance_id", query.TargetId.Value); generationCommand.Parameters.AddWithValue("target_revision", revision); generationCommand.Parameters.AddWithValue("thread_id", query.ThreadId); generationCommand.Parameters.AddWithValue("limit", query.Limit + 1); generationCommand.Parameters.AddWithValue("snapshot_utc", snapshot); generationCommand.Parameters.Add("cursor_generation", NpgsqlTypes.NpgsqlDbType.Bigint).Value = (object?)query.Cursor?.Generation ?? DBNull.Value;
        var generations = new List<IncidentGenerationItem>(query.Limit + 1);
        await using (NpgsqlDataReader reader = await generationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) generations.Add(new IncidentGenerationItem(reader.GetGuid(0), reader.GetInt64(1), ReadUtc(reader, 2), Digest(reader, 3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
        bool evidenceMore = items.Count > query.Limit;
        if (evidenceMore) items.RemoveRange(query.Limit, items.Count - query.Limit);
        bool generationsMore = generations.Count > query.Limit;
        if (generationsMore) generations.RemoveRange(query.Limit, generations.Count - query.Limit);
        bool hasMore = evidenceMore || generationsMore;
        IncidentEvidenceCursor? next = hasMore && (items.Count > 0 || generations.Count > 0)
            ? new IncidentEvidenceCursor(query.TargetId, new ObservationTargetRevision(revision), query.ThreadId, snapshot,
                items.Count > 0 ? items[^1].OccurredAtUtc : query.Cursor?.EvidenceOccurredAtUtc,
                items.Count > 0 ? items[^1].PacketId : query.Cursor?.EvidencePacketId,
                generations.Count > 0 ? generations[^1].Generation : query.Cursor?.Generation)
            : null;
        if (hasMore && next is null) throw new InvalidDataException("Incident evidence continuation is missing its tie key.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count == 0 && generations.Count == 0) return null;
        return new IncidentEvidencePage(query.TargetId, query.ThreadId, items, generations, new ObservationTargetRevision(revision), snapshot, next) { HasMore = hasMore };
    }

    private static string Digest(NpgsqlDataReader reader, int ordinal)
    { byte[] value = reader.GetFieldValue<byte[]>(ordinal); if (value.Length != 32) throw new InvalidDataException("Projection digest is invalid."); return Convert.ToHexString(value).ToLowerInvariant(); }
    private static IReadOnlyDictionary<string, string> ParseMcpDimensions(string json)
    { using JsonDocument document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Projection dimensions are not an object."); var result = new Dictionary<string, string>(StringComparer.Ordinal); foreach (JsonProperty p in document.RootElement.EnumerateObject()) { if (p.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Projection dimension is not a string."); result.Add(p.Name, p.Value.GetString()!); } return CanonicalDimensions.Normalize(result); }
    private static DiagnosticEventSafeMetadata ParseSafeMetadata(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Diagnostic metadata is not an object.");
        string? metric = null; int? participants = null, relations = null; bool? truncated = null;
        foreach (JsonProperty p in root.EnumerateObject())
            switch (p.Name)
            {
                case "metricKey": if (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { Length: > 0 and <= 128 } value) throw new InvalidDataException("Diagnostic metric metadata is invalid."); metric = value; break;
                case "participantCount": if (!p.Value.TryGetInt32(out int pc) || pc is < 0 or > 128) throw new InvalidDataException("Diagnostic participant metadata is invalid."); participants = pc; break;
                case "relationCount": if (!p.Value.TryGetInt32(out int rc) || rc is < 0 or > 256) throw new InvalidDataException("Diagnostic relation metadata is invalid."); relations = rc; break;
                case "parseTruncated": if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Diagnostic truncation metadata is invalid."); truncated = p.Value.GetBoolean(); break;
                default: throw new InvalidDataException("Diagnostic metadata contains an unknown key.");
            }
        return new DiagnosticEventSafeMetadata(metric, participants, relations, truncated);
    }

    private static async ValueTask<DateTimeOffset> ReadRepositoryClockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT clock_timestamp() AT TIME ZONE 'UTC';", FiveSecondTimeout);
        object value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Repository clock returned no value.");
        DateTime utc = value switch
        {
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeOffset offset => offset.UtcDateTime,
            _ => throw new InvalidDataException("Repository clock returned an invalid value.")
        };
        return new DateTimeOffset(utc);
    }
}
