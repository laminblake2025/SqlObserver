using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class RepositoryReplaySafetyIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration DefaultLeaseDuration = new(TimeSpan.FromMinutes(2));

    private readonly PostgreSql18Fixture _fixture;

    public RepositoryReplaySafetyIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetricReplayDeduplicatesExactContentAndDivergenceRollsBackWholeBatch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "metric-replay");
        DateTimeOffset observedAt = new(2026, 10, 10, 10, 10, 10, TimeSpan.Zero);
        await EnsurePartitionAsync(
            collectorDataSource,
            lease,
            new PartitionSetName("raw_metric_sample"),
            PartitionGranularity.Daily,
            observedAt);

        var limits = new IngestionLimits(10, 100_000, 20_000);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        var sampleId = new MetricSampleId(Guid.NewGuid());
        var instance = new MonitoredInstanceId(instanceId);
        MetricSample first = CreateMetricSample(sampleId, instance, observedAt, 1, "primary");

        IngestionResult inserted = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(new TelemetryBatch([first], limits), lease.Identity, DefaultTimeout),
            CancellationToken.None);
        MetricSample exactReplay = CreateMetricSample(sampleId, instance, observedAt, 1, "primary");
        IngestionResult replayed = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(
                new TelemetryBatch([exactReplay], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(1, inserted.InsertedCount);
        Assert.Equal(1, replayed.DuplicateCount);
        Assert.Equal(0, replayed.PersistedBytes);

        MetricSample divergent = CreateMetricSample(sampleId, instance, observedAt, 99, "conflicting");
        var unrelatedId = new MetricSampleId(Guid.NewGuid());
        MetricSample otherwiseInsertable = CreateMetricSample(
            unrelatedId,
            instance,
            observedAt.AddTicks(TimeSpan.TicksPerMicrosecond),
            2,
            "new");

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestTelemetryAsync(
                new TelemetryIngestionRequest(
                    new TelemetryBatch([otherwiseInsertable, divergent], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));

        Assert.Contains("divergent content", conflict.Message, StringComparison.Ordinal);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (WHERE sample_id = @unrelated_id),
                max(metric_value) FILTER (WHERE sample_id = @first_id)
            FROM telemetry.raw_metric_sample;
            """,
            connection);
        verify.Parameters.AddWithValue("unrelated_id", unrelatedId.Value);
        verify.Parameters.AddWithValue("first_id", sampleId.Value);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1D, reader.GetDouble(2));
    }

    [Fact]
    public async Task EventReplayDeduplicatesExactContentAndDivergenceRollsBackWholeBatch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "event-replay");
        DateTimeOffset occurredAt = new(2026, 11, 11, 11, 11, 11, TimeSpan.Zero);
        await EnsurePartitionAsync(
            collectorDataSource,
            lease,
            new PartitionSetName("diagnostic_event"),
            PartitionGranularity.Monthly,
            occurredAt);

        var limits = new IngestionLimits(10, 100_000, 20_000);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        var eventId = new DiagnosticEventId(Guid.NewGuid());
        var instance = new MonitoredInstanceId(instanceId);
        DateTimeOffset collectedAt = occurredAt.AddSeconds(1);
        DiagnosticEventEnvelope first = CreateEvent(eventId, instance, occurredAt, collectedAt, "deadlock.test");

        IngestionResult inserted = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(
                new DiagnosticEventBatch([first], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
        DiagnosticEventEnvelope exactReplay = CreateEvent(
            eventId,
            instance,
            occurredAt,
            collectedAt,
            "deadlock.test");
        IngestionResult replayed = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(
                new DiagnosticEventBatch([exactReplay], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(1, inserted.InsertedCount);
        Assert.Equal(1, replayed.DuplicateCount);

        DiagnosticEventEnvelope divergent = CreateEvent(
            eventId,
            instance,
            occurredAt,
            collectedAt,
            "deadlock.conflicting");
        var unrelatedId = new DiagnosticEventId(Guid.NewGuid());
        DiagnosticEventEnvelope otherwiseInsertable = CreateEvent(
            unrelatedId,
            instance,
            occurredAt.AddTicks(TimeSpan.TicksPerMicrosecond),
            collectedAt,
            "deadlock.new");

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([otherwiseInsertable, divergent], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));

        Assert.Contains("divergent content", conflict.Message, StringComparison.Ordinal);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (WHERE event_id = @unrelated_id),
                max(event_kind) FILTER (WHERE event_id = @first_id)
            FROM events.diagnostic_event;
            """,
            connection);
        verify.Parameters.AddWithValue("unrelated_id", unrelatedId.Value);
        verify.Parameters.AddWithValue("first_id", eventId.Value);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal("deadlock.test", reader.GetString(2));
        await reader.DisposeAsync();

        await using (var changeSeverity = new NpgsqlCommand(
            """
            UPDATE events.diagnostic_event
            SET severity = 1
            WHERE occurred_at = @occurred_at
              AND event_id = @event_id;
            """,
            connection))
        {
            changeSeverity.Parameters.AddWithValue("occurred_at", occurredAt);
            changeSeverity.Parameters.AddWithValue("event_id", eventId.Value);
            Assert.Equal(1, await changeSeverity.ExecuteNonQueryAsync());
        }

        InvalidDataException severityConflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([exactReplay], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));
        Assert.Contains("divergent content", severityConflict.Message, StringComparison.Ordinal);

        await using (var changeMetadata = new NpgsqlCommand(
            """
            UPDATE events.diagnostic_event
            SET severity = 0,
                safe_metadata = '{"source":"legacy"}'::jsonb
            WHERE occurred_at = @occurred_at
              AND event_id = @event_id;
            """,
            connection))
        {
            changeMetadata.Parameters.AddWithValue("occurred_at", occurredAt);
            changeMetadata.Parameters.AddWithValue("event_id", eventId.Value);
            Assert.Equal(1, await changeMetadata.ExecuteNonQueryAsync());
        }

        InvalidDataException metadataConflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([exactReplay], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));
        Assert.Contains("divergent content", metadataConflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveFingerprintRetryIsImmutableFirstWriteWins()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "payload-retry");
        var port = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        byte[] fingerprint = Enumerable.Repeat((byte)41, SensitivePayloadFingerprint.RequiredLength).ToArray();
        ProtectedSensitivePayload first = CreateProtectedPayload(fingerprint, material: 11);
        ProtectedSensitivePayload randomizedRetry = CreateProtectedPayload(fingerprint, material: 22);

        SensitivePayloadReference inserted = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(first, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        SensitivePayloadReference exactReplay = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(first, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        SensitivePayloadReference randomized = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(randomizedRetry, lease.Identity, DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(inserted.PayloadId, exactReplay.PayloadId);
        Assert.Equal(inserted.PayloadId, randomized.PayloadId);
        PersistedPayload persisted = await ReadPersistedPayloadAsync(database, fingerprint);
        Assert.Equal(1L, persisted.Count);
        Assert.Equal(first.ProtectionAlgorithm, persisted.Algorithm);
        Assert.Equal(first.KeyIdentifier, persisted.KeyIdentifier);
        Assert.Equal(first.GetNonce(), persisted.Nonce);
        Assert.Equal(first.GetAuthenticationTag(), persisted.AuthenticationTag);
        Assert.Equal(first.GetCiphertext(), persisted.Ciphertext);
    }

    [Fact]
    public async Task ConcurrentSensitiveFingerprintCollisionCommitsOneCompleteRepresentation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease firstLease = await AcquireLeaseAsync(collectorDataSource, "payload-concurrency-a");
        WorkerLease secondLease = await AcquireLeaseAsync(collectorDataSource, "payload-concurrency-b");
        var port = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        byte[] fingerprint = Enumerable.Repeat((byte)73, SensitivePayloadFingerprint.RequiredLength).ToArray();
        ProtectedSensitivePayload first = CreateProtectedPayload(fingerprint, material: 31);
        ProtectedSensitivePayload second = CreateProtectedPayload(fingerprint, material: 47);

        Task<SensitivePayloadReference> firstWrite = port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(first, firstLease.Identity, DefaultTimeout),
            CancellationToken.None).AsTask();
        Task<SensitivePayloadReference> secondWrite = port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(second, secondLease.Identity, DefaultTimeout),
            CancellationToken.None).AsTask();
        SensitivePayloadReference[] references = await Task.WhenAll(firstWrite, secondWrite);

        Assert.Equal(references[0].PayloadId, references[1].PayloadId);
        PersistedPayload persisted = await ReadPersistedPayloadAsync(database, fingerprint);
        bool matchesFirst = PersistedPayloadMatches(first, persisted);
        bool matchesSecond = PersistedPayloadMatches(second, persisted);

        Assert.Equal(1L, persisted.Count);
        Assert.True(matchesFirst ^ matchesSecond);
    }

    [Fact]
    public async Task SqlConstraintsRejectInvalidNonceTagAndCiphertextSize()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();

        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_nonce",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes - 1],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            ciphertext: [1]);
        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_authentication_tag",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes - 1],
            ciphertext: [1]);
        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_ciphertext",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            ciphertext: new byte[ProtectedSensitivePayload.MaximumCiphertextBytes + 1]);

        Assert.Equal(0L, await ExecuteScalarInt64Async(
            database,
            "SELECT count(*) FROM security.protected_diagnostic_payload;"));
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static MetricSample CreateMetricSample(
        MetricSampleId sampleId,
        MonitoredInstanceId instance,
        DateTimeOffset observedAtUtc,
        double value,
        string scope) =>
        new(
            sampleId,
            instance,
            new MetricId("engine.replay_safety"),
            observedAtUtc,
            value,
            [new MetricDimension("scope", scope)]);

    private static DiagnosticEventEnvelope CreateEvent(
        DiagnosticEventId eventId,
        MonitoredInstanceId instance,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset collectedAtUtc,
        string kind) =>
        new(
            eventId,
            instance,
            new DiagnosticEventKind(kind),
            occurredAtUtc,
            collectedAtUtc);

    private static ProtectedSensitivePayload CreateProtectedPayload(byte[] fingerprint, byte material) =>
        new(
            SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(fingerprint),
            $"AES-256-GCM-{material}",
            $"integration-key-{material}",
            Enumerable.Repeat(material, 12).ToArray(),
            Enumerable.Repeat(checked((byte)(material + 1)), 16).ToArray(),
            Enumerable.Repeat(checked((byte)(material + 2)), 64).ToArray());

    private static async Task<WorkerLease> AcquireLeaseAsync(NpgsqlDataSource dataSource, string purpose)
    {
        var leases = new PostgreSqlWorkerLeasePort(dataSource);
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"integration:{purpose}:{Guid.NewGuid():N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                DefaultLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease);
    }

    private static async Task EnsurePartitionAsync(
        NpgsqlDataSource dataSource,
        WorkerLease lease,
        PartitionSetName setName,
        PartitionGranularity granularity,
        DateTimeOffset anchorUtc)
    {
        var partitions = new PostgreSqlPartitionMaintenancePort(dataSource);
        await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                setName,
                granularity,
                anchorUtc,
                partitionsAhead: 0,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
    }

    private static async Task InsertObservationTargetAsync(
        RepositoryTestDatabase database,
        Guid instanceId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH repository_clock AS
            (
                SELECT clock_timestamp() AS captured_at
            )
            INSERT INTO control.observation_target
            (
                instance_id, instance_key, display_name,
                created_at, updated_at, discovery_requested_at
            )
            SELECT
                @instance_id, @instance_key, @display_name,
                captured_at, captured_at, captured_at
            FROM repository_clock;
            """,
            connection);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("instance_key", $"replay-{instanceId:N}");
        command.Parameters.AddWithValue("display_name", "Replay safety integration target");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PersistedPayload> ReadPersistedPayloadAsync(
        RepositoryTestDatabase database,
        byte[] fingerprint)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*) OVER (), protection_algorithm, key_identifier,
                nonce, authentication_tag, ciphertext
            FROM security.protected_diagnostic_payload
            WHERE payload_kind = 'query_text'
              AND fingerprint = @fingerprint;
            """,
            connection);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        return new PersistedPayload(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetFieldValue<byte[]>(5));
    }

    private static bool PersistedPayloadMatches(
        ProtectedSensitivePayload expected,
        PersistedPayload actual) =>
        expected.ProtectionAlgorithm == actual.Algorithm &&
        expected.KeyIdentifier == actual.KeyIdentifier &&
        expected.GetNonce().SequenceEqual(actual.Nonce) &&
        expected.GetAuthenticationTag().SequenceEqual(actual.AuthenticationTag) &&
        expected.GetCiphertext().SequenceEqual(actual.Ciphertext);

    private static async Task AssertProtectedPayloadConstraintAsync(
        RepositoryTestDatabase database,
        string expectedConstraint,
        byte[] nonce,
        byte[] authenticationTag,
        byte[] ciphertext)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO security.protected_diagnostic_payload
            (
                payload_id, payload_kind, fingerprint, protection_algorithm,
                key_identifier, nonce, authentication_tag, ciphertext
            )
            VALUES
            (
                @payload_id, 'query_text', @fingerprint, 'AES-256-GCM',
                'integration-key', @nonce, @authentication_tag, @ciphertext
            );
            """,
            connection);
        command.Parameters.AddWithValue("payload_id", Guid.NewGuid());
        command.Parameters.AddWithValue("fingerprint", RandomNumberGenerator.GetBytes(32));
        command.Parameters.AddWithValue("nonce", nonce);
        command.Parameters.AddWithValue("authentication_tag", authenticationTag);
        command.Parameters.AddWithValue("ciphertext", ciphertext);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task<long> ExecuteScalarInt64Async(
        RepositoryTestDatabase database,
        string commandText)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PersistedPayload(
        long Count,
        string Algorithm,
        string KeyIdentifier,
        byte[] Nonce,
        byte[] AuthenticationTag,
        byte[] Ciphertext);
}
