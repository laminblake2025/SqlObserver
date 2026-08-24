using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M4ContractAndRegistryTests
{
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public void ManifestV2CarriesDependenciesEditionsSplitBoundsResilienceAndOutputKind()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();

        Assert.Equal(TimeSpan.FromSeconds(1), manifest.Limits.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), manifest.Limits.CommandTimeout);
        Assert.Equal(manifest.Limits.CommandTimeout, manifest.Limits.Timeout);
        Assert.Equal(
            [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express],
            manifest.SupportedEngineEditions);
        Assert.Equal(CollectorCatalogIds.CapabilityConnection, Assert.Single(manifest.DependsOn));
        Assert.Equal(2, manifest.Resilience.MaximumAttempts);
        Assert.Equal(CollectorOutputKind.Metrics, manifest.OutputKind);
    }

    [Fact]
    public void DatabaseAndFileObservationsExcludePhysicalPathsAndBoundUntrustedNames()
    {
        Type fileType = typeof(DatabaseFileObservation);
        Assert.DoesNotContain(fileType.GetProperties(), property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Physical", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new SqlServerObjectName(
            new string('\u20ac', (SqlServerObjectName.MaximumUtf8Bytes / 3) + 1)));

        var file = new DatabaseFileObservation(
            M4TestData.TargetId,
            M4TestData.TargetRevision,
            databaseId: 5,
            fileId: 1,
            new SqlServerObjectName("warehouse_data"),
            DatabaseFileType.Rows,
            DatabaseFileState.Online,
            sizeBytes: 1_048_576,
            maximumSizeBytes: 2_097_152,
            growthBytes: 65_536,
            growthPercent: 0,
            readCount: 10,
            writeCount: 5,
            bytesRead: 4096,
            bytesWritten: 2048,
            ioStallMilliseconds: 12,
            M4TestData.RepositoryTime);

        Assert.Equal("warehouse_data", file.LogicalName.Value);
        Assert.True(file.EstimatedSizeBytes > DatabaseFileObservation.FixedEstimatedBytes);
    }

    [Fact]
    public void RegistryEnforcesMandatoryOrderDependenciesAndOutputValidators()
    {
        CollectorManifest core = M4TestData.CreateManifest();
        CollectorManifest databases = M4TestData.CreateManifest(
            "database.inventory",
            executionOrder: 2,
            outputKind: CollectorOutputKind.DatabaseInventory,
            dependsOn: [CollectorCatalogIds.CapabilityConnection, CollectorCatalogIds.EngineCore]);
        CollectorManifest files = M4TestData.CreateManifest(
            "database.files",
            executionOrder: 3,
            outputKind: CollectorOutputKind.DatabaseFiles,
            dependsOn:
            [
                CollectorCatalogIds.CapabilityConnection,
                CollectorCatalogIds.EngineCore,
                CollectorCatalogIds.DatabaseInventory,
            ]);
        var registry = new CollectorRegistry(
        [
            M4TestData.CreateRegistration(files, NoExecution, order: 3),
            M4TestData.CreateRegistration(core, NoExecution, order: 1),
            M4TestData.CreateRegistration(databases, NoExecution, order: 2),
        ]);

        Assert.Equal(
            ["engine.core", "database.inventory", "database.files"],
            registry.Registrations.Select(registration => registration.Manifest.Id.Value));
        Assert.Throws<ArgumentException>(() => new CollectorRegistration(
            2,
            new NeverCollector(core),
            new CollectorOutputValidator(M4TestData.CreateOutputContract(CollectorOutputKind.DatabaseFiles)),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64))));
        Assert.Throws<ArgumentException>(() => new CollectorRegistration(
            2,
            new NeverCollector(databases),
            new CollectorOutputValidator(new CollectorOutputContract(
                new CollectorOutputSchemaVersion(1),
                [new CollectorMetricOutputContract(new MetricId("unexpected.metric"), [])],
                maxMetricSamples: 1,
                maxDatabaseObservations: 1,
                maxDatabaseFileObservations: 0)),
            new CollectorSha256Digest(new string('a', 64)),
            new CollectorSha256Digest(new string('b', 64))));
        Assert.Throws<ArgumentException>(() => new CollectorRegistry(
        [
            M4TestData.CreateRegistration(core, NoExecution, order: 2),
        ]));
    }

    [Fact]
    public void RegistryRejectsFallbackCyclesAndUnknownDependencies()
    {
        CollectorManifest first = M4TestData.CreateManifest(
            "custom.first",
            executionOrder: 10,
            fallback: new CollectorFallbackPolicy(
                CollectorFallbackMode.AlternateCollector,
                new CollectorId("custom.second")));
        CollectorManifest second = M4TestData.CreateManifest(
            "custom.second",
            executionOrder: 11,
            fallback: new CollectorFallbackPolicy(
                CollectorFallbackMode.AlternateCollector,
                new CollectorId("custom.first")));

        Assert.Throws<ArgumentException>(() => new CollectorRegistry(
        [
            M4TestData.CreateRegistration(first, NoExecution, order: 10),
            M4TestData.CreateRegistration(second, NoExecution, order: 11),
        ]));

        CollectorManifest unknownDependency = M4TestData.CreateManifest(
            "custom.third",
            executionOrder: 12,
            dependsOn: [new CollectorId("custom.missing")]);
        Assert.Throws<ArgumentException>(() => new CollectorRegistry(
        [
            M4TestData.CreateRegistration(unknownDependency, NoExecution, order: 12),
        ]));
    }

    [Fact]
    public void OutputValidatorRejectsDuplicateMetricSeriesBeforeRepositoryIo()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        MetricSample first = M4TestData.CreateMetric(0);
        MetricSample second = M4TestData.CreateMetric(1);
        CollectorExecutionResult result = M4TestData.CreateSuccessResult(manifest, request, [first, second]);
        var validator = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1),
            [
                new CollectorMetricOutputContract(new MetricId("engine.cpu.percent"), []),
                new CollectorMetricOutputContract(new MetricId("engine.user_connections"), []),
            ],
            maxMetricSamples: 2,
            maxDatabaseObservations: 0,
            maxDatabaseFileObservations: 0));

        Assert.Throws<InvalidDataException>(() => validator.Validate(manifest, request, result));
    }

    [Fact]
    public void OutputValidatorRejectsIncompleteFixedMetricSetBeforeRepositoryIo()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        CollectorExecutionResult result = M4TestData.CreateSuccessResult(
            manifest,
            request,
            [M4TestData.CreateMetric()]);
        var validator = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1),
            [
                new CollectorMetricOutputContract(new MetricId("engine.cpu.percent"), []),
                new CollectorMetricOutputContract(new MetricId("engine.user_connections"), []),
            ],
            maxMetricSamples: 2,
            maxDatabaseObservations: 0,
            maxDatabaseFileObservations: 0));

        Assert.Throws<InvalidDataException>(() => validator.Validate(manifest, request, result));
    }

    [Fact]
    public void EligibilityUsesRepositoryTimeAndAllManifestEvidence()
    {
        CollectorManifest manifest = M4TestData.CreateManifest();

        Assert.Equal(
            CollectorEligibilityStatus.Eligible,
            CollectorEligibilityEvaluator.Evaluate(manifest, M4TestData.CreateWork(manifest)).Status);
        Assert.Equal(
            CollectorEligibilityStatus.PermissionDenied,
            CollectorEligibilityEvaluator.Evaluate(
                manifest,
                M4TestData.CreateWork(
                    manifest,
                    M4TestData.CreateProfile(PermissionEvidenceOutcome.Denied))).Status);
        Assert.Equal(
            CollectorEligibilityStatus.CapabilityProfileStale,
            CollectorEligibilityEvaluator.Evaluate(
                manifest,
                M4TestData.CreateWork(
                    manifest,
                    M4TestData.CreateProfile(validUntilUtc: M4TestData.RepositoryTime))).Status);
        Assert.Equal(
            CollectorEligibilityStatus.PlatformUnsupported,
            CollectorEligibilityEvaluator.Evaluate(
                manifest,
                M4TestData.CreateWork(
                    manifest,
                    M4TestData.CreateProfile(platform: SqlServerPlatform.Linux))).Status);
    }

    [Fact]
    public async Task HealthQueryAuthorizesExactRoleScopeBeforeRepositoryIo()
    {
        var repository = new FakeHealthRepository();
        var service = new HealthProjectionQueryService(repository);
        AuthorizationContext allowed = CreateAuthorization(
            [new RoleAuthorizationGrant(
                ApplicationRole.Viewer,
                TargetAuthorizationScope.ForTargets([M4TestData.TargetId]))]);

        InstanceHealthProjection? result = await service.GetInstanceAsync(
            new GetInstanceHealthQuery(allowed, M4TestData.TargetId, RepositoryTimeout),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, repository.GetCalls);

        AuthorizationContext unionEscalation = CreateAuthorization(
        [
            new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.None()),
            new RoleAuthorizationGrant(
                ApplicationRole.SecurityAdministrator,
                TargetAuthorizationScope.ForTargets([M4TestData.TargetId])),
        ]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.GetInstanceAsync(
                new GetInstanceHealthQuery(unionEscalation, M4TestData.TargetId, RepositoryTimeout),
                CancellationToken.None));
        Assert.Equal(1, repository.GetCalls);
    }

    private static ValueTask<CollectorExecutionResult> NoExecution(
        CollectorExecutionRequest request,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<CollectorExecutionResult>(new InvalidOperationException("Not executed."));

    private static AuthorizationContext CreateAuthorization(IReadOnlyList<RoleAuthorizationGrant> grants) =>
        new(
            new ActorSecurityIdentifier("S-1-5-21-444"),
            AuthorizationPrincipalState.Active,
            grants);

    private sealed class NeverCollector : ISqlServerCollector
    {
        internal NeverCollector(CollectorManifest manifest) => Manifest = manifest;
        public CollectorManifest Manifest { get; }

        public ValueTask<CollectorExecutionResult> CollectAsync(
            CollectorExecutionRequest request,
            CancellationToken cancellationToken) => NoExecution(request, cancellationToken);
    }

    private sealed class FakeHealthRepository : IHealthProjectionRepositoryPort
    {
        internal int GetCalls { get; private set; }

        public ValueTask<InstanceHealthProjection?> GetInstanceHealthAsync(
            GetInstanceHealthRepositoryRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult<InstanceHealthProjection?>(null);
        }

        public ValueTask<DatabaseHealthPage?> ListDatabaseHealthAsync(
            ListDatabaseHealthRepositoryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DatabaseHealthPage?>(new DatabaseHealthPage(
                request.TargetId,
                snapshotRunId: null,
                snapshotTargetRevision: null,
                CreatePendingCollector(request.TargetId, "database.inventory"),
                [],
                nextCursor: null,
                M4TestData.RepositoryTime));

        public ValueTask<DatabaseFileHealthPage?> ListDatabaseFileHealthAsync(
            ListDatabaseFileHealthRepositoryRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<DatabaseFileHealthPage?>(new DatabaseFileHealthPage(
                request.TargetId,
                snapshotRunId: null,
                snapshotTargetRevision: null,
                CreatePendingCollector(request.TargetId, "database.files"),
                [],
                nextCursor: null,
                M4TestData.RepositoryTime));

        private static CollectorHealthProjection CreatePendingCollector(
            MonitoredInstanceId targetId,
            string collectorId) =>
            new(
                targetId,
                new CollectorId(collectorId),
                collectorManifestVersion: 1,
                outputSchemaVersion: 1,
                CollectorHealthState.Pending,
                CollectorHealthReason.NeverCollected,
                CollectorCircuitSnapshot.Closed(M4TestData.RepositoryTime),
                latestRun: null,
                insertedCount: 0,
                duplicateCount: 0,
                rejectedCount: 0,
                persistedBytes: 0,
                M4TestData.RepositoryTime,
                lastAttemptAtUtc: null,
                lastSuccessAtUtc: null,
                M4TestData.RepositoryTime,
                M4TestData.RepositoryTime);
    }
}
