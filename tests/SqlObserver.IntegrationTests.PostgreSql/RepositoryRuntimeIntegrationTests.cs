using System.Security.Cryptography;
using System.Text;
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
public sealed class RepositoryRuntimeIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration DefaultLeaseDuration = new(TimeSpan.FromSeconds(30));

    private readonly PostgreSql18Fixture _fixture;

    public RepositoryRuntimeIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorkerLeasesAreAtomicRenewableAndFencedByRepositoryTime()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        var key = new WorkerLeaseKey($"integration:lease:{Guid.NewGuid():N}");
        var firstOwner = new WorkerExecutionId(Guid.NewGuid());
        var secondOwner = new WorkerExecutionId(Guid.NewGuid());

        Task<LeaseAcquisitionResult> firstTask = leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(key, firstOwner, DefaultLeaseDuration, DefaultTimeout),
            CancellationToken.None).AsTask();
        Task<LeaseAcquisitionResult> secondTask = leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(key, secondOwner, DefaultLeaseDuration, DefaultTimeout),
            CancellationToken.None).AsTask();
        LeaseAcquisitionResult[] results = await Task.WhenAll(firstTask, secondTask);

        LeaseAcquisitionResult acquired = Assert.Single(
            results,
            static result => result.Status == LeaseAcquisitionStatus.Acquired);
        _ = Assert.Single(results, static result => result.Status == LeaseAcquisitionStatus.Contended);
        WorkerLease lease = Assert.IsType<WorkerLease>(acquired.Lease);
        Assert.True(lease.AcquiredAtUtc <= acquired.RepositoryTimeUtc);
        Assert.True(lease.ExpiresAtUtc > acquired.RepositoryTimeUtc);

        LeaseRenewalResult renewed = await leases.RenewAsync(
            new RenewWorkerLeaseRequest(lease.Identity, DefaultLeaseDuration, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(LeaseRenewalStatus.Renewed, renewed.Status);
        Assert.True(Assert.IsType<WorkerLease>(renewed.Lease).RenewedAtUtc >= lease.RenewedAtUtc);

        WorkerExecutionId losingOwner = lease.Identity.Owner == firstOwner ? secondOwner : firstOwner;
        var losingIdentity = new WorkerLeaseIdentity(key, losingOwner, lease.Identity.FencingToken);
        Assert.Equal(
            LeaseReleaseStatus.NotOwned,
            await leases.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(losingIdentity, DefaultTimeout),
                CancellationToken.None));

        Assert.Equal(
            LeaseReleaseStatus.Released,
            await leases.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(lease.Identity, DefaultTimeout),
                CancellationToken.None));

        LeaseAcquisitionResult replacement = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(key, losingOwner, DefaultLeaseDuration, DefaultTimeout),
            CancellationToken.None);
        WorkerLease replacementLease = Assert.IsType<WorkerLease>(replacement.Lease);
        Assert.True(replacementLease.Identity.FencingToken.Value > lease.Identity.FencingToken.Value);
        Assert.Equal(
            LeaseOwnershipStatus.NotCurrent,
            await leases.AssertOwnershipAsync(
                new AssertWorkerLeaseRequest(lease.Identity, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal(
            LeaseOwnershipStatus.Current,
            await leases.AssertOwnershipAsync(
                new AssertWorkerLeaseRequest(replacementLease.Identity, DefaultTimeout),
                CancellationToken.None));
    }

    [Fact]
    public async Task PartitionCareIsIdempotentUtcBoundedAndCreatesExpectedIndexes()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        WorkerLease lease = await AcquireLeaseAsync(database, "partition-care");
        var partitions = new PostgreSqlPartitionMaintenancePort(database.DataSource);
        var rawSet = new PartitionSetName("raw_metric_sample");
        var eventSet = new PartitionSetName("diagnostic_event");
        var anchor = new DateTimeOffset(2026, 2, 18, 16, 30, 0, TimeSpan.Zero);

        PartitionCareResult daily = await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                rawSet,
                PartitionGranularity.Daily,
                anchor,
                partitionsAhead: 1,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
        PartitionCareResult dailyAgain = await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                rawSet,
                PartitionGranularity.Daily,
                anchor,
                partitionsAhead: 1,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
        PartitionCareResult monthly = await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                eventSet,
                PartitionGranularity.Monthly,
                anchor,
                partitionsAhead: 1,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(2, daily.CreatedCount);
        Assert.Equal(2, dailyAgain.ExistingCount);
        Assert.Equal("raw_metric_sample_p20260218", daily.Partitions[0].Name);
        Assert.Equal("raw_metric_sample_p20260219", daily.Partitions[1].Name);
        Assert.Equal(2, monthly.CreatedCount);
        Assert.Equal("diagnostic_event_p202602", monthly.Partitions[0].Name);
        Assert.Equal("diagnostic_event_p202603", monthly.Partitions[1].Name);
        Assert.All(daily.Partitions, static partition => Assert.Equal(TimeSpan.Zero, partition.FromInclusiveUtc.Offset));
        Assert.All(monthly.Partitions, static partition => Assert.Equal(TimeSpan.Zero, partition.FromInclusiveUtc.Offset));

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        const string indexSql = """
            SELECT indexdef
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'telemetry'
              AND tablename = 'raw_metric_sample_p20260218';
            """;
        var indexDefinitions = new List<string>();
        await using (var command = new NpgsqlCommand(indexSql, connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                indexDefinitions.Add(reader.GetString(0));
            }
        }

        Assert.Contains(indexDefinitions, static definition => definition.Contains("USING brin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexDefinitions, static definition =>
            definition.Contains("instance_id", StringComparison.OrdinalIgnoreCase) &&
            definition.Contains("observed_at", StringComparison.OrdinalIgnoreCase));

        RetentionPreview preview = await partitions.PreviewRetentionAsync(
            new RetentionPreviewRequest(
                rawSet,
                new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                maxEntries: 10,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(2, preview.Entries.Count);
        Assert.All(preview.Entries, static entry =>
        {
            Assert.False(entry.PolicyEnabled);
            Assert.Equal(RetentionPreviewReason.PolicyDisabled, entry.Reason);
            Assert.Equal(RetentionPreviewDisposition.Keep, entry.Disposition);
        });
    }

    [Fact]
    public async Task BinaryCopyIngestionAndProtectedDedupAreIdempotentAndFenceChecked()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "ingestion");

        var partitionPort = new PostgreSqlPartitionMaintenancePort(collectorDataSource);
        DateTimeOffset observedAt = new(2026, 4, 12, 10, 11, 12, TimeSpan.Zero);
        await partitionPort.EnsurePartitionsAsync(
            new PartitionCareRequest(
                new PartitionSetName("raw_metric_sample"),
                PartitionGranularity.Daily,
                observedAt,
                partitionsAhead: 0,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
        await partitionPort.EnsurePartitionsAsync(
            new PartitionCareRequest(
                new PartitionSetName("diagnostic_event"),
                PartitionGranularity.Monthly,
                observedAt,
                partitionsAhead: 0,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        var limits = new IngestionLimits(maxItems: 100, maxBatchBytes: 1_000_000, maxItemBytes: 100_000);
        var monitoredInstance = new MonitoredInstanceId(instanceId);
        MetricSample[] samples =
        [
            new(
                new MetricSampleId(Guid.NewGuid()),
                monitoredInstance,
                new MetricId("engine.batch_requests"),
                observedAt,
                42,
                [new MetricDimension("scope", "instance")]),
            new(
                new MetricSampleId(Guid.NewGuid()),
                monitoredInstance,
                new MetricId("engine.active_tasks"),
                observedAt.AddSeconds(1),
                7),
        ];
        var telemetryBatch = new TelemetryBatch(samples, limits);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        IngestionResult firstTelemetry = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(telemetryBatch, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        IngestionResult retryTelemetry = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(telemetryBatch, lease.Identity, DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(2, firstTelemetry.InsertedCount);
        Assert.Equal(0, firstTelemetry.DuplicateCount);
        Assert.Equal(0, retryTelemetry.InsertedCount);
        Assert.Equal(2, retryTelemetry.DuplicateCount);
        Assert.Equal(0, retryTelemetry.PersistedBytes);

        byte[] fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes("test-only-payload-identity"));
        var protectedPayload = new ProtectedSensitivePayload(
            SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(fingerprint),
            "AES-256-GCM",
            "integration-test-key",
            RandomNumberGenerator.GetBytes(12),
            RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(64));
        var sensitivePayloads = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        SensitivePayloadReference firstReference = await sensitivePayloads.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(monitoredInstance, protectedPayload, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        SensitivePayloadReference secondReference = await sensitivePayloads.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(monitoredInstance, protectedPayload, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(firstReference.PayloadId, secondReference.PayloadId);

        var diagnosticEvent = new DiagnosticEventEnvelope(
            new DiagnosticEventId(Guid.NewGuid()),
            monitoredInstance,
            new DiagnosticEventKind("deadlock.test"),
            observedAt,
            observedAt.AddSeconds(2),
            firstReference);
        var eventBatch = new DiagnosticEventBatch([diagnosticEvent], limits);
        IngestionResult firstEvent = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(eventBatch, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        IngestionResult retryEvent = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(eventBatch, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(1, firstEvent.InsertedCount);
        Assert.Equal(1, retryEvent.DuplicateCount);

        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        {
            await using var counts = new NpgsqlCommand(
                """
                SELECT
                    (SELECT count(*) FROM telemetry.raw_metric_sample),
                    (SELECT count(*) FROM events.diagnostic_event),
                    (SELECT count(*) FROM security.protected_diagnostic_payload);
                """,
                connection);
            await using NpgsqlDataReader reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
        }

        var leases = new PostgreSqlWorkerLeasePort(collectorDataSource);
        Assert.Equal(
            LeaseReleaseStatus.Released,
            await leases.ReleaseAsync(
                new ReleaseWorkerLeaseRequest(lease.Identity, DefaultTimeout),
                CancellationToken.None));
        LeaseAcquisitionResult replacement = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                lease.Identity.Key,
                new WorkerExecutionId(Guid.NewGuid()),
                DefaultLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        _ = Assert.IsType<WorkerLease>(replacement.Lease);

        var staleSample = new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            monitoredInstance,
            new MetricId("engine.stale_fence"),
            observedAt.AddSeconds(2),
            1);
        PostgresException staleFence = await Assert.ThrowsAsync<PostgresException>(async () =>
            await ingestion.IngestTelemetryAsync(
                new TelemetryIngestionRequest(new TelemetryBatch([staleSample], limits), lease.Identity, DefaultTimeout),
                CancellationToken.None));
        Assert.Equal("55000", staleFence.SqlState);
    }

    [Fact]
    public async Task RepositoryCallsHonorCallerCancellation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await leases.AcquireAsync(
                new AcquireWorkerLeaseRequest(
                    new WorkerLeaseKey($"cancelled:{Guid.NewGuid():N}"),
                    new WorkerExecutionId(Guid.NewGuid()),
                    DefaultLeaseDuration,
                    DefaultTimeout),
                cancellation.Token));
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

    private static async Task<WorkerLease> AcquireLeaseAsync(
        RepositoryTestDatabase database,
        string purpose)
    {
        return await AcquireLeaseAsync(database.DataSource, purpose);
    }

    private static async Task<WorkerLease> AcquireLeaseAsync(
        NpgsqlDataSource dataSource,
        string purpose)
    {
        var leases = new PostgreSqlWorkerLeasePort(dataSource);
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"integration:{purpose}:{Guid.NewGuid():N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                DefaultLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, result.Status);
        return Assert.IsType<WorkerLease>(result.Lease);
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
        command.Parameters.AddWithValue("instance_key", $"integration-{instanceId:N}");
        command.Parameters.AddWithValue("display_name", "Authorized integration target");
        await command.ExecuteNonQueryAsync();
    }
}
