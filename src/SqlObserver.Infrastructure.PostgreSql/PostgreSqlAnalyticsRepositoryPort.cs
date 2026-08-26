using System.Text;
using System.Text.Json;
using System.Globalization;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Target-scoped analytics persistence. Every command runs in a short transaction with local UTC/scope settings.</summary>
public sealed class PostgreSqlAnalyticsRepositoryPort : IAnalyticsRepositoryPort, IAnalyticsSurfaceRepositoryPort, IRetentionRepositoryPort, IRetentionPolicyRepositoryPort, IAnalyticsDerivationStore
{
    private static readonly RepositoryCallTimeout FiveSecondTimeout = new(TimeSpan.FromSeconds(5));
    private readonly NpgsqlDataSource dataSource;
    private readonly IdentityFingerprintKey fingerprintKey;
    public PostgreSqlAnalyticsRepositoryPort(NpgsqlDataSource dataSource, IdentityFingerprintKey fingerprintKey)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.fingerprintKey = fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey));
    }

    public async ValueTask<int> ScheduleAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken)
    {
        ValidateDerivationLease(lease, "Analytics derivation schedule contract is invalid.");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT control.schedule_m10_derivation_jobs(@owner_execution_id,@fencing_token)", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value);
        command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            throw new InvalidDataException("PostgreSQL returned no derivation schedule count.");
        int scheduled = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (scheduled is < 0 or > AnalyticsJobBounds.MaximumRows)
            throw new InvalidDataException("PostgreSQL returned an out-of-bounds derivation schedule count.");
        return scheduled;
    }

    public async ValueTask<IReadOnlyList<AnalyticsDerivationJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken)
    {
        ValidateDerivationLease(lease, "Analytics derivation claim contract is invalid.");
        if (maximumJobs is < 1 or > AnalyticsJobBounds.MaximumConcurrency)
            throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT job_id,instance_id,target_revision,job_kind,from_utc,to_utc,source_cutoff_utc,generation,metric_key,horizon_seconds,dimensions_hash,rollup_interval FROM control.claim_m10_derivation_jobs(@work_key,@owner_execution_id,@fencing_token,@limit)", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("work_key", lease.Key.Value); command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value); command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value); command.Parameters.AddWithValue("limit", maximumJobs);
        var jobs = new List<AnalyticsDerivationJob>(maximumJobs);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TimeSpan? horizon = reader.IsDBNull(9) ? null : TimeSpan.FromSeconds(reader.GetDouble(9));
            string? dimensionHash = reader.IsDBNull(10) ? null : Convert.ToHexString(reader.GetFieldValue<byte[]>(10)).ToLowerInvariant();
            RollupInterval? rollupInterval = reader.IsDBNull(11) ? null : reader.GetString(11) switch { "5m" => SqlObserver.Domain.Analytics.RollupInterval.FiveMinutes, "hour" => SqlObserver.Domain.Analytics.RollupInterval.Hour, "day" => SqlObserver.Domain.Analytics.RollupInterval.Day, _ => throw new InvalidDataException("PostgreSQL returned an unknown derivation rollup interval.") };
            var job = new AnalyticsDerivationJob(reader.GetGuid(0), new MonitoredInstanceId(reader.GetGuid(1)), new ObservationTargetRevision(reader.GetInt64(2)), reader.GetString(3), ReadUtc(reader, 4), ReadUtc(reader, 5), reader.IsDBNull(6) ? ReadUtc(reader, 5) : ReadUtc(reader, 6), reader.IsDBNull(7) ? 1 : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8), horizon, dimensionHash, rollupInterval);
            job.Validate(); jobs.Add(job);
        }
        return jobs;
    }

    private static void ValidateDerivationLease(WorkerLeaseIdentity lease, string message)
    {
        if (lease is null || lease.Key.Value != "analytics/derivation" || lease.Owner.Value == Guid.Empty || lease.FencingToken.Value <= 0)
            throw new ArgumentException(message, nameof(lease));
    }

    public ValueTask<IReadOnlyList<RollupResult>> ReadBaselineInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        return ReadRollupsAsync(new AnalyticsQueryRequest(job.TargetId, job.MetricKey ?? "host.cpu.percent", job.FromUtc, job.ToUtc, AnalyticsJobBounds.MaximumRows, FiveSecondTimeout, job.TargetRevision, job.SourceCutoffUtc, job.DimensionsSha256), RollupInterval.Hour, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MetricPoint>> ReadRollupInputsAsync(AnalyticsDerivationJob job, RollupInterval interval, CancellationToken cancellationToken)
    {
        job.Validate();
        const string sql = "SELECT observed_at,metric_key,metric_value,dimensions,reset,complete FROM reporting.read_m10_rollup_derivation_inputs(@instance_id,@target_revision,@from_utc,@to_utc,@metric_key,@limit,@snapshot_utc,@rollup_interval,@dimension_hash);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", job.TargetId.Value); command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value); command.Parameters.AddWithValue("from_utc", job.FromUtc); command.Parameters.AddWithValue("to_utc", job.ToUtc); command.Parameters.AddWithValue("metric_key", (object?)job.MetricKey ?? DBNull.Value); command.Parameters.AddWithValue("limit", AnalyticsJobBounds.MaximumRows); command.Parameters.AddWithValue("snapshot_utc", job.SourceCutoffUtc); command.Parameters.AddWithValue("rollup_interval", interval switch { RollupInterval.FiveMinutes => "5m", RollupInterval.Hour => "hour", RollupInterval.Day => "day", _ => throw new ArgumentOutOfRangeException(nameof(interval)) }); command.Parameters.AddWithValue("dimension_hash", (object?)(job.DimensionsSha256 is null ? null : Convert.FromHexString(job.DimensionsSha256)) ?? DBNull.Value);
        var result = new List<MetricPoint>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimensions = ParseDimensions(reader.GetString(3));
            MetricKind kind = MetricCatalogV1.TryGet(reader.GetString(1), out MetricCatalogEntry? entry) && entry!.Kind == MetricKind.Counter ? MetricKind.Counter : MetricKind.Gauge;
            result.Add(new MetricPoint(ReadUtc(reader, 0), reader.GetString(1), reader.GetDouble(2), kind, dimensions, reader.IsDBNull(4) ? false : reader.GetBoolean(4), reader.IsDBNull(5) || reader.GetBoolean(5)));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<AnalyticsForecastInput> ReadForecastInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        // Forecast derivation consumes the bounded daily rollup projection;
        // raw telemetry is deliberately not read here.
        // A forecast is dimension-scoped. A missing fence is intentionally
        // unavailable rather than allowing a bounded page to silently mix
        // volumes or truncate one dimension at the query limit.
        if (job.DimensionsSha256 is null)
            return new AnalyticsForecastInput(Array.Empty<MetricPoint>(), null);
        RollupResult[] rollups = (await ReadRollupsAsync(new AnalyticsQueryRequest(job.TargetId, job.MetricKey ?? "host.memory.available_bytes", job.FromUtc, job.ToUtc, AnalyticsJobBounds.MaximumRows, FiveSecondTimeout, job.TargetRevision, job.SourceCutoffUtc, job.DimensionsSha256), RollupInterval.Day, cancellationToken).ConfigureAwait(false)).ToArray();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var capacityCommand = new NpgsqlCommand("SELECT reporting.get_m10_forecast_capacity_scoped(@instance_id,@target_revision,@metric_key,@dimensions,@snapshot_utc)", connection, transaction) { CommandTimeout = 5 };
        string dimensionsJson = JsonSerializer.Serialize(rollups.Length == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : rollups[0].Dimensions, CamelCaseJson);
        capacityCommand.Parameters.AddWithValue("instance_id", job.TargetId.Value); capacityCommand.Parameters.AddWithValue("target_revision", job.TargetRevision.Value); capacityCommand.Parameters.AddWithValue("metric_key", job.MetricKey ?? "host.memory.available_bytes"); capacityCommand.Parameters.AddWithValue("dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb, dimensionsJson); capacityCommand.Parameters.AddWithValue("snapshot_utc", job.SourceCutoffUtc);
        object? scalar = await capacityCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        double? capacity = scalar is null or DBNull ? null : Convert.ToDouble(scalar, CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AnalyticsForecastInput(Array.Empty<MetricPoint>(), capacity) { DailyRollups = rollups, DimensionsSha256 = job.DimensionsSha256, Dimensions = rollups.Length == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : rollups[0].Dimensions };
    }

    public async ValueTask<IReadOnlyList<AnalyticsEvidenceInput>> ReadEvidenceInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        const string sql = "SELECT occurred_at,\"references\",tombstones FROM reporting.read_m10_evidence_derivation_inputs(@instance_id,@target_revision,@from_utc,@to_utc,@metric_key,@limit,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", job.TargetId.Value); command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value); command.Parameters.AddWithValue("from_utc", job.FromUtc); command.Parameters.AddWithValue("to_utc", job.ToUtc); command.Parameters.AddWithValue("metric_key", (object?)job.MetricKey ?? DBNull.Value); command.Parameters.AddWithValue("limit", AnalyticsJobBounds.MaximumRows); command.Parameters.AddWithValue("snapshot_utc", job.SourceCutoffUtc);
        var result = new List<AnalyticsEvidenceInput>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var input = new AnalyticsEvidenceInput(ReadUtc(reader, 0), ParseEvidenceReferences(reader.GetFieldValue<string>(1)), ParseStringArray(reader.GetFieldValue<string>(2)));
            input.Validate(); result.Add(input);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<IReadOnlyList<EvidencePacket>> ReadIncidentInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken)
    {
        job.Validate();
        const string sql = "SELECT packet_id,occurred_at,evidence_kind,source_run_id,source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,evidence,confidence,visibility_state FROM reporting.read_m10_incident_derivation_inputs(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("instance_id", job.TargetId.Value); command.Parameters.AddWithValue("target_revision", job.TargetRevision.Value); command.Parameters.AddWithValue("from_utc", job.FromUtc); command.Parameters.AddWithValue("to_utc", job.ToUtc); command.Parameters.AddWithValue("limit", AnalyticsJobBounds.MaximumRows); command.Parameters.AddWithValue("snapshot_utc", job.SourceCutoffUtc);
        var result = new List<EvidencePacket>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DateTimeOffset occurred = ReadUtc(reader, 1);
            using JsonDocument evidence = JsonDocument.Parse(reader.GetFieldValue<string>(8));
            IReadOnlyList<EvidenceReference> refs = evidence.RootElement.TryGetProperty("references", out JsonElement refsJson) ? ParseEvidenceReferences(refsJson.GetRawText()) : Array.Empty<EvidenceReference>();
            IReadOnlyList<string> tombstones = evidence.RootElement.TryGetProperty("tombstones", out JsonElement tombJson) ? ParseStringArray(tombJson.GetRawText()) : Array.Empty<string>();
            result.Add(new EvidencePacket { PacketId = reader.GetGuid(0), TargetId = job.TargetId.Value, TargetRevision = job.TargetRevision.Value, WindowStartUtc = occurred.AddMinutes(-15), WindowEndUtc = occurred.AddMinutes(5), References = refs, Tombstones = tombstones, IdentitySha256 = Convert.ToHexString(reader.GetFieldValue<byte[]>(5)).ToLowerInvariant(), SourceCutoffSha256 = Convert.ToHexString(reader.GetFieldValue<byte[]>(6)).ToLowerInvariant(), SourceDigest = Convert.ToHexString(reader.GetFieldValue<byte[]>(4)).ToLowerInvariant(), SourceCutoffUtc = reader.IsDBNull(7) ? null : ReadUtc(reader, 7), SourceRunId = reader.IsDBNull(3) ? null : reader.GetGuid(3), EvidenceKind = reader.GetString(2), Confidence = reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9), CultureInfo.InvariantCulture), State = reader.GetString(10) });
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask CompleteAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, AnalyticsDerivationCompletion completion, string? failureDetail, CancellationToken cancellationToken)
    {
        job.Validate();
        if (lease.Key.Value != "analytics/derivation" || failureDetail?.Length > 1024) throw new ArgumentException("Derivation completion contract is invalid.", nameof(lease));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, job.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT control.complete_m10_derivation_job(@job_id,@status,@error,@owner_execution_id,@fencing_token)", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("job_id", job.JobId); command.Parameters.AddWithValue("status", completion switch { AnalyticsDerivationCompletion.Succeeded => "succeeded", AnalyticsDerivationCompletion.Partial => "partial", AnalyticsDerivationCompletion.Failed => "failed", _ => "cancelled" }); command.Parameters.AddWithValue("error", (object?)failureDetail ?? DBNull.Value); command.Parameters.AddWithValue("owner_execution_id", lease.Owner.Value); command.Parameters.AddWithValue("fencing_token", lease.FencingToken.Value); _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AnalyticsSurfacePage> ReadSurfaceAsync(Guid targetId, string surface, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken cancellationToken)
    {
        if (targetId == Guid.Empty || fromUtc.Offset != TimeSpan.Zero || toUtc.Offset != TimeSpan.Zero || toUtc <= fromUtc || toUtc - fromUtc > TimeSpan.FromDays(7) || limit is < 1 or > 200 || cursor?.Length > 2048)
            throw new ArgumentException("M10 surface query is outside its bounded contract.");
        _ = surface switch { "host/status" or "host/metrics" or "replication/status" or "replication/evidence" or "diagnostics/search" or "jobs" or "incidents" or "evidence-packets" => surface, _ => throw new ArgumentException("Unknown analytics surface.", nameof(surface)) };
        SurfaceCursor? continuation = ParseSurfaceCursor(cursor);
        if (continuation is not null && (continuation.Kind != SurfaceCursorKind || continuation.Surface != surface || continuation.TargetId != targetId || continuation.FromUtc != fromUtc || continuation.ToUtc != toUtc))
            throw new ArgumentException("Surface cursor is bound to a different target, surface, or filter window.", nameof(cursor));
        string sql = surface switch
        {
            "host/status" => "SELECT host_id,target_revision,binding_revision,host_name,binding_state,capability_state,observed_at,generation FROM reporting.list_m10_host_status(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@snapshot_utc);",
            "host/metrics" => "SELECT observed_at,run_id,metric_key,metric_value,dimensions,target_revision FROM reporting.list_m10_host_metrics_scoped(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@cursor_metric,@cursor_dimensions,@snapshot_utc);",
            "replication/status" or "replication/evidence" => "SELECT observed_at,run_id,role,synchronization_state,send_queue_bytes,redo_queue_bytes,visibility_scope,state_available,target_revision,topology_fingerprint FROM reporting.list_m10_replication_scoped(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@cursor_fingerprint,@snapshot_utc);",
            "diagnostics/search" => "SELECT occurred_at,event_id,event_kind,severity,safe_metadata,collected_at,target_revision FROM reporting.search_m10_diagnostics(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@snapshot_utc);",
            "jobs" => "SELECT job_id,job_kind,status,requested_at,started_at,completed_at,attempt,target_revision FROM reporting.list_m10_analytics_jobs(@instance_id,@target_revision,@limit,@cursor_at,@cursor_id,@snapshot_utc);",
            "incidents" => "SELECT thread_id,opened_at,closed_at,current_generation,target_revision FROM reporting.list_m10_incidents_scoped(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@snapshot_utc);",
            _ => "SELECT occurred_at,packet_id,evidence_kind,source_run_id,source_digest,confidence,visibility_state,target_revision FROM reporting.list_m10_evidence_packets(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@cursor_at,@cursor_id,@snapshot_utc);"
        };
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, targetId, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision;
        long generation;
        DateTimeOffset snapshotUtc;
        DateTimeOffset sourceCutoffUtc;
        if (continuation is null)
        {
            targetRevision = await ResolveTargetRevisionAsync(connection, transaction, targetId, null, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
            await using var fence = Command(connection, transaction, "SELECT control.get_m10_surface_generation(@instance_id,@target_revision);", FiveSecondTimeout);
            fence.Parameters.AddWithValue("instance_id", targetId); fence.Parameters.AddWithValue("target_revision", targetRevision);
            generation = Convert.ToInt64(await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            snapshotUtc = DateTimeOffset.UtcNow; sourceCutoffUtc = snapshotUtc;
        }
        else
        {
            targetRevision = await ResolveTargetRevisionAsync(connection, transaction, targetId, new ObservationTargetRevision(continuation.TargetRevision), FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
            generation = continuation.Generation; snapshotUtc = continuation.SnapshotUtc; sourceCutoffUtc = continuation.SourceCutoffUtc;
        }
        await using (var fence = Command(connection, transaction, "SELECT control.assert_m10_surface_fence(@instance_id,@target_revision,@generation,@snapshot_utc,@source_cutoff_utc);", FiveSecondTimeout))
        {
            fence.Parameters.AddWithValue("instance_id", targetId); fence.Parameters.AddWithValue("target_revision", targetRevision); fence.Parameters.AddWithValue("generation", generation);
            fence.Parameters.AddWithValue("snapshot_utc", snapshotUtc); fence.Parameters.AddWithValue("source_cutoff_utc", sourceCutoffUtc);
            _ = await fence.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = Command(connection, transaction, sql, FiveSecondTimeout);
        command.Parameters.AddWithValue("instance_id", targetId); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("from_utc", fromUtc); command.Parameters.AddWithValue("to_utc", toUtc); command.Parameters.AddWithValue("limit", limit);
        command.Parameters.Add("cursor_at", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = (object?)continuation?.TieAt ?? DBNull.Value;
        command.Parameters.Add("cursor_id", NpgsqlTypes.NpgsqlDbType.Uuid).Value = (object?)continuation?.TieId ?? DBNull.Value;
        command.Parameters.Add("cursor_metric", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)continuation?.TieMetric ?? DBNull.Value;
        command.Parameters.Add("cursor_dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb).Value = (object?)continuation?.TieDimensionsJson ?? DBNull.Value;
        command.Parameters.Add("cursor_fingerprint", NpgsqlTypes.NpgsqlDbType.Bytea).Value = (object?)(continuation?.TieFingerprint is null ? null : Convert.FromHexString(continuation.TieFingerprint)) ?? DBNull.Value;
        command.Parameters.AddWithValue("snapshot_utc", snapshotUtc);
        var items = new List<JsonElement>(); string? nextCursor = null;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            object item = surface switch
            {
                "host/status" => new { hostId = reader.GetGuid(0), targetRevision = reader.GetInt64(1), bindingRevision = reader.GetInt64(2), hostName = reader.GetString(3), bindingState = reader.GetString(4), capabilityState = reader.GetString(5), observedAtUtc = ReadUtc(reader, 6), generation = reader.GetInt64(7) },
                "host/metrics" => new { observedAtUtc = ReadUtc(reader, 0), runId = reader.GetGuid(1), metricKey = reader.GetString(2), value = reader.GetDouble(3), dimensions = reader.GetFieldValue<string>(4), targetRevision = reader.GetInt64(5) },
                "replication/status" or "replication/evidence" => new { observedAtUtc = ReadUtc(reader, 0), runId = reader.GetGuid(1), role = reader.GetString(2), synchronizationState = reader.GetString(3), sendQueueBytes = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4), redoQueueBytes = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5), visibilityScope = reader.GetInt16(6), coverage = reader.GetInt16(6) switch { 1 => "complete", 2 => "local_summary", _ => "visibility_gap" }, visibilityGap = reader.GetInt16(6) == 3 ? new { kind = "visibility_gap", reason = "distribution_database_unbound", identity = Convert.ToHexString(reader.GetFieldValue<byte[]>(9)).ToLowerInvariant() } : null, stateAvailable = reader.GetBoolean(7), targetRevision = reader.GetInt64(8) },
                "diagnostics/search" => new { occurredAtUtc = ReadUtc(reader, 0), eventId = reader.GetGuid(1), eventKind = reader.GetString(2), severity = reader.GetInt16(3), safeMetadata = reader.GetFieldValue<string>(4), collectedAtUtc = ReadUtc(reader, 5), targetRevision = reader.GetInt64(6) },
                "jobs" => new { jobId = reader.GetGuid(0), jobKind = reader.GetString(1), status = reader.GetString(2), requestedAtUtc = ReadUtc(reader, 3), startedAtUtc = reader.IsDBNull(4) ? (DateTimeOffset?)null : ReadUtc(reader, 4), completedAtUtc = reader.IsDBNull(5) ? (DateTimeOffset?)null : ReadUtc(reader, 5), attempt = reader.GetInt32(6), targetRevision = reader.GetInt64(7) },
                "incidents" => new { threadId = reader.GetGuid(0), openedAtUtc = ReadUtc(reader, 1), closedAtUtc = reader.IsDBNull(2) ? (DateTimeOffset?)null : ReadUtc(reader, 2), currentGeneration = reader.GetInt64(3), targetRevision = reader.GetInt64(4) },
                _ => new { occurredAtUtc = ReadUtc(reader, 0), packetId = reader.GetGuid(1), evidenceKind = reader.GetString(2), sourceRunId = reader.IsDBNull(3) ? (Guid?)null : reader.GetGuid(3), sourceDigest = Convert.ToHexString(reader.GetFieldValue<byte[]>(4)).ToLowerInvariant(), confidence = reader.IsDBNull(5) ? (double?)null : Convert.ToDouble(reader.GetValue(5), System.Globalization.CultureInfo.InvariantCulture), visibilityState = reader.GetString(6), targetRevision = reader.GetInt64(7) }
            };
            DateTimeOffset at = surface switch { "host/status" => ReadUtc(reader, 6), "jobs" => ReadUtc(reader, 3), "incidents" => ReadUtc(reader, 1), _ => ReadUtc(reader, 0) };
            Guid tieId = reader.GetGuid(surface switch { "host/status" => 0, "host/metrics" or "replication/status" or "replication/evidence" => 1, "diagnostics/search" => 1, "jobs" => 0, "incidents" => 0, _ => 1 });
            nextCursor = surface switch
            {
                "host/metrics" => EncodeSurfaceCursor(new SurfaceCursor(SurfaceCursorKind, surface, targetId, targetRevision, generation, snapshotUtc, sourceCutoffUtc, fromUtc, toUtc, at, tieId, reader.GetString(2), reader.GetFieldValue<string>(4), null)),
                "replication/status" or "replication/evidence" => EncodeSurfaceCursor(new SurfaceCursor(SurfaceCursorKind, surface, targetId, targetRevision, generation, snapshotUtc, sourceCutoffUtc, fromUtc, toUtc, at, tieId, null, null, Convert.ToHexString(reader.GetFieldValue<byte[]>(9)).ToLowerInvariant())),
                _ => EncodeSurfaceCursor(new SurfaceCursor(SurfaceCursorKind, surface, targetId, targetRevision, generation, snapshotUtc, sourceCutoffUtc, fromUtc, toUtc, at, tieId, null, null, null))
            };
            items.Add(JsonSerializer.SerializeToElement(item, CamelCaseJson));
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        string state = items.Count == 0
            ? "no_data"
            : surface is "replication/status" or "replication/evidence" && items.Any(static item =>
                item.TryGetProperty("coverage", out JsonElement coverage) && coverage.ValueKind == JsonValueKind.String &&
                coverage.GetString() == "visibility_gap")
                ? "visibility_gap"
                : "complete";
        return new AnalyticsSurfacePage(surface, state, items, items.Count == limit ? nextCursor : null, sourceCutoffUtc, generation) { TargetRevision = targetRevision, SnapshotUtc = snapshotUtc };
    }

    public async ValueTask<AnalyticsMutationReceipt> StartBackfillAsync(BackfillMutationRequest request, CancellationToken cancellationToken)
    {
        ValidateBackfillMutation(request);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetClaimAsync(connection, transaction, "TargetAdministrator", request.TargetId.ToString("D"), FiveSecondTimeout, request.ActorSid, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT target_revision,job_count,state FROM control.enqueue_m10_backfill(@instance_id,@from_utc,@to_utc,@metric_key,@expected_target_revision,@operation_id,@request_digest,@actor_sid,@correlation_id,@change_reason);"; await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("instance_id", request.TargetId); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("metric_key", (object?)request.MetricKey ?? DBNull.Value); command.Parameters.AddWithValue("expected_target_revision", request.ExpectedRevision); command.Parameters.AddWithValue("operation_id", request.OperationId); command.Parameters.AddWithValue("request_digest", request.RequestDigest.ToArray()); command.Parameters.AddWithValue("actor_sid", request.ActorSid); command.Parameters.AddWithValue("correlation_id", request.CorrelationId); command.Parameters.AddWithValue("change_reason", request.ChangeReason); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Backfill enqueue returned no receipt."); string state = reader.GetString(2); long committedRevision = reader.GetInt64(0); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new AnalyticsMutationReceipt(request.OperationId, state, committedRevision, DateTimeOffset.UtcNow);
    }

    public async ValueTask<AnalyticsMutationReceipt> BindHostAsync(HostBindingMutationRequest request, CancellationToken cancellationToken)
    {
        ValidateHostBindingMutation(request, out Guid hostId, out string hostName, out string fingerprint, out long expectedBindingRevision, out long profileRevision);
        JsonElement profile = request.Body.GetProperty("profile");
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetClaimAsync(connection, transaction, "TargetAdministrator", request.TargetId.ToString("D"), FiveSecondTimeout, request.ActorSid, cancellationToken).ConfigureAwait(false);
        const string sql = "SELECT target_revision,binding_revision,state FROM control.update_m10_host_binding(@instance_id,@host_id,@host_name,@fingerprint,@expected_target_revision,@expected_binding_revision,@profile_revision,@profile,@operation_id,@request_digest,@actor_sid,@correlation_id,@change_reason);"; await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("instance_id", request.TargetId); command.Parameters.AddWithValue("host_id", hostId); command.Parameters.AddWithValue("host_name", hostName); command.Parameters.AddWithValue("fingerprint", Convert.FromHexString(fingerprint)); command.Parameters.AddWithValue("expected_target_revision", request.ExpectedRevision); command.Parameters.AddWithValue("expected_binding_revision", expectedBindingRevision); command.Parameters.AddWithValue("profile_revision", profileRevision); command.Parameters.AddWithValue("profile", NpgsqlTypes.NpgsqlDbType.Jsonb, profile.GetRawText()); command.Parameters.AddWithValue("operation_id", request.OperationId); command.Parameters.AddWithValue("request_digest", request.RequestDigest.ToArray()); command.Parameters.AddWithValue("actor_sid", request.ActorSid); command.Parameters.AddWithValue("correlation_id", request.CorrelationId); command.Parameters.AddWithValue("change_reason", request.ChangeReason); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Host binding returned no receipt."); long bindingRevision = reader.GetInt64(1); string state = reader.GetString(2); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new AnalyticsMutationReceipt(request.OperationId, state, bindingRevision, DateTimeOffset.UtcNow);
    }

    public async ValueTask<AnalyticsMutationReceipt> RecordAttestationAsync(AttestationMutationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); JsonElement body = request.Body; if (request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.ActorSid) || request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 512 || body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("attestedBy", out JsonElement by) || by.GetString() is not { Length: > 0 and <= 256 } attestedBy || !body.TryGetProperty("backupSetReference", out JsonElement reference) || reference.GetString() is not { Length: > 0 and <= 512 } backup || !body.TryGetProperty("expiresAtUtc", out JsonElement expires) || !DateTimeOffset.TryParse(expires.GetString(), out DateTimeOffset expiresUtc) || expiresUtc.Offset != TimeSpan.Zero || !body.TryGetProperty("digest", out JsonElement digest) || digest.GetString() is not { Length: 64 } digestHex || !digestHex.All(Uri.IsHexDigit)) throw new ArgumentException("Recovery attestation payload is outside its bounded contract.");
        if (request.RequestDigest.Length != 32) throw new ArgumentException("Canonical attestation request digest is required.", nameof(request));
        byte[] requestDigest = request.RequestDigest.ToArray(); await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetClaimAsync(connection, transaction, "SecurityAdministrator", "global", FiveSecondTimeout, request.ActorSid, cancellationToken).ConfigureAwait(false); await using var command = Command(connection, transaction, "SELECT system.record_m10_recovery_attestation(@id,@by,@backup,@expires,decode(@digest,'hex'),@request_digest,@actor_sid,@correlation_id,@change_reason)", FiveSecondTimeout); command.Parameters.AddWithValue("id", request.OperationId); command.Parameters.AddWithValue("by", attestedBy); command.Parameters.AddWithValue("backup", backup); command.Parameters.AddWithValue("expires", expiresUtc); command.Parameters.AddWithValue("digest", digestHex.ToLowerInvariant()); command.Parameters.AddWithValue("request_digest", requestDigest); command.Parameters.AddWithValue("actor_sid", request.ActorSid); command.Parameters.AddWithValue("correlation_id", request.CorrelationId); command.Parameters.AddWithValue("change_reason", request.ChangeReason); bool accepted = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is bool value && value; await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new AnalyticsMutationReceipt(request.OperationId, accepted ? "committed" : "replayed", null, DateTimeOffset.UtcNow);
    }

    public async ValueTask<IReadOnlyList<MetricPoint>> ReadMetricPointsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken)
    {
        ValidateQuery(request); RepositoryCallTimeout timeout = FiveSecondTimeout; const string sql = "SELECT observed_at,metric_key,metric_value,dimensions FROM reporting.list_metric_series(@instance_id,@target_revision,@from_utc,@to_utc,@metric_key,@limit,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, timeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, timeout, cancellationToken).ConfigureAwait(false); await using var command = Command(connection, transaction, sql, timeout); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("metric_key", request.MetricKey); command.Parameters.AddWithValue("limit", Math.Min(request.Limit, 1000)); command.Parameters.AddWithValue("snapshot_utc", request.SnapshotUtc ?? DateTimeOffset.UtcNow);
        var result = new List<MetricPoint>(); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dimensions = new Dictionary<string, string>(StringComparer.Ordinal); using JsonDocument doc = JsonDocument.Parse(reader.GetString(3)); foreach (JsonProperty p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) dimensions[p.Name] = p.Value.GetString()!;
            MetricKind kind = MetricCatalogV1.TryGet(reader.GetString(1), out MetricCatalogEntry? entry) && entry!.Kind == MetricKind.Counter ? MetricKind.Counter : MetricKind.Gauge;
            result.Add(new MetricPoint(ReadUtc(reader, 0), reader.GetString(1), reader.GetDouble(2), kind, dimensions));
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<RollupResult>> ReadRollupsAsync(AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken)
        => (await ReadRollupPageAsync(request, interval, null, cancellationToken).ConfigureAwait(false)).Items;

    public async ValueTask<AnalyticsRollupPage> ReadRollupPageAsync(AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken)
    {
        ValidateQuery(request); RepositoryCallTimeout timeout = FiveSecondTimeout; _ = interval switch { RollupInterval.FiveMinutes or RollupInterval.Hour or RollupInterval.Day => true, _ => throw new ArgumentOutOfRangeException(nameof(interval)) };
        // The continuation is part of the fixed SQL query.  Do not fetch the
        // first page and then discard rows in memory: doing so loses rows when
        // the page boundary falls inside a bucket/dimension tie group.
        const string sql = "SELECT bucket_start,metric_key,aggregation,sample_count,value,visibility_state,computed_at,target_revision,rollup_interval,dimensions,dimension_hash,expected_count,reset_count,gap_count,truncated,source_cutoff_utc,catalog_version,algorithm_version,last_value,counter_delta,rate_per_second,generation FROM analytics.list_metric_rollups_scoped(@instance_id,@target_revision,@metric_key,@from_utc,@to_utc,@limit,@snapshot_utc,@rollup_interval,@after_bucket,@after_metric,@after_dimensions,@after_generation,@dimension_hash) WHERE aggregation='avg';";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, timeout, cancellationToken).ConfigureAwait(false);
        RollupCursor? continuation = ParseRollupCursor(cursor);
        if (continuation is not null && (continuation.TargetId != request.TargetId.Value || continuation.MetricKey != request.MetricKey || continuation.Interval != interval || continuation.FromUtc != request.FromUtc || continuation.ToUtc != request.ToUtc || !string.Equals(continuation.QueryDimensionsSha256, request.DimensionsSha256, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Rollup cursor does not match the query.", nameof(cursor));
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, continuation is null ? request.TargetRevision : new ObservationTargetRevision(continuation.TargetRevision), timeout, cancellationToken).ConfigureAwait(false);
        if (continuation is not null && continuation.TargetRevision != targetRevision) throw new InvalidOperationException("Rollup target revision changed.");
        DateTimeOffset snapshotUtc = continuation?.SnapshotUtc ?? request.SnapshotUtc ?? DateTimeOffset.UtcNow;
        await using var generationCommand = Command(connection, transaction, "SELECT control.get_m10_surface_generation(@instance_id,@target_revision);", timeout);
        generationCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); generationCommand.Parameters.AddWithValue("target_revision", targetRevision);
        long generationFence = Convert.ToInt64(await generationCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (generationFence < 1 || continuation is not null && continuation.Generation != generationFence) throw new InvalidOperationException("Rollup generation changed.");
        await using var command = Command(connection, transaction, sql, timeout); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("metric_key", request.MetricKey); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("limit", Math.Min(request.Limit + 1, AnalyticsJobBounds.MaximumRows)); command.Parameters.AddWithValue("snapshot_utc", snapshotUtc); command.Parameters.AddWithValue("rollup_interval", interval switch { RollupInterval.FiveMinutes => "5m", RollupInterval.Hour => "hour", RollupInterval.Day => "day", _ => throw new ArgumentOutOfRangeException(nameof(interval)) }); command.Parameters.AddWithValue("dimension_hash", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)(request.DimensionsSha256 is null ? null : Convert.FromHexString(request.DimensionsSha256)) ?? DBNull.Value);
        command.Parameters.AddWithValue("after_bucket", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)continuation?.TieBucket ?? DBNull.Value);
        command.Parameters.AddWithValue("after_metric", NpgsqlTypes.NpgsqlDbType.Text, (object?)continuation?.TieMetric ?? DBNull.Value);
        command.Parameters.AddWithValue("after_dimensions", NpgsqlTypes.NpgsqlDbType.Bytea, (object?)(continuation is null ? null : Convert.FromHexString(continuation.TieDimensions)) ?? DBNull.Value);
        command.Parameters.AddWithValue("after_generation", NpgsqlTypes.NpgsqlDbType.Bigint, (object?)continuation?.TieGeneration ?? DBNull.Value);
        var result = new List<RollupResult>(); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string physicalInterval = reader.GetString(8);
            RollupInterval returnedInterval = physicalInterval switch { "5m" => RollupInterval.FiveMinutes, "hour" => RollupInterval.Hour, "day" => RollupInterval.Day, _ => throw new InvalidDataException("PostgreSQL returned an unknown rollup interval.") };
            var rollup = new RollupResult { Interval = returnedInterval, BucketStartUtc = ReadUtc(reader, 0), BucketEndUtc = ReadUtc(reader, 0).Add(returnedInterval switch { RollupInterval.FiveMinutes => TimeSpan.FromMinutes(5), RollupInterval.Hour => TimeSpan.FromHours(1), _ => TimeSpan.FromDays(1) }), MetricKey = reader.GetString(1), DimensionsSha256 = Convert.ToHexString(reader.GetFieldValue<byte[]>(10)).ToLowerInvariant(), Count = reader.GetInt32(3), Mean = reader.IsDBNull(4) ? null : reader.GetDouble(4), Expected = reader.GetInt32(11), ResetCount = reader.GetInt32(12), GapCount = reader.GetInt32(13), Truncated = reader.GetBoolean(14), SourceCutoffUtc = reader.IsDBNull(15) ? null : ReadUtc(reader, 15), Min = null, Max = null, Sum = null, Last = reader.IsDBNull(19) ? null : reader.GetDouble(19), CounterDelta = reader.IsDBNull(20) ? null : reader.GetDouble(20), RatePerSecond = reader.IsDBNull(21) ? null : reader.GetDouble(21), Generation = reader.GetInt64(22), VisibilityState = reader.GetString(5), Dimensions = ParseDimensions(reader.GetString(9)) };
            rollup.Validate(); result.Add(rollup);
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        long generation = generationFence;
        bool hasMore = result.Count > request.Limit;
        if (hasMore) result.RemoveRange(request.Limit, result.Count - request.Limit);
        string? next = hasMore && result.Count > 0 ? EncodeRollupCursor(new RollupCursor(request.TargetId.Value, targetRevision, generation, snapshotUtc, request.FromUtc, request.ToUtc, request.MetricKey, request.DimensionsSha256, interval, result[^1].BucketStartUtc, result[^1].MetricKey, result[^1].DimensionsSha256, result[^1].Generation)) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AnalyticsRollupPage(result, hasMore, next, targetRevision, generation, snapshotUtc, snapshotUtc);
    }

    public async ValueTask<IReadOnlyList<BaselineResult>> ReadBaselinesAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken)
    {
        ValidateQuery(request); const string sql = "SELECT metric_key,hour_of_week,complete_days,window_start,window_end,sample_count,mean,stddev,median,mad,p10,p90,coverage,confidence,lower_bound,upper_bound,visibility_state,generation,dimensions,dimension_hash FROM reporting.list_metric_baselines(@instance_id,@target_revision,@metric_key,@from_utc,@to_utc,@limit,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("metric_key", request.MetricKey); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("limit", Math.Min(request.Limit, 200)); command.Parameters.AddWithValue("snapshot_utc", DateTimeOffset.UtcNow);
        var result = new List<BaselineResult>(); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new BaselineResult { MetricKey = reader.GetString(0), HourOfWeek = reader.IsDBNull(1) ? 0 : reader.GetInt32(1), CompleteDays = reader.IsDBNull(2) ? 0 : reader.GetInt32(2), WindowStartUtc = ReadUtc(reader, 3), WindowEndUtc = ReadUtc(reader, 4), SampleCount = reader.GetInt32(5), Mean = reader.IsDBNull(6) ? null : reader.GetDouble(6), Stddev = reader.IsDBNull(7) ? null : reader.GetDouble(7), Median = reader.IsDBNull(8) ? null : reader.GetDouble(8), Mad = reader.IsDBNull(9) ? null : reader.GetDouble(9), P10 = reader.IsDBNull(10) ? null : reader.GetDouble(10), P90 = reader.IsDBNull(11) ? null : reader.GetDouble(11), Coverage = reader.IsDBNull(12) ? 0 : reader.GetDouble(12), Confidence = reader.IsDBNull(13) ? 0 : Convert.ToDouble(reader.GetValue(13), CultureInfo.InvariantCulture), LowerBound = reader.IsDBNull(14) ? null : reader.GetDouble(14), UpperBound = reader.IsDBNull(15) ? null : reader.GetDouble(15), VisibilityState = reader.GetString(16), Generation = reader.GetInt64(17), Dimensions = ParseDimensions(reader.GetString(18)), DimensionsSha256 = Convert.ToHexString(reader.GetFieldValue<byte[]>(19)).ToLowerInvariant() });
        return result;
    }

    public async ValueTask<IReadOnlyList<ForecastResult>> ReadForecastsAsync(AnalyticsQueryRequest request, TimeSpan horizon, CancellationToken cancellationToken)
    {
        ValidateQuery(request); if (horizon < TimeSpan.FromHours(1) || horizon > TimeSpan.FromDays(366)) throw new ArgumentOutOfRangeException(nameof(horizon)); const string sql = "SELECT forecast_id,metric_key,dimension_hash,dimensions,horizon_start,horizon_end,predicted_value,lower_bound,upper_bound,confidence,source_generation,residual,slope_per_day,visibility_state,model FROM reporting.get_m10_forecast_scoped(@instance_id,@target_revision,@metric_key,@dimensions,@horizon,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("metric_key", request.MetricKey); string dimensionsJson = JsonSerializer.Serialize(request.Dimensions ?? new Dictionary<string, string>(StringComparer.Ordinal), CamelCaseJson); command.Parameters.AddWithValue("dimensions", NpgsqlTypes.NpgsqlDbType.Jsonb, dimensionsJson); command.Parameters.AddWithValue("horizon", horizon); command.Parameters.AddWithValue("snapshot_utc", DateTimeOffset.UtcNow);
        var result = new List<ForecastResult>(); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new ForecastResult { ForecastId = reader.GetGuid(0), MetricKey = reader.GetString(1), DimensionsSha256 = Convert.ToHexString(reader.GetFieldValue<byte[]>(2)).ToLowerInvariant(), Dimensions = ParseDimensions(reader.GetString(3)), HorizonStartUtc = ReadUtc(reader, 4), HorizonEndUtc = ReadUtc(reader, 5), Estimate = reader.IsDBNull(6) ? null : reader.GetDouble(6), LowerBound = reader.IsDBNull(7) ? null : reader.GetDouble(7), UpperBound = reader.IsDBNull(8) ? null : reader.GetDouble(8), Confidence = reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9), System.Globalization.CultureInfo.InvariantCulture), SourceGeneration = reader.GetInt64(10), Residual = reader.IsDBNull(11) ? 0 : reader.GetDouble(11), SlopePerDay = reader.IsDBNull(12) ? null : reader.GetDouble(12), VisibilityState = reader.GetString(13), Model = reader.GetString(14) });
        return result;
    }

    public async ValueTask<IReadOnlyList<IncidentThread>> ReadIncidentsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken)
    {
        ValidateQuery(request); const string sql = "SELECT thread_id,opened_at,closed_at,current_generation FROM reporting.list_incidents(@instance_id,@target_revision,@from_utc,@to_utc,@limit,@snapshot_utc);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); DateTimeOffset snapshotUtc = DateTimeOffset.UtcNow;
        await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("from_utc", request.FromUtc); command.Parameters.AddWithValue("to_utc", request.ToUtc); command.Parameters.AddWithValue("limit", Math.Min(request.Limit, 100)); command.Parameters.AddWithValue("snapshot_utc", snapshotUtc);
        var result = new List<IncidentThread>(); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new IncidentThread(reader.GetGuid(0), ReadUtc(reader, 1), reader.IsDBNull(2) ? null : ReadUtc(reader, 2), new List<EvidencePacket>(), reader.GetInt64(3)));
        await reader.DisposeAsync().ConfigureAwait(false);
        // Hydrate packet bodies through the fixed reporting projection.  The
        // server role never reads analytics tables directly and never returns
        // an empty placeholder for an incident's evidence.
        for (int index = 0; index < result.Count; index++)
        {
            IncidentThread thread = result[index];
            await using var evidenceCommand = Command(connection, transaction, "SELECT occurred_at,packet_id,evidence_kind,source_run_id,source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,evidence,confidence,visibility_state FROM reporting.get_m10_incident_evidence(@instance_id,@target_revision,@thread_id,@limit,@snapshot_utc);", FiveSecondTimeout);
            evidenceCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); evidenceCommand.Parameters.AddWithValue("target_revision", targetRevision); evidenceCommand.Parameters.AddWithValue("thread_id", thread.ThreadId); evidenceCommand.Parameters.AddWithValue("limit", 256); evidenceCommand.Parameters.AddWithValue("snapshot_utc", snapshotUtc);
            var packets = new List<EvidencePacket>(); var packetIds = new HashSet<Guid>();
            await using (NpgsqlDataReader evidenceReader = await evidenceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await evidenceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    EvidencePacket packet = HydrateEvidencePacket(evidenceReader, request.TargetId.Value, targetRevision);
                    if (packetIds.Add(packet.PacketId)) packets.Add(packet);
                }

            await using var generationCommand = Command(connection, transaction, "SELECT thread_id,generation,observed_at,correlation_digest,supersedes_previous,evidence_packet_id FROM reporting.get_m10_incident_generations(@instance_id,@target_revision,@thread_id,@limit,@snapshot_utc);", FiveSecondTimeout);
            generationCommand.Parameters.AddWithValue("instance_id", request.TargetId.Value); generationCommand.Parameters.AddWithValue("target_revision", targetRevision); generationCommand.Parameters.AddWithValue("thread_id", thread.ThreadId); generationCommand.Parameters.AddWithValue("limit", 256); generationCommand.Parameters.AddWithValue("snapshot_utc", snapshotUtc);
            var generations = new List<IncidentGeneration>();
            await using (NpgsqlDataReader generationReader = await generationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await generationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    byte[] correlation = generationReader.GetFieldValue<byte[]>(3); if (correlation.Length != 32) throw new InvalidDataException("Incident generation correlation digest is invalid.");
                    generations.Add(new IncidentGeneration(generationReader.GetGuid(0), generationReader.GetInt64(1), ReadUtc(generationReader, 2), Convert.ToHexString(correlation).ToLowerInvariant(), generationReader.GetBoolean(4)) { EvidencePacketId = generationReader.IsDBNull(5) ? null : generationReader.GetGuid(5) });
                }
            result[index] = thread with { Packets = packets, Generations = generations };
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return result;
    }

    public async ValueTask StoreRollupsAsync(AnalyticsJobRequest request, IReadOnlyList<RollupResult> rollups, CancellationToken cancellationToken)
    {
        AnalyticsJobRequest.Validate(request); RepositoryCallTimeout timeout = FiveSecondTimeout; ArgumentNullException.ThrowIfNull(rollups); if (request.TargetId is null) throw new ArgumentException("A target is required for rollup writes.", nameof(request)); if (rollups.Count > 100_000) throw new ArgumentOutOfRangeException(nameof(rollups));
        const string sql = "SELECT * FROM analytics.commit_metric_rollups(@operation_id,@job_id,@instance_id,@target_revision,@work_key,@owner_execution_id,@fencing_token,@request_digest,@rows,@result_digest);";
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, timeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, timeout, cancellationToken).ConfigureAwait(false);
        string json = JsonSerializer.Serialize(rollups.Select(ToPersistence).ToArray(), CamelCaseJson); AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create("rollup", request, json, json);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, timeout, cancellationToken).ConfigureAwait(false);
        await using (var command = Command(connection, transaction, sql, timeout)) { command.Parameters.AddWithValue("operation_id", replay.OperationId); command.Parameters.AddWithValue("job_id", request.JobId); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("work_key", request.WorkKey); command.Parameters.AddWithValue("owner_execution_id", request.Lease.Owner.Value); command.Parameters.AddWithValue("fencing_token", request.Lease.FencingToken.Value); command.Parameters.AddWithValue("request_digest", replay.RequestDigest); command.Parameters.AddWithValue("rows", NpgsqlTypes.NpgsqlDbType.Jsonb, json); command.Parameters.AddWithValue("result_digest", replay.ResultDigest); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask StoreBaselineAsync(AnalyticsJobRequest request, IReadOnlyList<BaselineResult> baselines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baselines); if (baselines.Count > 100_000) throw new ArgumentOutOfRangeException(nameof(baselines));
        string json = JsonSerializer.Serialize(baselines.Select(ToPersistence).ToArray(), CamelCaseJson);
        await ExecuteAnalyticsWriteAsync("analytics.commit_metric_baselines", request, json, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask StoreForecastAsync(AnalyticsJobRequest request, ForecastResult forecast, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forecast); string json = JsonSerializer.Serialize(ToPersistence(forecast), CamelCaseJson);
        await ExecuteAnalyticsWriteAsync("analytics.commit_metric_forecast", request, json, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask StoreEvidenceAsync(AnalyticsJobRequest request, EvidencePacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet); string json = JsonSerializer.Serialize(ToPersistence(request, packet), CamelCaseJson);
        await ExecuteAnalyticsWriteAsync("analytics.commit_evidence_packet", request, json, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask StoreIncidentAsync(AnalyticsJobRequest request, IncidentThread thread, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IncidentPersistenceDto persistence = ToPersistence(request, thread);
        await ExecuteIncidentWriteAsync(request, persistence, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask StoreIncidentGenerationAsync(AnalyticsJobRequest request, IncidentGeneration generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (generation.ThreadId == Guid.Empty || generation.Generation < 1 || generation.ObservedAtUtc.Offset != TimeSpan.Zero || !IsHexSha256(generation.CorrelationSha256)) throw new ArgumentException("Incident generation persistence contract rejected.", nameof(generation));
        string json = JsonSerializer.Serialize(ToPersistence(generation), CamelCaseJson);
        await ExecuteAnalyticsWriteAsync("analytics.commit_incident_generation", request, json, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SqlObserver.Domain.Retention.RetentionPreview> PreviewAsync(RetentionPreviewQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); if (request.MaxEntries is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(request));
        (DateTimeOffset evaluatedAt, string? cursorPayload) = ParseRetentionCursor(request.Cursor);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT data_class,parent_schema,parent_table,partition_name,range_start,range_end,eligible,reason FROM system.preview_m10_retention(@now_utc,@cursor) LIMIT @limit", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("now_utc", evaluatedAt); command.Parameters.AddWithValue("cursor", (object?)cursorPayload ?? DBNull.Value); command.Parameters.AddWithValue("limit", request.MaxEntries + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<RetentionPreviewEntry>(request.MaxEntries); var identities = new HashSet<string>(StringComparer.Ordinal); bool truncated = false; string? nextCursor = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entries.Count >= request.MaxEntries) { truncated = true; break; }
            string dataClass = reader.GetString(0), parentSchema = reader.GetString(1), parentTable = reader.GetString(2), partition = reader.GetString(3);
            var entry = new RetentionPreviewEntry(dataClass, parentSchema, parentTable, partition, ReadUtc(reader, 4), ReadUtc(reader, 5), reader.GetBoolean(6), reader.GetString(7)); entry.Validate();
            if (identities.Add($"{dataClass}\0{parentSchema}\0{parentTable}\0{partition}")) entries.Add(entry);
            nextCursor = EncodeRetentionCursor(evaluatedAt, dataClass, parentSchema, parentTable, partition);
        }
        var result = new SqlObserver.Domain.Retention.RetentionPreview(entries, truncated, truncated ? nextCursor : null, evaluatedAt); result.Validate(request.MaxEntries); return result;
    }
    public async ValueTask<RetentionExecutionResult> ExecuteAsync(RetentionExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); if (request.ExecutionId == Guid.Empty) throw new ArgumentException("An execution id is required.", nameof(request));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetClaimAsync(connection, transaction, "SecurityAdministrator", "global", FiveSecondTimeout, request.Authorization.ActorSid.ToString(), cancellationToken).ConfigureAwait(false); await SetRetentionContextAsync(connection, transaction, request, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await AssertRetentionRevisionAsync(connection, transaction, request, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        bool accepted;
        if (request.Operation == "drop")
        {
            await using var drop = Command(connection, transaction, "SELECT system.drop_m10_partition(@execution_id)", FiveSecondTimeout); drop.Parameters.AddWithValue("execution_id", request.ExecutionId); accepted = (await drop.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is bool value && value;
        }
        else
        {
            await using var detach = Command(connection, transaction, "SELECT system.detach_m10_partition(@parent_schema,@parent_table,@partition_name,@execution_id)", FiveSecondTimeout); detach.Parameters.AddWithValue("parent_schema", request.ParentSchema); detach.Parameters.AddWithValue("parent_table", request.ParentTable); detach.Parameters.AddWithValue("partition_name", request.PartitionName); detach.Parameters.AddWithValue("execution_id", request.ExecutionId); accepted = (await detach.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is bool value && value;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return new RetentionExecutionResult(request.ExecutionId, accepted ? (request.Operation == "drop" ? RetentionExecutionState.Dropped : RetentionExecutionState.Grace) : RetentionExecutionState.Blocked, accepted ? null : RetentionBlockReason.DependencyPending, request.Operation == "drop" ? null : accepted ? DateTimeOffset.UtcNow.AddHours(24) : null);
    }
    public async ValueTask<RetentionPolicyReadResult> GetPolicyAsync(string dataClass, CancellationToken cancellationToken)
    {
        ValidateDataClass(dataClass); await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using var command = new NpgsqlCommand("SELECT data_class,enabled,retain_for,minimum_partitions_to_keep,policy_revision FROM system.get_m10_retention_policy(@data_class)", connection) { CommandTimeout = 5 }; command.Parameters.AddWithValue("data_class", dataClass); await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException("Retention policy not found."); TimeSpan? duration = reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeSpan>(2); return new RetentionPolicyReadResult(new RetentionPolicy(reader.GetString(0), reader.GetBoolean(1), duration, reader.GetInt32(3)), reader.GetInt64(4), DateTimeOffset.UtcNow);
    }
    public async ValueTask<RetentionPolicyReadResult> UpdatePolicyAsync(RetentionPolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); RetentionPolicyValidator.Validate(request.Policy); await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetClaimAsync(connection, transaction, "SecurityAdministrator", "global", FiveSecondTimeout, request.Authorization.ActorSid.ToString(), cancellationToken).ConfigureAwait(false); await using var command = Command(connection, transaction, "SELECT system.update_m10_retention_policy(@data_class,@enabled,@retain_for,@minimum,@expected,@changed_by,@reason)", FiveSecondTimeout); command.Parameters.AddWithValue("data_class", request.Policy.DataClass); command.Parameters.AddWithValue("enabled", request.Policy.Enabled); command.Parameters.AddWithValue("retain_for", (object?)request.Policy.RetainFor ?? DBNull.Value); command.Parameters.AddWithValue("minimum", request.Policy.MinimumPartitionsToKeep); command.Parameters.AddWithValue("expected", request.ExpectedRevision); command.Parameters.AddWithValue("changed_by", request.Authorization.ActorSid.ToString()); command.Parameters.AddWithValue("reason", request.ChangeReason); await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); return await GetPolicyAsync(request.Policy.DataClass, cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, RepositoryCallTimeout timeout) => new(sql, connection, transaction) { CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout) };
    private static readonly JsonSerializerOptions CamelCaseJson = new(JsonSerializerDefaults.Web);
    private async ValueTask ExecuteAnalyticsWriteAsync(string function, AnalyticsJobRequest request, string payload, CancellationToken cancellationToken)
    {
        AnalyticsJobRequest.Validate(request); if (request.TargetId is null || System.Text.Encoding.UTF8.GetByteCount(payload) > 8 * 1024 * 1024) throw new ArgumentException("Bounded target analytics write is required.", nameof(request));
        AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create(function switch { "analytics.commit_metric_baselines" => "baseline", "analytics.commit_metric_forecast" => "forecast", "analytics.commit_evidence_packet" => "evidence", "analytics.commit_incident_thread" => "incident", "analytics.commit_incident_generation" => "incident-generation", _ => throw new ArgumentException("Unknown analytics write function.", nameof(function)) }, request, payload, payload);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false); await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false); await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false); await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
         string sql = $"SELECT {function}(@operation_id,@job_id,@instance_id,@target_revision,@work_key,@owner_execution_id,@fencing_token,@request_digest,@payload,@result_digest);"; await using var command = Command(connection, transaction, sql, FiveSecondTimeout); command.Parameters.AddWithValue("operation_id", replay.OperationId); command.Parameters.AddWithValue("job_id", request.JobId); command.Parameters.AddWithValue("instance_id", request.TargetId.Value); command.Parameters.AddWithValue("target_revision", targetRevision); command.Parameters.AddWithValue("work_key", request.WorkKey); command.Parameters.AddWithValue("owner_execution_id", request.Lease.Owner.Value); command.Parameters.AddWithValue("fencing_token", request.Lease.FencingToken.Value); command.Parameters.AddWithValue("request_digest", replay.RequestDigest); command.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("result_digest", replay.ResultDigest); await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false); await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the thread and every generation under one transaction.  The
    /// generation port is intentionally exercised here (rather than being a
    /// declaration-only capability on the interface), so a correlation write
    /// cannot silently lose supersession/evidence links.
    /// </summary>
    private async ValueTask ExecuteIncidentWriteAsync(AnalyticsJobRequest request, IncidentPersistenceDto persistence, CancellationToken cancellationToken)
    {
        AnalyticsJobRequest.Validate(request);
        if (request.TargetId is null) throw new ArgumentException("A target is required for incident writes.", nameof(request));
        if (persistence.Generations.Count > 256) throw new ArgumentOutOfRangeException(nameof(persistence));
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection, transaction, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        await SetScopeAsync(connection, transaction, request.TargetId.Value, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        long targetRevision = await ResolveTargetRevisionAsync(connection, transaction, request.TargetId.Value, request.TargetRevision, FiveSecondTimeout, cancellationToken).ConfigureAwait(false);
        AnalyticsJobRequest effectiveRequest = request.TargetRevision is null
            ? request with { TargetRevision = new ObservationTargetRevision(targetRevision) }
            : request;

        async ValueTask WriteAsync(string function, string payload)
        {
            AnalyticsReplayEnvelope replay = AnalyticsReplayContract.Create(function switch
            {
                "analytics.commit_incident_thread" => "incident",
                "analytics.commit_incident_generation" => "incident-generation",
                _ => throw new ArgumentException("Unknown incident write function.", nameof(function))
            }, effectiveRequest, payload, payload);
            await using var command = Command(connection, transaction, $"SELECT {function}(@operation_id,@job_id,@instance_id,@target_revision,@work_key,@owner_execution_id,@fencing_token,@request_digest,@payload,@result_digest);", FiveSecondTimeout);
            command.Parameters.AddWithValue("operation_id", replay.OperationId);
            command.Parameters.AddWithValue("job_id", request.JobId);
            command.Parameters.AddWithValue("instance_id", request.TargetId.Value);
            command.Parameters.AddWithValue("target_revision", targetRevision);
            command.Parameters.AddWithValue("work_key", request.WorkKey);
            command.Parameters.AddWithValue("owner_execution_id", request.Lease.Owner.Value);
            command.Parameters.AddWithValue("fencing_token", request.Lease.FencingToken.Value);
            command.Parameters.AddWithValue("request_digest", replay.RequestDigest);
            command.Parameters.AddWithValue("payload", NpgsqlTypes.NpgsqlDbType.Jsonb, payload);
            command.Parameters.AddWithValue("result_digest", replay.ResultDigest);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAsync("analytics.commit_incident_thread", JsonSerializer.Serialize(persistence, CamelCaseJson)).ConfigureAwait(false);
        foreach (IncidentGeneration generation in persistence.Generations)
            await WriteAsync("analytics.commit_incident_generation", JsonSerializer.Serialize(ToPersistence(generation), CamelCaseJson)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async Task SetScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT set_config('sqlobserver.target_scope', @target_scope, true);", timeout);
        command.Parameters.AddWithValue("target_scope", targetId.ToString("D")); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async Task SetClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string role, string authorizationScope, RepositoryCallTimeout timeout, string actorSid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorSid)) throw new ArgumentException("Authenticated actor is required.", nameof(actorSid));
        await using var command = Command(connection, transaction, "SELECT set_config('sqlobserver.role', @role, true), set_config('sqlobserver.authorization_scope', @authorization_scope, true), set_config('sqlobserver.actor_sid', @actor_sid, true);", timeout);
        command.Parameters.AddWithValue("role", role); command.Parameters.AddWithValue("authorization_scope", authorizationScope); command.Parameters.AddWithValue("actor_sid", actorSid); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async Task SetRetentionContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RetentionExecutionRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT set_config('sqlobserver.retention_change_reason', @reason, true), set_config('sqlobserver.retention_correlation_id', @correlation_id, true), set_config('sqlobserver.retention_operation_id', @operation_id, true), set_config('sqlobserver.retention_request_digest', @request_digest, true);", timeout);
        command.Parameters.AddWithValue("reason", request.ChangeReason); command.Parameters.AddWithValue("correlation_id", request.CorrelationId.ToString("D")); command.Parameters.AddWithValue("operation_id", request.OperationId.ToString("D")); command.Parameters.AddWithValue("request_digest", request.RequestDigest); await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    private static async Task AssertRetentionRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, RetentionExecutionRequest request, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        // Revision probes are fixed SECURITY DEFINER functions.  The runtime
        // role must never read retention_execution/policy directly.
        string sql = request.Operation == "drop" ? "SELECT system.get_m10_retention_execution_revision(@execution_id)" : "SELECT system.get_m10_retention_policy_revision(@parent_schema,@parent_table,@data_class)";
        await using var command = Command(connection, transaction, sql, timeout); command.Parameters.AddWithValue("execution_id", request.ExecutionId); command.Parameters.AddWithValue("parent_schema", request.ParentSchema); command.Parameters.AddWithValue("parent_table", request.ParentTable); command.Parameters.AddWithValue("data_class", request.DataClass); object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false); if (value is null || value is DBNull || Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != request.ExpectedPolicyRevision) throw new InvalidOperationException("Retention policy revision conflict.");
    }
    private static async Task<long> ResolveTargetRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid targetId, ObservationTargetRevision? requested, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT control.resolve_m10_target_revision(@instance_id,@requested_revision)", timeout);
        command.Parameters.AddWithValue("instance_id", targetId); command.Parameters.AddWithValue("requested_revision", (object?)requested?.Value ?? DBNull.Value);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull) throw new KeyNotFoundException("Observation target revision not found.");
        long revision = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        if (revision <= 0) throw new InvalidOperationException("Observation target revision is invalid.");
        return revision;
    }

    private static RollupPersistenceDto ToPersistence(RollupResult row)
    {
        ArgumentNullException.ThrowIfNull(row); row.Validate();
        return new RollupPersistenceDto(row.BucketStartUtc, row.Interval switch { RollupInterval.FiveMinutes => "5m", RollupInterval.Hour => "hour", RollupInterval.Day => "day", _ => throw new ArgumentOutOfRangeException(nameof(row)) }, row.MetricKey, "avg", row.Count, row.Mean, row.VisibilityState, row.Dimensions, row.DimensionsSha256, row.Expected, row.ResetCount, row.GapCount, row.Truncated, row.SourceCutoffUtc, row.Last, row.CounterDelta, row.RatePerSecond, row.Generation);
    }

    private static BaselinePersistenceDto ToPersistence(BaselineResult row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.WindowStartUtc is null || row.WindowEndUtc is null || row.WindowStartUtc >= row.WindowEndUtc || row.WindowStartUtc.Value.Offset != TimeSpan.Zero || row.WindowEndUtc.Value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Persisted baselines require a non-empty UTC window.", nameof(row));
        if (row.SampleCount < 0 || row.Generation < 1 || row.Confidence is < 0 or > 1 || row.Coverage is < 0 or > 1 || !IsVisibility(row.VisibilityState)) throw new ArgumentException("Baseline persistence contract rejected.", nameof(row));
        if (!IsHexSha256(row.DimensionsSha256) || !string.Equals(CanonicalDimensions.Sha256(row.Dimensions), row.DimensionsSha256, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Baseline dimension identity is invalid.", nameof(row));
        return new BaselinePersistenceDto(row.MetricKey, row.Dimensions, row.DimensionsSha256, row.HourOfWeek, row.CompleteDays, row.WindowStartUtc.Value, row.WindowEndUtc.Value, row.SampleCount, row.Mean, row.Stddev, row.Median, row.Mad, row.P10, row.P90, row.Coverage, row.Confidence, row.LowerBound, row.UpperBound, row.VisibilityState, row.Generation);
    }

    private static ForecastPersistenceDto ToPersistence(ForecastResult row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.HorizonStartUtc.Offset != TimeSpan.Zero || row.HorizonEndUtc.Offset != TimeSpan.Zero || row.HorizonEndUtc <= row.HorizonStartUtc || row.SourceGeneration < 1 || row.Confidence is < 0 or > 1 || !IsVisibility(row.VisibilityState) || !IsHexSha256(row.DimensionsSha256) || !string.Equals(CanonicalDimensions.Sha256(row.Dimensions), row.DimensionsSha256, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Forecast persistence contract rejected.", nameof(row));
        Guid id = row.ForecastId ?? DeterministicGuid(row.MetricKey, row.DimensionsSha256, row.HorizonStartUtc, row.HorizonEndUtc, row.SourceGeneration);
        return new ForecastPersistenceDto(id, row.MetricKey, row.Dimensions, row.DimensionsSha256, row.HorizonStartUtc, row.HorizonEndUtc, row.Model, row.Estimate, row.LowerBound, row.UpperBound, row.Confidence, row.SourceGeneration, row.VisibilityState);
    }

    private static EvidencePersistenceDto ToPersistence(AnalyticsJobRequest request, EvidencePacket row)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(row);
        Guid targetId = request.TargetId?.Value ?? row.TargetId ?? Guid.Empty;
        long targetRevision = request.TargetRevision?.Value ?? row.TargetRevision;
        string sourceDigest = row.SourceDigest;
        string expectedCutoff = Sha256Text(row.SourceCutoffUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "none");
        if (targetId == Guid.Empty || targetRevision < 1 || row.PacketId == Guid.Empty || row.WindowStartUtc.Offset != TimeSpan.Zero || row.WindowEndUtc.Offset != TimeSpan.Zero || row.WindowEndUtc - row.WindowStartUtc != TimeSpan.FromMinutes(20) || row.Confidence is < 0 or > 1 || !IsHexSha256(row.SourceCutoffSha256) || !string.Equals(expectedCutoff, row.SourceCutoffSha256, StringComparison.OrdinalIgnoreCase) || !IsHexSha256(sourceDigest) || !IsHexSha256(row.IdentitySha256) || !string.Equals(Sha256Text(row.IdentitySha256 + "|" + row.SourceCutoffSha256), sourceDigest, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Evidence persistence contract rejected.", nameof(row));
        if (row.References.Count > 256 || row.Tombstones.Count > 256 || row.Evidence.Count > 64 || row.EvidenceKind is not { Length: > 0 and <= 64 } || row.Trigger is not { Length: > 0 and <= 64 } || row.Algorithm is not { Length: > 0 and <= 64 } || row.Generation < 1 || !IsVisibility(row.State))
            throw new ArgumentException("Evidence identity or reference bounds rejected.", nameof(row));
        EvidenceReferencePersistenceDto[] references = row.References.Select(ToPersistence).OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Identity, StringComparer.Ordinal).ToArray();
        string[] tombstones = NormalizeTombstones(row.Tombstones);
        Dictionary<string, object?> evidence = NormalizeEvidence(row.Evidence);
        // The SQL function persists only p_row.evidence.  Keep the typed
        // arrays and immutable metadata in that object as well as on the
        // envelope so writes and reads have one lossless representation.
        evidence["references"] = references;
        evidence["tombstones"] = tombstones;
        evidence["packetId"] = row.PacketId;
        evidence["targetId"] = targetId;
        evidence["targetRevision"] = targetRevision;
        evidence["trigger"] = row.Trigger;
        evidence["algorithm"] = row.Algorithm;
        evidence["generation"] = row.Generation;
        evidence["state"] = row.State;
        evidence["truncated"] = row.Truncated;
        evidence["evidenceKind"] = row.EvidenceKind;
        evidence["sourceDigest"] = row.SourceDigest;
        evidence["sourceCutoffSha256"] = row.SourceCutoffSha256;
        evidence["sourceCutoffUtc"] = row.SourceCutoffUtc;
        evidence["sourceRunId"] = row.SourceRunId;
        return new EvidencePersistenceDto(row.PacketId, targetId, targetRevision, row.WindowStartUtc.AddMinutes(15), row.EvidenceKind, row.Trigger, row.SourceCutoffUtc, row.SourceDigest, row.SourceRunId, row.IdentitySha256, row.SourceCutoffSha256, references, tombstones, row.Algorithm, row.Generation, row.State, row.Truncated, evidence, row.Confidence, row.State);
    }

    private static EvidenceReferencePersistenceDto ToPersistence(EvidenceReference row)
    {
        if (row is null || row.OccurredAtUtc.Offset != TimeSpan.Zero || row.Type is not { Length: > 0 and <= 64 } || row.Identity is not { Length: > 0 and <= 256 } || !AllowedEvidenceTypes.Contains(row.Type))
            throw new ArgumentException("Evidence reference is not allowlisted.", nameof(row));
        return new EvidenceReferencePersistenceDto(row.Type, row.Identity, row.OccurredAtUtc);
    }

    private static IncidentPersistenceDto ToPersistence(AnalyticsJobRequest request, IncidentThread row)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(row);
        Guid targetId = request.TargetId?.Value ?? Guid.Empty; long targetRevision = request.TargetRevision?.Value ?? 0;
        if (targetId == Guid.Empty || targetRevision < 1 || row.ThreadId == Guid.Empty || row.OpenedAtUtc.Offset != TimeSpan.Zero || row.ClosedAtUtc?.Offset != TimeSpan.Zero || row.ClosedAtUtc < row.OpenedAtUtc || row.CurrentGeneration < 1)
            throw new ArgumentException("Incident persistence contract rejected.", nameof(row));
        if (row.Packets.Count > 256 || row.Generations.Count > 256 || row.Packets.Any(p => p.PacketId == Guid.Empty || p.TargetId is not null && p.TargetId != targetId || p.TargetRevision > 0 && p.TargetRevision != targetRevision)) throw new ArgumentException("Incident packet bounds rejected.", nameof(row));
        var packetIds = row.Packets.Select(p => p.PacketId).ToHashSet();
        if (row.Generations.Any(g => g.ThreadId != row.ThreadId || g.Generation < 1 || g.Generation > row.CurrentGeneration || g.ObservedAtUtc.Offset != TimeSpan.Zero || !IsHexSha256(g.CorrelationSha256) || g.Generation == 1 && g.SupersedesPrevious || g.EvidencePacketId is not null && !packetIds.Contains(g.EvidencePacketId.Value)))
            throw new ArgumentException("Incident generation linkage is invalid.", nameof(row));
        return new IncidentPersistenceDto(row.ThreadId, targetId, targetRevision, row.OpenedAtUtc, row.ClosedAtUtc, "open", new { packetIds = packetIds.ToArray(), packetCount = row.Packets.Count }, row.CurrentGeneration, row.Generations);
    }

    private static IncidentGenerationPersistenceDto ToPersistence(IncidentGeneration row)
    {
        if (row is null || row.ThreadId == Guid.Empty || row.Generation < 1 || row.ObservedAtUtc.Offset != TimeSpan.Zero || !IsHexSha256(row.CorrelationSha256) || row.State is not { Length: > 0 and <= 32 } || row.Details.Count > 32 || row.Generation == 1 && row.SupersedesPrevious)
            throw new ArgumentException("Incident generation persistence contract rejected.", nameof(row));
        return new IncidentGenerationPersistenceDto(row.ThreadId, row.Generation, row.ObservedAtUtc, row.State, row.EvidencePacketId, row.CorrelationSha256, row.SupersedesPrevious, NormalizeEvidence(row.Details));
    }

    private static readonly HashSet<string> AllowedEvidenceTypes = new(StringComparer.Ordinal) { "metric", "alert", "activity", "deadlock", "replication", "host", "health" };
    private static readonly HashSet<string> AllowedEvidenceFields = new(StringComparer.Ordinal) { "metricKey", "metricValue", "unit", "severity", "status", "reason", "source", "dimensions", "visibilityGap" };
    private static string[] NormalizeTombstones(IReadOnlyList<string> values)
    {
        if (values.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 256)) throw new ArgumentException("Evidence tombstone bounds exceeded.", nameof(values));
        return values.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Take(256).ToArray();
    }
    private static Dictionary<string, object?> NormalizeEvidence(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in values)
        {
            if (!AllowedEvidenceFields.Contains(key) || key.Length > 64) throw new ArgumentException("Evidence contains a non-allowlisted field.", nameof(values));
            JsonElement element = JsonSerializer.SerializeToElement(value, CamelCaseJson); ValidateSafeEvidence(element, 0);
            result[key] = element.Clone();
        }
        return result;
    }
    private static void ValidateSafeEvidence(JsonElement value, int depth)
    {
        if (depth > 4) throw new ArgumentException("Evidence nesting bound exceeded.");
        switch (value.ValueKind)
        {
            case JsonValueKind.String when value.GetString() is { Length: > 512 }: throw new ArgumentException("Evidence string bound exceeded.");
            case JsonValueKind.Object: foreach (JsonProperty p in value.EnumerateObject()) { if (p.Name.Length > 64) throw new ArgumentException("Evidence key bound exceeded."); ValidateSafeEvidence(p.Value, depth + 1); } break;
            case JsonValueKind.Array: if (value.GetArrayLength() > 64) throw new ArgumentException("Evidence array bound exceeded."); foreach (JsonElement item in value.EnumerateArray()) ValidateSafeEvidence(item, depth + 1); break;
        }
    }

    private static bool IsVisibility(string value) => value is "complete" or "partial" or "unavailable" or "unsupported";
    private static bool IsHexSha256(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Sha256Text(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static Guid DeterministicGuid(string metric, string dimensionsSha256, DateTimeOffset start, DateTimeOffset end, long generation)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"forecast-v1|{metric}|{dimensionsSha256}|{start:O}|{end:O}|{generation}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static Dictionary<string, string> ParseDimensions(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Rollup dimensions are not an object."); var result = new Dictionary<string, string>(StringComparer.Ordinal); foreach (JsonProperty property in document.RootElement.EnumerateObject()) if (property.Value.ValueKind == JsonValueKind.String) result.Add(property.Name, property.Value.GetString()!); return result;
    }

    private static EvidencePacket HydrateEvidencePacket(NpgsqlDataReader reader, Guid targetId, long targetRevision)
    {
        DateTimeOffset occurred = ReadUtc(reader, 0); byte[] source = reader.GetFieldValue<byte[]>(4); byte[] identity = reader.GetFieldValue<byte[]>(5); byte[] cutoff = reader.GetFieldValue<byte[]>(6);
        if (targetId == Guid.Empty || targetRevision < 1 || source.Length != 32 || identity.Length != 32 || cutoff.Length != 32) throw new InvalidDataException("Evidence packet identity or digest is invalid.");
        DateTimeOffset? sourceCutoffUtc = reader.IsDBNull(7) ? null : ReadUtc(reader, 7);
        string sourceHex = Convert.ToHexString(source).ToLowerInvariant(), identityHex = Convert.ToHexString(identity).ToLowerInvariant(), cutoffHex = Convert.ToHexString(cutoff).ToLowerInvariant();
        if (!string.Equals(Sha256Text(sourceCutoffUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "none"), cutoffHex, StringComparison.OrdinalIgnoreCase) || !string.Equals(Sha256Text(identityHex + "|" + cutoffHex), sourceHex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Evidence packet digest does not match its normalized content.");
        using JsonDocument document = JsonDocument.Parse(reader.GetFieldValue<string>(8)); JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Evidence payload must be an object.");
        JsonElement references = root.TryGetProperty("references", out JsonElement refs) ? refs : default;
        var hydratedRefs = new List<EvidenceReference>();
        if (references.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Evidence references are missing.");
        foreach (JsonElement item in references.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out JsonElement type) || !item.TryGetProperty("identity", out JsonElement id) || !item.TryGetProperty("occurredAtUtc", out JsonElement at) ||
                type.GetString() is not { Length: > 0 } || id.GetString() is not { Length: > 0 } || !AllowedEvidenceTypes.Contains(type.GetString()!) || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset referenceAt) || referenceAt.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Evidence reference is invalid.");
            hydratedRefs.Add(new EvidenceReference(type.GetString()!, id.GetString()!, referenceAt));
            if (hydratedRefs.Count > 256) throw new InvalidDataException("Evidence packet reference bound exceeded.");
        }
        string computedIdentity = Sha256Text(string.Join("\n", hydratedRefs.Select(x => $"{x.Type}|{x.Identity}|{x.OccurredAtUtc:O}")));
        if (!string.Equals(computedIdentity, identityHex, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Evidence packet identity does not match its references.");
        var tombstones = new List<string>();
        if (!root.TryGetProperty("tombstones", out JsonElement tombstoneJson) || tombstoneJson.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Evidence tombstones are missing.");
        foreach (JsonElement tombstone in tombstoneJson.EnumerateArray())
        {
            if (tombstone.ValueKind != JsonValueKind.String || tombstone.GetString() is not { Length: > 0 and <= 256 } value) throw new InvalidDataException("Evidence tombstone is invalid.");
            tombstones.Add(value);
            if (tombstones.Count > 256) throw new InvalidDataException("Evidence packet tombstone bound exceeded.");
        }
        if (tombstones.Count != tombstones.Distinct(StringComparer.Ordinal).Count()) throw new InvalidDataException("Evidence tombstones are not canonical.");
        if (root.TryGetProperty("packetId", out JsonElement persistedPacket) && persistedPacket.ValueKind == JsonValueKind.String && (!Guid.TryParse(persistedPacket.GetString(), out Guid parsedPacket) || parsedPacket != reader.GetGuid(1))) throw new InvalidDataException("Evidence packet identity mismatch.");
        if (root.TryGetProperty("targetId", out JsonElement persistedTarget) && persistedTarget.ValueKind == JsonValueKind.String && (!Guid.TryParse(persistedTarget.GetString(), out Guid parsedTarget) || parsedTarget != targetId)) throw new InvalidDataException("Evidence target identity mismatch.");
        if (root.TryGetProperty("targetRevision", out JsonElement persistedRevision) && (!persistedRevision.TryGetInt64(out long parsedRevision) || parsedRevision != targetRevision)) throw new InvalidDataException("Evidence target revision mismatch.");
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is "references" or "tombstones" or "packetId" or "targetId" or "targetRevision" or "trigger" or "algorithm" or "generation" or "state" or "truncated" or "evidenceKind" or "sourceDigest" or "sourceCutoffSha256" or "sourceCutoffUtc" or "sourceRunId") continue;
            if (!AllowedEvidenceFields.Contains(property.Name)) throw new InvalidDataException("Evidence contains a non-allowlisted field.");
            ValidateSafeEvidence(property.Value, 0);
            evidence[property.Name] = property.Value.Clone();
        }
        string trigger = root.TryGetProperty("trigger", out JsonElement triggerElement) && triggerElement.ValueKind == JsonValueKind.String ? triggerElement.GetString()! : reader.GetString(2);
        string algorithm = root.TryGetProperty("algorithm", out JsonElement algorithmElement) && algorithmElement.ValueKind == JsonValueKind.String ? algorithmElement.GetString()! : EvidencePacket.AlgorithmVersion;
        long generation = root.TryGetProperty("generation", out JsonElement generationElement) && generationElement.TryGetInt64(out long parsedGeneration) ? parsedGeneration : 1;
        string state = root.TryGetProperty("state", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.String ? stateElement.GetString()! : reader.GetString(10);
        bool truncated = root.TryGetProperty("truncated", out JsonElement truncatedElement) && truncatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False && truncatedElement.GetBoolean();
        if (trigger is not { Length: > 0 and <= 64 } || algorithm is not { Length: > 0 and <= 64 } || generation < 1 || !IsVisibility(state)) throw new InvalidDataException("Evidence metadata is invalid.");
        return new EvidencePacket
        {
            PacketId = reader.GetGuid(1), TargetId = targetId, TargetRevision = targetRevision, WindowStartUtc = occurred.AddMinutes(-15), WindowEndUtc = occurred.AddMinutes(5),
            References = hydratedRefs, IdentitySha256 = identityHex, SourceCutoffSha256 = cutoffHex,
            SourceDigest = sourceHex, SourceRunId = reader.IsDBNull(3) ? null : reader.GetGuid(3), EvidenceKind = reader.GetString(2), SourceCutoffUtc = sourceCutoffUtc,
            Trigger = trigger, Algorithm = algorithm, Generation = generation, State = state, Truncated = truncated,
            Evidence = evidence, Tombstones = tombstones, Confidence = reader.IsDBNull(9) ? 0 : Convert.ToDouble(reader.GetValue(9), System.Globalization.CultureInfo.InvariantCulture)
        };
    }
    private sealed record RollupPersistenceDto(DateTimeOffset BucketStartUtc, string Interval, string MetricKey, string Aggregation, int SampleCount, double? Value, string VisibilityState, IReadOnlyDictionary<string, string> Dimensions, string DimensionHash, int ExpectedCount, int ResetCount, int GapCount, bool Truncated, DateTimeOffset? SourceCutoffUtc, double? LastValue, double? CounterDelta, double? RatePerSecond, long Generation);
    private sealed record BaselinePersistenceDto(string MetricKey, IReadOnlyDictionary<string, string> Dimensions, string DimensionHash, int HourOfWeek, int CompleteDays, DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, int SampleCount, double? Mean, double? Stddev, double? Median, double? Mad, double? P10, double? P90, double Coverage, double Confidence, double? LowerBound, double? UpperBound, string VisibilityState, long Generation);
    private sealed record ForecastPersistenceDto(Guid ForecastId, string MetricKey, IReadOnlyDictionary<string, string> Dimensions, string DimensionsSha256, DateTimeOffset HorizonStartUtc, DateTimeOffset HorizonEndUtc, string Model, double? PredictedValue, double? LowerBound, double? UpperBound, double Confidence, long SourceGeneration, string VisibilityState);
    private sealed record EvidenceReferencePersistenceDto(string Type, string Identity, DateTimeOffset OccurredAtUtc);
    private sealed record EvidencePersistenceDto(Guid PacketId, Guid TargetId, long TargetRevision, DateTimeOffset OccurredAtUtc, string EvidenceKind, string Trigger, DateTimeOffset? SourceCutoffUtc, string SourceDigest, Guid? SourceRunId, string IdentitySha256, string SourceCutoffSha256, IReadOnlyList<EvidenceReferencePersistenceDto> References, IReadOnlyList<string> Tombstones, string Algorithm, long Generation, string State, bool Truncated, IReadOnlyDictionary<string, object?> Evidence, double Confidence, string VisibilityState);
    private sealed record IncidentGenerationPersistenceDto(Guid ThreadId, long Generation, DateTimeOffset ObservedAtUtc, string State, Guid? EvidencePacketId, string CorrelationDigest, bool SupersedesPrevious, IReadOnlyDictionary<string, object?> Details);
    private sealed record IncidentPersistenceDto(Guid ThreadId, Guid TargetId, long TargetRevision, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc, string State, object Summary, long CurrentGeneration, IReadOnlyList<IncidentGeneration> Generations);
    private static DateTimeOffset ReadUtc(NpgsqlDataReader reader, int ordinal) { DateTime value = reader.GetDateTime(ordinal); return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)); }
    private static string[] ParseStringArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Repository returned a non-array tombstone set.");
        return document.RootElement.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(256).ToArray();
    }
    private static List<EvidenceReference> ParseEvidenceReferences(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Repository returned a non-array evidence reference set.");
        var references = new List<EvidenceReference>();
        foreach (JsonElement item in document.RootElement.EnumerateArray().Take(100_000))
        {
            string? type = item.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() : null;
            string? identity = item.TryGetProperty("identity", out JsonElement identityValue) ? identityValue.GetString() : null;
            string? occurredText = item.TryGetProperty("occurredAtUtc", out JsonElement occurredValue) ? occurredValue.GetString() : null;
            if (type is null || identity is null || !DateTimeOffset.TryParse(occurredText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset occurred) || occurred.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Repository returned an invalid evidence reference.");
            references.Add(new EvidenceReference(type, identity, occurred));
        }
        return references;
    }
    private sealed record RollupCursor(Guid TargetId, long TargetRevision, long Generation, DateTimeOffset SnapshotUtc, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string MetricKey, string? QueryDimensionsSha256, RollupInterval Interval, DateTimeOffset TieBucket, string TieMetric, string TieDimensions, long TieGeneration);
    private static RollupCursor? ParseRollupCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 1024) throw new ArgumentException("Rollup cursor exceeds its bound.", nameof(value));
        try
        {
            string padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(padded)); JsonElement root = document.RootElement;
            if (root.GetProperty("v").GetString() != "m10.rollup.v1") throw new ArgumentException("Rollup cursor version is invalid.", nameof(value));
            DateTimeOffset Read(string name) => DateTimeOffset.TryParse(root.GetProperty(name).GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) && parsed.Offset == TimeSpan.Zero ? parsed : throw new ArgumentException("Rollup cursor UTC field is invalid.", nameof(value));
            Guid target = root.GetProperty("targetId").GetGuid(); long revision = root.GetProperty("targetRevision").GetInt64(); long generation = root.GetProperty("generation").GetInt64();
            string metric = root.GetProperty("metricKey").GetString() ?? throw new ArgumentException("Rollup cursor metric is invalid.", nameof(value));
            RollupInterval interval = root.GetProperty("interval").GetString() switch { "5m" => RollupInterval.FiveMinutes, "hour" => RollupInterval.Hour, "day" => RollupInterval.Day, _ => throw new ArgumentException("Rollup cursor interval is invalid.", nameof(value)) };
            string tieMetric = root.GetProperty("tieMetric").GetString() ?? throw new ArgumentException("Rollup cursor tie is invalid.", nameof(value)); string tieDimensions = root.GetProperty("tieDimensions").GetString() ?? throw new ArgumentException("Rollup cursor tie is invalid.", nameof(value));
            if (target == Guid.Empty || revision < 1 || generation < 1 || metric.Length is 0 or > 128 || tieDimensions.Length != 64 || !tieDimensions.All(Uri.IsHexDigit)) throw new ArgumentException("Rollup cursor fence is invalid.", nameof(value));
            DateTimeOffset snapshot = Read("snapshotUtc"), from = Read("fromUtc"), to = Read("toUtc"), tieBucket = Read("tieBucket"); long tieGeneration = root.GetProperty("tieGeneration").GetInt64();
            if (to <= from || tieGeneration < 1) throw new ArgumentException("Rollup cursor fence is invalid.", nameof(value));
            string? queryDimensions = root.TryGetProperty("dimensionsSha256", out JsonElement queryDimensionsElement) && queryDimensionsElement.ValueKind == JsonValueKind.String ? queryDimensionsElement.GetString() : null;
            if (queryDimensions is not null && (queryDimensions.Length != 64 || !queryDimensions.All(Uri.IsHexDigit))) throw new ArgumentException("Rollup cursor dimension identity is invalid.", nameof(value));
            return new RollupCursor(target, revision, generation, snapshot, from, to, metric, queryDimensions, interval, tieBucket, tieMetric, tieDimensions, tieGeneration);
        }
        catch (KeyNotFoundException) { throw new ArgumentException("Rollup cursor is missing required fields.", nameof(value)); }
        catch (InvalidOperationException) { throw new ArgumentException("Rollup cursor fields have invalid types.", nameof(value)); }
        catch (JsonException) { throw new ArgumentException("Rollup cursor is invalid.", nameof(value)); }
        catch (FormatException) { throw new ArgumentException("Rollup cursor is invalid.", nameof(value)); }
    }
    private static string EncodeRollupCursor(RollupCursor cursor)
    {
        string json = JsonSerializer.Serialize(new { v = "m10.rollup.v1", targetId = cursor.TargetId, targetRevision = cursor.TargetRevision, generation = cursor.Generation, snapshotUtc = cursor.SnapshotUtc.ToString("O"), fromUtc = cursor.FromUtc.ToString("O"), toUtc = cursor.ToUtc.ToString("O"), metricKey = cursor.MetricKey, dimensionsSha256 = cursor.QueryDimensionsSha256, interval = cursor.Interval switch { RollupInterval.FiveMinutes => "5m", RollupInterval.Hour => "hour", _ => "day" }, tieBucket = cursor.TieBucket.ToString("O"), tieMetric = cursor.TieMetric, tieDimensions = cursor.TieDimensions, tieGeneration = cursor.TieGeneration }, CamelCaseJson);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static (DateTimeOffset SnapshotUtc, string? SqlCursor) ParseRetentionCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (DateTimeOffset.UtcNow, null);
        if (cursor.Length > 1024) throw new ArgumentException("Retention cursor exceeds its bound.", nameof(cursor));
        try
        {
            string padded = cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - cursor.Length % 4) % 4);
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(padded));
            JsonElement root = document.RootElement;
            if (root.GetProperty("v").GetString() != "m10.retention.v1" || root.GetProperty("o").GetString() != "dataClass,parentSchema,parentTable,partitionName") throw new ArgumentException("Retention cursor contract is invalid.", nameof(cursor));
            if (!DateTimeOffset.TryParse(root.GetProperty("s").GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset snapshot) || snapshot.Offset != TimeSpan.Zero) throw new ArgumentException("Retention cursor snapshot is invalid.", nameof(cursor));
            foreach (string key in new[] { "d", "s1", "t", "p" }) if (!root.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } text || !System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z0-9_.-]+$")) throw new ArgumentException("Retention cursor key is invalid.", nameof(cursor));
            return (snapshot, cursor);
        }
        catch (KeyNotFoundException) { throw new ArgumentException("Retention cursor is missing required fields.", nameof(cursor)); }
        catch (JsonException) { throw new ArgumentException("Retention cursor is invalid.", nameof(cursor)); }
        catch (FormatException) { throw new ArgumentException("Retention cursor is invalid.", nameof(cursor)); }
    }
    private static string EncodeRetentionCursor(DateTimeOffset snapshot, string dataClass, string parentSchema, string parentTable, string partition)
    {
        string json = JsonSerializer.Serialize(new { v = "m10.retention.v1", o = "dataClass,parentSchema,parentTable,partitionName", s = snapshot.ToString("O"), d = dataClass, s1 = parentSchema, t = parentTable, p = partition });
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private const string SurfaceCursorKind = "m10.surface.v1";
    private sealed record SurfaceCursor(
        string Kind, string Surface, Guid TargetId, long TargetRevision, long Generation,
        DateTimeOffset SnapshotUtc, DateTimeOffset SourceCutoffUtc,
        DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset TieAt, Guid TieId,
        string? TieMetric, string? TieDimensionsJson, string? TieFingerprint, string FiltersJson = "{}");
    private static SurfaceCursor? ParseSurfaceCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string kind = root.GetProperty("kind").GetString() ?? throw new ArgumentException("Surface cursor kind is missing.", nameof(value));
            string surface = root.GetProperty("surface").GetString() ?? throw new ArgumentException("Surface cursor surface is missing.", nameof(value));
            Guid targetId = root.GetProperty("targetId").GetGuid();
            long targetRevision = root.GetProperty("targetRevision").GetInt64();
            long generation = root.GetProperty("generation").GetInt64();
            DateTimeOffset snapshot = ParseCursorUtc(root, "snapshotUtc", value);
            DateTimeOffset sourceCutoff = ParseCursorUtc(root, "sourceCutoffUtc", value);
            DateTimeOffset from = ParseCursorUtc(root, "fromUtc", value);
            DateTimeOffset to = ParseCursorUtc(root, "toUtc", value);
            DateTimeOffset tieAt = ParseCursorUtc(root, "tieAt", value);
            Guid tieId = root.GetProperty("tieId").GetGuid();
            string? metric = root.TryGetProperty("tieMetric", out JsonElement metricValue) ? metricValue.GetString() : null;
            string? dimensions = root.TryGetProperty("tieDimensions", out JsonElement dimensionsValue) && dimensionsValue.ValueKind != JsonValueKind.Null ? dimensionsValue.GetRawText() : null;
            string? fingerprint = root.TryGetProperty("tieFingerprint", out JsonElement fingerprintValue) ? fingerprintValue.GetString() : null;
            string filters = root.TryGetProperty("filters", out JsonElement filtersValue) ? filtersValue.GetRawText() : "{}";
            if (targetId == Guid.Empty || targetRevision < 1 || generation < 1 || to <= from || kind.Length > 64 || surface.Length > 64 || tieId == Guid.Empty || filters != "{}" || (fingerprint is not null && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))) throw new ArgumentException("Surface cursor fence is invalid.", nameof(value));
            return new SurfaceCursor(kind, surface, targetId, targetRevision, generation, snapshot, sourceCutoff, from, to, tieAt, tieId, metric, dimensions, fingerprint, filters);
        }
        catch (FormatException) { throw new ArgumentException("Surface cursor is invalid.", nameof(value)); }
        catch (KeyNotFoundException) { throw new ArgumentException("Surface cursor is missing required fence fields.", nameof(value)); }
        catch (InvalidOperationException) { throw new ArgumentException("Surface cursor fields have invalid types.", nameof(value)); }
        catch (JsonException) { throw new ArgumentException("Surface cursor is invalid.", nameof(value)); }
    }
    private static string EncodeSurfaceCursor(SurfaceCursor cursor)
    {
        JsonElement? dimensions = null;
        if (cursor.TieDimensionsJson is not null)
        {
            using JsonDocument document = JsonDocument.Parse(cursor.TieDimensionsJson);
            dimensions = document.RootElement.Clone();
        }
        string json = JsonSerializer.Serialize(new
        {
            kind = cursor.Kind, surface = cursor.Surface, targetId = cursor.TargetId,
            targetRevision = cursor.TargetRevision, generation = cursor.Generation,
            snapshotUtc = cursor.SnapshotUtc.ToString("O"), sourceCutoffUtc = cursor.SourceCutoffUtc.ToString("O"),
            fromUtc = cursor.FromUtc.ToString("O"), toUtc = cursor.ToUtc.ToString("O"),
            filters = new { },
            tieAt = cursor.TieAt.ToString("O"), tieId = cursor.TieId, tieMetric = cursor.TieMetric,
            tieDimensions = dimensions, tieFingerprint = cursor.TieFingerprint
        }, CamelCaseJson);
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static DateTimeOffset ParseCursorUtc(JsonElement root, string property, string source)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || !DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) || parsed.Offset != TimeSpan.Zero)
            throw new ArgumentException($"Surface cursor {property} is invalid.", nameof(source));
        return parsed;
    }
    private static void ValidateQuery(AnalyticsQueryRequest request) { ArgumentNullException.ThrowIfNull(request); if (request.TargetId is null || request.MetricKey is null || request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.SnapshotUtc?.Offset != TimeSpan.Zero || request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > TimeSpan.FromDays(90) || request.Limit is < 1 or > 100_000 || request.DimensionsSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) || request.Dimensions is not null && !string.Equals(CanonicalDimensions.Sha256(request.Dimensions), request.DimensionsSha256 ?? CanonicalDimensions.Sha256(null), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Analytics query is outside its bounded contract.", nameof(request)); }
    private static void ValidateBackfillMutation(BackfillMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetId == Guid.Empty || request.OperationId == Guid.Empty || request.CorrelationId == Guid.Empty || request.ExpectedRevision < 1 || request.RequestDigest.Length != 32 || string.IsNullOrWhiteSpace(request.ActorSid) || string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 512 || request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > TimeSpan.FromDays(90) || request.MetricKey is not null && (request.MetricKey.Length is 0 or > 128 || !request.MetricKey.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Backfill mutation contract is invalid.", nameof(request));
    }
    private void ValidateHostBindingMutation(HostBindingMutationRequest request, out Guid hostId, out string hostName, out string fingerprint, out long bindingRevision, out long profileRevision)
    {
        ArgumentNullException.ThrowIfNull(request);
        hostId = Guid.Empty; hostName = string.Empty; fingerprint = string.Empty; bindingRevision = 0; profileRevision = 0;
        JsonElement body = request.Body;
        string? parsedHostName = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("hostName", out JsonElement nameValue) ? nameValue.GetString() : null;
        string? parsedFingerprint = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("identityFingerprint", out JsonElement fpValue) ? fpValue.GetString() : null;
        string[] allowedProfileFields = ["osFamily", "osVersion", "cpuCount", "memoryBytes", "capabilityState"];
        if (request.TargetId == Guid.Empty || request.OperationId == Guid.Empty || request.CorrelationId == Guid.Empty || request.ExpectedRevision < 1 || request.RequestDigest.Length != 32 || string.IsNullOrWhiteSpace(request.ActorSid) || string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 512 || body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("hostId", out JsonElement hostValue) || !Guid.TryParse(hostValue.GetString(), out hostId) || hostId == Guid.Empty || parsedHostName is not { Length: > 0 and <= 512 } || parsedHostName.Any(char.IsControl) || parsedFingerprint is not { Length: 64 } || !parsedFingerprint.All(Uri.IsHexDigit) || HostIdentityFingerprint.Parse(parsedFingerprint).ToStableHostId(fingerprintKey) != hostId || !body.TryGetProperty("bindingRevision", out JsonElement bindingValue) || !bindingValue.TryGetInt64(out bindingRevision) || bindingRevision < 0 || !body.TryGetProperty("profileRevision", out JsonElement profileValue) || !profileValue.TryGetInt64(out profileRevision) || profileRevision < 1 || !body.TryGetProperty("profile", out JsonElement profile) || profile.ValueKind != JsonValueKind.Object || profile.EnumerateObject().Any(property => !allowedProfileFields.Contains(property.Name, StringComparer.Ordinal)) || !profile.TryGetProperty("osFamily", out JsonElement osFamily) || osFamily.GetString() is not { Length: > 0 and <= 128 } || osFamily.GetString()!.Any(char.IsControl) || !profile.TryGetProperty("osVersion", out JsonElement osVersion) || osVersion.GetString() is not { Length: > 0 and <= 128 } || osVersion.GetString()!.Any(char.IsControl) || !profile.TryGetProperty("cpuCount", out JsonElement cpuValue) || !cpuValue.TryGetInt32(out int cpuCount) || cpuCount is < 1 or > 65536 || !profile.TryGetProperty("memoryBytes", out JsonElement memoryValue) || !memoryValue.TryGetInt64(out long memoryBytes) || memoryBytes < 0 || memoryBytes > 1L << 60 || !profile.TryGetProperty("capabilityState", out JsonElement capability) || capability.GetString() is not ("available" or "partial" or "unsupported" or "permission_denied"))
            throw new ArgumentException("Host binding/profile mutation contract is invalid.", nameof(request));
        hostName = parsedHostName; fingerprint = parsedFingerprint;
    }
    private static void ValidateDataClass(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128) throw new ArgumentException("Invalid retention data class.", nameof(value)); }
}
