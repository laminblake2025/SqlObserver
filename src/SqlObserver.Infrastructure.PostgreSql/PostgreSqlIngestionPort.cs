using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Uses bounded PostgreSQL binary COPY into transaction-local staging, then conflict-safe insertion
/// into partitioned parents so retry counts and lease fencing remain honest.
/// </summary>
public sealed class PostgreSqlIngestionPort : ITelemetryIngestionPort, IDiagnosticEventIngestionPort
{
    private const string CreateMetricStageSql = """
        CREATE TEMPORARY TABLE sqlobserver_metric_stage
        (
            observed_at timestamptz NOT NULL,
            sample_id uuid NOT NULL,
            instance_id uuid NOT NULL,
            metric_key text NOT NULL,
            metric_value double precision NOT NULL,
            dimensions jsonb NOT NULL,
            collected_at timestamptz NOT NULL
        ) ON COMMIT DROP;
        """;
    private const string CopyMetricStageSql = """
        COPY pg_temp.sqlobserver_metric_stage
        (observed_at, sample_id, instance_id, metric_key, metric_value, dimensions, collected_at)
        FROM STDIN (FORMAT BINARY)
        """;
    // This is the generic M2 compatibility path.  It intentionally does not
    // manufacture a target revision (or collection-run provenance): M10
    // analytics/backfill admit raw rows only through an authoritative
    // collection_run target-revision join.  M4+ collector commits use the
    // fenced collection-run persistence contract instead.
    private const string InsertMetricsSql = """
        INSERT INTO telemetry.raw_metric_sample
        (observed_at, sample_id, instance_id, metric_key, metric_value, dimensions, collected_at)
        SELECT observed_at, sample_id, instance_id, metric_key, metric_value, dimensions, collected_at
        FROM pg_temp.sqlobserver_metric_stage
        ON CONFLICT (observed_at, sample_id) DO NOTHING
        RETURNING observed_at, sample_id;
        """;
    private const string ValidateMetricReplaySql = """
        SELECT control.validate_metric_replay(
            @observed_ats,
            @sample_ids,
            @instance_ids,
            @metric_keys,
            @metric_values,
            @dimensions);
        """;
    private const string CreateEventStageSql = """
        CREATE TEMPORARY TABLE sqlobserver_event_stage
        (
            occurred_at timestamptz NOT NULL,
            event_id uuid NOT NULL,
            instance_id uuid NOT NULL,
            event_kind text NOT NULL,
            protected_payload_id uuid,
            collected_at timestamptz NOT NULL
        ) ON COMMIT DROP;
        """;
    private const string CopyEventStageSql = """
        COPY pg_temp.sqlobserver_event_stage
        (occurred_at, event_id, instance_id, event_kind, protected_payload_id, collected_at)
        FROM STDIN (FORMAT BINARY)
        """;
    private const string InsertEventsSql = """
        INSERT INTO events.diagnostic_event
        (
            occurred_at,
            event_id,
            instance_id,
            event_kind,
            severity,
            protected_payload_id,
            safe_metadata,
            collected_at
        )
        SELECT
            occurred_at,
            event_id,
            instance_id,
            event_kind,
            0,
            protected_payload_id,
            '{}'::jsonb,
            collected_at
        FROM pg_temp.sqlobserver_event_stage
        ON CONFLICT (occurred_at, event_id) DO NOTHING
        RETURNING occurred_at, event_id;
        """;
    private const string ValidateEventReplaySql = """
        SELECT control.validate_diagnostic_event_replay(
            @occurred_ats,
            @event_ids,
            @instance_ids,
            @event_kinds,
            @protected_payload_ids,
            @collected_ats);
        """;
    private const string RepositoryClockSql = "SELECT clock_timestamp();";

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlIngestionPort(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<IngestionResult> IngestTelemetryAsync(
        TelemetryIngestionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(timeout.Token)
                .ConfigureAwait(false);

            try
            {
                await PrepareProtectedWriteAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        CreateMetricStageSql,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);

                DateTimeOffset collectedAt = await ReadRepositoryClockAsync(
                        connection,
                        transaction,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await CopyMetricsAsync(connection, request.Batch.Samples, collectedAt, timeout.Token)
                    .ConfigureAwait(false);

                int insertedBytes = 0;
                int insertedCount = 0;
                Dictionary<IngestionIdentity, Queue<int>> sizes = CreateMetricSizeIndex(request.Batch.Samples);
                await using (var insert = new NpgsqlCommand(InsertMetricsSql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                })
                await using (NpgsqlDataReader reader = await insert
                    .ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        var identity = new IngestionIdentity(
                            NormalizePostgreSqlTimestamp(reader.GetDateTime(0)),
                            reader.GetGuid(1));
                        insertedBytes = checked(insertedBytes + TakePersistedSize(sizes, identity));
                        insertedCount++;
                    }
                }

                await AssertMetricReplayConsistentAsync(
                        connection,
                        transaction,
                        request.Batch.Samples,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);

                await PostgreSqlPartitionMaintenancePort.AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                DateTimeOffset completedAt = await ReadRepositoryClockAsync(
                        connection,
                        transaction,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

                return IngestionResult.FromTelemetryBatch(
                    request.Batch,
                    insertedCount,
                    request.Batch.ItemCount - insertedCount,
                    rejectedCount: 0,
                    insertedBytes,
                    completedAt);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("telemetry ingestion", exception);
        }
    }

    public async ValueTask<IngestionResult> IngestDiagnosticEventsAsync(
        DiagnosticEventIngestionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource timeout = PostgreSqlRuntimeSupport.CreateTimeoutScope(
            request.Timeout,
            cancellationToken);

        try
        {
            await using NpgsqlConnection connection = await _dataSource
                .OpenConnectionAsync(timeout.Token)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(timeout.Token)
                .ConfigureAwait(false);

            try
            {
                await PrepareProtectedWriteAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await ExecuteNonQueryAsync(
                        connection,
                        transaction,
                        CreateEventStageSql,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await CopyEventsAsync(connection, request.Batch.Events, timeout.Token).ConfigureAwait(false);

                int insertedBytes = 0;
                int insertedCount = 0;
                Dictionary<IngestionIdentity, Queue<int>> sizes = CreateEventSizeIndex(request.Batch.Events);
                await using (var insert = new NpgsqlCommand(InsertEventsSql, connection, transaction)
                {
                    CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(request.Timeout),
                })
                await using (NpgsqlDataReader reader = await insert
                    .ExecuteReaderAsync(timeout.Token)
                    .ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        var identity = new IngestionIdentity(
                            NormalizePostgreSqlTimestamp(reader.GetDateTime(0)),
                            reader.GetGuid(1));
                        insertedBytes = checked(insertedBytes + TakePersistedSize(sizes, identity));
                        insertedCount++;
                    }
                }

                await AssertEventReplayConsistentAsync(
                        connection,
                        transaction,
                        request.Batch.Events,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);

                await PostgreSqlPartitionMaintenancePort.AssertLeaseAsync(
                        connection,
                        transaction,
                        request.Lease,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                DateTimeOffset completedAt = await ReadRepositoryClockAsync(
                        connection,
                        transaction,
                        request.Timeout,
                        timeout.Token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(timeout.Token).ConfigureAwait(false);

                return IngestionResult.FromDiagnosticEventBatch(
                    request.Batch,
                    insertedCount,
                    request.Batch.ItemCount - insertedCount,
                    rejectedCount: 0,
                    insertedBytes,
                    completedAt);
            }
            catch
            {
                await RollbackWithoutMaskingAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw PostgreSqlRuntimeSupport.CreateTimeoutException("diagnostic-event ingestion", exception);
        }
    }

    private static async Task PrepareProtectedWriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SqlObserver.Domain.Coordination.WorkerLeaseIdentity lease,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(
                connection,
                transaction,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        await PostgreSqlPartitionMaintenancePort.AssertLeaseAsync(
                connection,
                transaction,
                lease,
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CopyMetricsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MetricSample> samples,
        DateTimeOffset collectedAt,
        CancellationToken cancellationToken)
    {
        await using NpgsqlBinaryImporter importer = await connection
            .BeginBinaryImportAsync(CopyMetricStageSql, cancellationToken)
            .ConfigureAwait(false);

        foreach (MetricSample sample in samples)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(sample.ObservedAtUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(sample.SampleId.Value, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(sample.InstanceId.Value, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(sample.MetricId.Value, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(sample.Value, NpgsqlDbType.Double, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(SerializeDimensions(sample.Dimensions), NpgsqlDbType.Jsonb, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(collectedAt.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken)
                .ConfigureAwait(false);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyEventsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<DiagnosticEventEnvelope> events,
        CancellationToken cancellationToken)
    {
        await using NpgsqlBinaryImporter importer = await connection
            .BeginBinaryImportAsync(CopyEventStageSql, cancellationToken)
            .ConfigureAwait(false);

        foreach (DiagnosticEventEnvelope diagnosticEvent in events)
        {
            await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(diagnosticEvent.OccurredAtUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(diagnosticEvent.EventId.Value, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(diagnosticEvent.InstanceId.Value, NpgsqlDbType.Uuid, cancellationToken)
                .ConfigureAwait(false);
            await importer.WriteAsync(diagnosticEvent.Kind.Value, NpgsqlDbType.Text, cancellationToken)
                .ConfigureAwait(false);

            if (diagnosticEvent.ProtectedPayload is null)
            {
                await importer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await importer.WriteAsync(
                        diagnosticEvent.ProtectedPayload.PayloadId.Value,
                        NpgsqlDbType.Uuid,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await importer.WriteAsync(diagnosticEvent.CollectedAtUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken)
                .ConfigureAwait(false);
        }

        await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeDimensions(IReadOnlyList<MetricDimension> dimensions)
    {
        var safeDimensions = new Dictionary<string, string>(dimensions.Count, StringComparer.Ordinal);
        foreach (MetricDimension dimension in dimensions)
        {
            safeDimensions.Add(dimension.Key, dimension.Value);
        }

        return JsonSerializer.Serialize(safeDimensions);
    }

    private static Dictionary<IngestionIdentity, Queue<int>> CreateMetricSizeIndex(
        IReadOnlyList<MetricSample> samples)
    {
        var result = new Dictionary<IngestionIdentity, Queue<int>>();
        foreach (MetricSample sample in samples)
        {
            AddSize(
                result,
                new IngestionIdentity(
                    NormalizePostgreSqlTimestamp(sample.ObservedAtUtc.UtcDateTime),
                    sample.SampleId.Value),
                sample.EstimatedSizeBytes);
        }

        return result;
    }

    private static Dictionary<IngestionIdentity, Queue<int>> CreateEventSizeIndex(
        IReadOnlyList<DiagnosticEventEnvelope> events)
    {
        var result = new Dictionary<IngestionIdentity, Queue<int>>();
        foreach (DiagnosticEventEnvelope diagnosticEvent in events)
        {
            AddSize(
                result,
                new IngestionIdentity(
                    NormalizePostgreSqlTimestamp(diagnosticEvent.OccurredAtUtc.UtcDateTime),
                    diagnosticEvent.EventId.Value),
                diagnosticEvent.EstimatedSizeBytes);
        }

        return result;
    }

    private static void AddSize(
        IDictionary<IngestionIdentity, Queue<int>> sizes,
        IngestionIdentity identity,
        int size)
    {
        if (!sizes.TryGetValue(identity, out Queue<int>? values))
        {
            values = new Queue<int>();
            sizes.Add(identity, values);
        }

        values.Enqueue(size);
    }

    private static int TakePersistedSize(
        IReadOnlyDictionary<IngestionIdentity, Queue<int>> sizes,
        IngestionIdentity identity)
    {
        if (!sizes.TryGetValue(identity, out Queue<int>? values) || values.Count == 0)
        {
            throw new InvalidDataException("PostgreSQL returned an ingestion identity that was not staged.");
        }

        return values.Dequeue();
    }

    private static long NormalizePostgreSqlTimestamp(DateTime timestamp)
    {
        long utcTicks = timestamp.Kind == DateTimeKind.Utc
            ? timestamp.Ticks
            : DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).Ticks;
        return utcTicks - utcTicks % TimeSpan.TicksPerMicrosecond;
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertMetricReplayConsistentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<MetricSample> samples,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        var observedAts = new DateTime[samples.Count];
        var sampleIds = new Guid[samples.Count];
        var instanceIds = new Guid[samples.Count];
        var metricKeys = new string[samples.Count];
        var metricValues = new double[samples.Count];
        var dimensions = new string[samples.Count];

        for (int index = 0; index < samples.Count; index++)
        {
            MetricSample sample = samples[index];
            observedAts[index] = sample.ObservedAtUtc.UtcDateTime;
            sampleIds[index] = sample.SampleId.Value;
            instanceIds[index] = sample.InstanceId.Value;
            metricKeys[index] = sample.MetricId.Value;
            metricValues[index] = sample.Value;
            dimensions[index] = SerializeDimensions(sample.Dimensions);
        }

        await using var command = new NpgsqlCommand(ValidateMetricReplaySql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue(
            "observed_ats",
            NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
            observedAts);
        command.Parameters.AddWithValue("sample_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, sampleIds);
        command.Parameters.AddWithValue("instance_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, instanceIds);
        command.Parameters.AddWithValue("metric_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, metricKeys);
        command.Parameters.AddWithValue(
            "metric_values",
            NpgsqlDbType.Array | NpgsqlDbType.Double,
            metricValues);
        command.Parameters.AddWithValue("dimensions", NpgsqlDbType.Array | NpgsqlDbType.Jsonb, dimensions);

        await AssertReplayValidationResultAsync(command, "metric sample", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AssertEventReplayConsistentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<DiagnosticEventEnvelope> events,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        var occurredAts = new DateTime[events.Count];
        var eventIds = new Guid[events.Count];
        var instanceIds = new Guid[events.Count];
        var eventKinds = new string[events.Count];
        var protectedPayloadIds = new Guid?[events.Count];
        var collectedAts = new DateTime[events.Count];

        for (int index = 0; index < events.Count; index++)
        {
            DiagnosticEventEnvelope diagnosticEvent = events[index];
            occurredAts[index] = diagnosticEvent.OccurredAtUtc.UtcDateTime;
            eventIds[index] = diagnosticEvent.EventId.Value;
            instanceIds[index] = diagnosticEvent.InstanceId.Value;
            eventKinds[index] = diagnosticEvent.Kind.Value;
            protectedPayloadIds[index] = diagnosticEvent.ProtectedPayload?.PayloadId.Value;
            collectedAts[index] = diagnosticEvent.CollectedAtUtc.UtcDateTime;
        }

        await using var command = new NpgsqlCommand(ValidateEventReplaySql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        command.Parameters.AddWithValue(
            "occurred_ats",
            NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
            occurredAts);
        command.Parameters.AddWithValue("event_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, eventIds);
        command.Parameters.AddWithValue("instance_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, instanceIds);
        command.Parameters.AddWithValue("event_kinds", NpgsqlDbType.Array | NpgsqlDbType.Text, eventKinds);
        command.Parameters.AddWithValue(
            "protected_payload_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            protectedPayloadIds);
        command.Parameters.AddWithValue(
            "collected_ats",
            NpgsqlDbType.Array | NpgsqlDbType.TimestampTz,
            collectedAts);

        await AssertReplayValidationResultAsync(command, "diagnostic event", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task AssertReplayValidationResultAsync(
        NpgsqlCommand command,
        string recordKind,
        CancellationToken cancellationToken)
    {
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is not bool isConsistent)
        {
            throw new InvalidOperationException("PostgreSQL returned an unexpected replay-validation result.");
        }

        if (!isConsistent)
        {
            throw new InvalidDataException(
                $"The {recordKind} persistence identity already contains divergent content.");
        }
    }

    private static async Task<DateTimeOffset> ReadRepositoryClockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RepositoryClockSql, connection, transaction)
        {
            CommandTimeout = PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(timeout),
        };
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is not DateTime timestamp)
        {
            throw new InvalidOperationException("PostgreSQL repository clock returned an unexpected value.");
        }

        return new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
    }

    private static async Task RollbackWithoutMaskingAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Preserve the original failure.
        }
    }

    private sealed record IngestionIdentity(long TimestampTicks, Guid Id);
}
