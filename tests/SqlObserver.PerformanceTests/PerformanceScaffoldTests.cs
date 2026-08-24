using System.Diagnostics;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.PerformanceTests;

public sealed class PerformanceScaffoldTests
{
    private const int SampleCount = IngestionLimits.MaximumItemCount;
    private static readonly TimeSpan EngineBudget = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset RepositoryTime = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId TargetId = new(
        Guid.Parse("0eb701f7-0dd8-4e35-858a-d82c281077f8"));
    private static readonly ObservationTargetRevision TargetRevision = new(1);
    private static readonly MetricId EngineMetricId = new("engine.cpu.percent");

    [Fact]
    public async Task CollectionEngineValidatesMaximumBatchWithinBoundedDeadline()
    {
        MetricSample[] samples = CreateSamples();
        var payload = new CollectorPayload(samples);
        CollectorManifest manifest = CreateManifest();
        var registration = new CollectorRegistration(
            executionOrder: 1,
            new FixedPayloadCollector(manifest, payload),
            new CollectorOutputValidator(new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                [new CollectorMetricOutputContract(EngineMetricId, [])],
                maxMetricSamples: SampleCount,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 0)),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64)));
        var registry = new CollectorRegistry([registration]);
        CollectorDueWorkItem work = CreateDueWork(manifest);
        var stopwatch = Stopwatch.StartNew();

        CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(
            registry.GetRequired(CollectorCatalogIds.EngineCore),
            work,
            new CollectorRunId(Guid.Parse("2e05a937-a32e-49f1-9b95-f6ac7dc45ddb")),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(SampleCount, result.Payload.ItemCount);
        Assert.Equal(SampleCount, result.Summary.Accounting.SourceRowsRead);
        Assert.Equal(SampleCount, result.Summary.Accounting.OutputItemsProduced);
        Assert.True(
            stopwatch.Elapsed < EngineBudget,
            $"Maximum-batch validation took {stopwatch.Elapsed}; budget is {EngineBudget}.");
    }

    private static MetricSample[] CreateSamples()
    {
        var samples = new MetricSample[SampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = new MetricSample(
                new MetricSampleId(Guid.CreateVersion7()),
                TargetId,
                EngineMetricId,
                RepositoryTime.AddTicks(index * TimeSpan.TicksPerMicrosecond),
                index);
        }

        return samples;
    }

    private static CollectorManifest CreateManifest() => new(
        CollectorCatalogIds.EngineCore,
        new CollectorDisplayName("Core engine performance fixture"),
        new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds")],
        [
            new CollectorPermissionRequirement(
                new SqlServerPermissionId("server.view-performance-state"),
                PermissionEvidenceScope.Server,
                new SqlServerMajorVersionRange(15, 17)),
        ],
        new SqlServerMajorVersionRange(15, 17),
        [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)),
        new CollectorExecutionLimits(
            TimeSpan.FromSeconds(1),
            EngineBudget,
            SampleCount,
            CollectorExecutionLimits.MaximumResponseBytes,
            CollectorEstimatedCost.Low),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
        new CollectorOutputSchemaVersion(1),
        CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Enterprise],
        [CollectorCatalogIds.CapabilityConnection],
        new CollectorResiliencePolicy(
            maximumAttempts: 2,
            transientRetryDelay: TimeSpan.FromMilliseconds(100),
            circuitFailureThreshold: 3,
            circuitOpenDuration: TimeSpan.FromMinutes(5)),
        CollectorOutputKind.Metrics);

    private static CollectorDueWorkItem CreateDueWork(CollectorManifest manifest) => new(
        TargetId,
        TargetRevision,
        new SqlServerConnectionPolicy(
            new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))),
        manifest.Id,
        manifest.ManifestVersion.Value,
        manifest.OutputSchemaVersion.Value,
        new CollectorScheduleRevision(1),
        RepositoryTime.AddSeconds(-1),
        RepositoryTime,
        CollectorCircuitSnapshot.Closed(RepositoryTime),
        CreateCapabilityProfile());

    private static CapabilityProfile CreateCapabilityProfile() => new(
        TargetId,
        TargetRevision,
        CollectorCatalogIds.CapabilityConnection,
        collectorManifestVersion: 1,
        outputSchemaVersion: 1,
        new SqlServerIdentity(
            new SqlServerVersion(16, 0, 1000, 1),
            new SqlServerEditionName("Enterprise"),
            SqlServerEngineEdition.Enterprise,
            SqlServerPlatform.Windows),
        CapabilityDiscoveryOutcome.Supported,
        CapabilityDiscoveryReason.Verified,
        SqlServerAuthenticationScheme.Kerberos,
        transportEncrypted: true,
        isSysAdmin: false,
        [
            new CapabilityEvidence(
                new CapabilityId("connection.tds"),
                CapabilityAvailability.Available,
                CapabilityEvidenceReason.Verified),
        ],
        [
            new PermissionEvidence(
                new SqlServerPermissionId("server.view-performance-state"),
                PermissionEvidenceScope.Server,
                PermissionEvidenceOutcome.Granted),
        ],
        TimeSpan.FromMilliseconds(1),
        evidenceBytes: 64,
        RepositoryTime.AddMinutes(-1),
        RepositoryTime.AddMinutes(5));

    private sealed class FixedPayloadCollector : ISqlServerCollector
    {
        private readonly CollectorPayload _payload;

        internal FixedPayloadCollector(CollectorManifest manifest, CollectorPayload payload)
        {
            Manifest = manifest;
            _payload = payload;
        }

        public CollectorManifest Manifest { get; }

        public ValueTask<CollectorExecutionResult> CollectAsync(
            CollectorExecutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new CollectorExecutionResult(
                request.TargetId,
                request.TargetRevision,
                Manifest.Id,
                Manifest.ManifestVersion.Value,
                Manifest.OutputSchemaVersion.Value,
                CollectorRunOutcome.Succeeded,
                CollectorRunReason.Completed,
                _payload,
                new CollectorRunAccounting(
                    sourceRowsRead: _payload.ItemCount,
                    outputItemsProduced: _payload.ItemCount,
                    responseBytes: _payload.EstimatedSizeBytes,
                    outputBytes: _payload.EstimatedSizeBytes),
                CollectorLossEvidence.None));
        }
    }
}
