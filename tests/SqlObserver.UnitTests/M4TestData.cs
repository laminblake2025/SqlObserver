using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

internal static class M4TestData
{
    internal static readonly DateTimeOffset RepositoryTime = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
    internal static readonly MonitoredInstanceId TargetId = new(Guid.Parse("8e1b51c7-f812-4321-a41e-d46a21ca7641"));
    internal static readonly ObservationTargetRevision TargetRevision = new(7);

    internal static CollectorManifest CreateManifest(
        string id = "engine.core",
        int executionOrder = 1,
        CollectorOutputKind outputKind = CollectorOutputKind.Metrics,
        CollectorFallbackPolicy? fallback = null,
        IReadOnlyList<CollectorId>? dependsOn = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? retryDelay = null,
        int failureThreshold = 3,
        int maxRows = 10_000,
        int maxResponseBytes = 33_554_432)
    {
        _ = executionOrder;
        return new CollectorManifest(
            new CollectorId(id),
            new CollectorDisplayName(id),
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
                commandTimeout ?? TimeSpan.FromSeconds(1),
                maxRows,
                maxResponseBytes,
                CollectorEstimatedCost.Low),
            fallback ?? new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(1),
            CollectorOperationalMode.Passive,
            [
                SqlServerEngineEdition.Standard,
                SqlServerEngineEdition.Enterprise,
                SqlServerEngineEdition.Express,
            ],
            dependsOn ?? [CollectorCatalogIds.CapabilityConnection],
            new CollectorResiliencePolicy(
                maximumAttempts: 2,
                transientRetryDelay: retryDelay ?? TimeSpan.Zero,
                circuitFailureThreshold: failureThreshold,
                circuitOpenDuration: TimeSpan.FromMinutes(5)),
            outputKind);
    }

    internal static CollectorOutputContract CreateOutputContract(
        CollectorOutputKind outputKind = CollectorOutputKind.Metrics,
        int maxMetrics = 10_000)
    {
        return outputKind switch
        {
            CollectorOutputKind.Metrics => new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                [new CollectorMetricOutputContract(new MetricId("engine.cpu.percent"), [])],
                maxMetrics,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 0),
            CollectorOutputKind.DatabaseInventory => new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                metrics: [],
                maxMetricSamples: 0,
                maxDatabaseObservations: 1_000,
                maxDatabaseFileObservations: 0),
            CollectorOutputKind.DatabaseFiles => new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                metrics: [],
                maxMetricSamples: 0,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 1_000),
            CollectorOutputKind.SqlVolumes => new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                metrics: [],
                maxMetricSamples: 0,
                maxDatabaseObservations: 0,
                maxDatabaseFileObservations: 0,
                maxSqlVolumeObservations: 1_000),
            _ => throw new ArgumentOutOfRangeException(nameof(outputKind)),
        };
    }

    internal static CapabilityProfile CreateProfile(
        PermissionEvidenceOutcome permissionOutcome = PermissionEvidenceOutcome.Granted,
        DateTimeOffset? validUntilUtc = null,
        SqlServerPlatform platform = SqlServerPlatform.Windows,
        SqlServerEngineEdition edition = SqlServerEngineEdition.Enterprise,
        int majorVersion = 16)
    {
        return new CapabilityProfile(
            TargetId,
            TargetRevision,
            CollectorCatalogIds.CapabilityConnection,
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(majorVersion, 0, 1000, 1),
                new SqlServerEditionName("Test Edition"),
                edition,
                platform),
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
                    permissionOutcome),
            ],
            TimeSpan.FromMilliseconds(1),
            evidenceBytes: 64,
            RepositoryTime.AddMinutes(-1),
            validUntilUtc ?? RepositoryTime.AddMinutes(5));
    }

    internal static CollectorDueWorkItem CreateWork(
        CollectorManifest manifest,
        CapabilityProfile? profile = null,
        CollectorCircuitSnapshot? circuit = null,
        DateTimeOffset? repositoryTime = null)
    {
        DateTimeOffset now = repositoryTime ?? RepositoryTime;
        return new CollectorDueWorkItem(
            TargetId,
            TargetRevision,
            CreateConnectionPolicy(),
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            new CollectorScheduleRevision(1),
            now.AddSeconds(-1),
            now,
            circuit ?? CollectorCircuitSnapshot.Closed(now),
            profile ?? CreateProfile());
    }

    internal static SqlServerConnectionPolicy CreateConnectionPolicy() => new(
        new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433),
        new SqlServerConnectTimeout(TimeSpan.FromSeconds(1)));

    internal static MetricSample CreateMetric(int ordinal = 0) => new(
        new MetricSampleId(Guid.Parse($"00000000-0000-0000-0000-{ordinal + 1:D12}")),
        TargetId,
        new MetricId("engine.cpu.percent"),
        RepositoryTime.AddTicks(ordinal * TimeSpan.TicksPerMicrosecond),
        ordinal);

    internal static CollectorExecutionResult CreateSuccessResult(
        CollectorManifest manifest,
        CollectorExecutionRequest request,
        IReadOnlyList<MetricSample>? metrics = null)
    {
        metrics ??= [CreateMetric()];
        var payload = new CollectorPayload(metrics);
        return new CollectorExecutionResult(
            request.TargetId,
            request.TargetRevision,
            manifest.Id,
            manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            payload,
            new CollectorRunAccounting(
                sourceRowsRead: metrics.Count,
                outputItemsProduced: payload.ItemCount,
                responseBytes: payload.EstimatedSizeBytes,
                outputBytes: payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
    }

    internal static CollectorExecutionRequest CreateExecutionRequest() => new(
        new CollectorRunId(Guid.Parse("a2f94cbd-2c26-4c7c-bcaa-9e801fd02c76")),
        TargetId,
        TargetRevision,
        CreateConnectionPolicy(),
        CreateProfile(),
        new CollectorAttemptNumber(1),
        new CollectorExecutionTimeout(TimeSpan.FromSeconds(1)));

    internal static CollectorRegistration CreateRegistration(
        CollectorManifest manifest,
        Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> collect,
        int order = 1,
        CollectorOutputContract? outputContract = null)
    {
        var collector = new DelegateCollector(manifest, collect);
        return new CollectorRegistration(
            order,
            collector,
            new CollectorOutputValidator(outputContract ?? CreateOutputContract(manifest.OutputKind)),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64)));
    }

    private sealed class DelegateCollector : ISqlServerCollector
    {
        private readonly Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> _collect;

        internal DelegateCollector(
            CollectorManifest manifest,
            Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> collect)
        {
            Manifest = manifest;
            _collect = collect;
        }

        public CollectorManifest Manifest { get; }

        public ValueTask<CollectorExecutionResult> CollectAsync(
            CollectorExecutionRequest request,
            CancellationToken cancellationToken) => _collect(request, cancellationToken);
    }
}
