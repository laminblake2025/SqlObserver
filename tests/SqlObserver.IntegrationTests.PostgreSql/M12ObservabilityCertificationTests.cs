using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Observability;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Release-only product-path proofs for bounded readiness and telemetry.</summary>
[Collection(PostgreSql18CollectionDefinition.Name)]
public sealed class M12ObservabilityCertificationTests(PostgreSql18Fixture fixture)
{
    [Fact]
    [Trait("Category", "RequiresM12ObservabilityRelease")]
    public async Task LiveReleaseRepositoryReadinessIsBoundedFailClosedAndOtelVisible()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        using SignalCapture signals = new(collectorRegistration: false);
        Npgsql.NpgsqlDataSource source = database.DataSource;
        var monitor = new PostgreSqlRepositoryReadinessMonitor(new PostgreSqlCompatibilityPort(source));
        RepositoryReadinessObservation observation = await monitor.CheckAsync(
            new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), CancellationToken.None);

        Assert.True(observation.IsReady);
        Assert.Equal(18, observation.PostgreSqlMajorVersion);
        signals.Flush();
        Assert.NotEmpty(signals.ExportedActivities);
        Activity activity = Assert.Single(signals.ExportedActivities);
        Assert.Equal("repository.readiness", activity.OperationName);
        Assert.Equal("postgresql", activity.GetTagItem("db.system"));
        Assert.Equal("compatibility", activity.GetTagItem("db.operation.name"));
        Assert.DoesNotContain(activity.Tags, static tag => tag.Key is "sqlobserver.target.id" or "db.statement" or "db.connection_string");
        Stopwatch timeoutClock = Stopwatch.StartNew();
        RepositoryReadinessObservation timedOut = await new PostgreSqlRepositoryReadinessMonitor(new HangingCompatibilityPort())
            .CheckAsync(new RepositoryCallTimeout(TimeSpan.FromMilliseconds(100)), CancellationToken.None);
        Assert.False(timedOut.IsReady);
        Assert.True(timeoutClock.Elapsed < TimeSpan.FromSeconds(2));
        using CancellationTokenSource callerCancellation = new();
        Task<RepositoryReadinessObservation> cancelled = new PostgreSqlRepositoryReadinessMonitor(new HangingCompatibilityPort())
            .CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), callerCancellation.Token).AsTask();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        await WriteResultAsync("m12-readiness-observability", nameof(LiveReleaseRepositoryReadinessIsBoundedFailClosedAndOtelVisible), new { readiness = true, otel = true, postgresMajorVersion = observation.PostgreSqlMajorVersion, activityCount = signals.ExportedActivities.Count, attributeCount = activity.Tags.Count(), bounded = true });
    }

    internal static async Task WriteResultAsync(string caseId, string testName, object facts)
    {
        string? path = Environment.GetEnvironmentVariable("SQLOBSERVER_RELEASE_OBSERVABILITY_RESULT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("M12 observability result path is invalid.");
        Directory.CreateDirectory(directory);
        string json = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, caseId, status = "passed", testName, testCount = 1, facts });
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    internal sealed class FailingCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        public ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<PostgreSqlCompatibilityResult>(new InvalidOperationException("deterministic unavailable path"));
    }

    private sealed class HangingCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        public async ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    internal sealed class SignalCapture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TracerProvider _tracerProvider;
        private readonly MeterProvider _meterProvider;
        private readonly List<Activity> _activities = [];
        private readonly List<Metric> _metrics = [];
        public List<Activity> ExportedActivities => _activities;
        public HashSet<string> MetricNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> MetricSums { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<double>> MetricValues { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<KeyValuePair<string, object?>>> MetricTags { get; } = [];
        public Dictionary<string, List<IReadOnlyList<KeyValuePair<string, object?>>>> MetricTagsByName { get; } = new(StringComparer.Ordinal);
        public int MeterMeasurements { get; private set; }
        public int MetricDataPointCount { get; private set; }

        public SignalCapture(bool collectorRegistration)
        {
            var services = new ServiceCollection();
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlObserverRepository"] = "Host=contract-testing.invalid;Database=sqlobserver;Username=contract",
            }).Build();
            var environment = new ContractEnvironment();
            var activityExporter = new CollectingActivityExporter(_activities);
            var metricExporter = new CollectingMetricExporter(_metrics, metric =>
            {
                MeterMeasurements++;
                MetricNames.Add(metric.Name);
                foreach (MetricPoint point in metric.GetMetricPoints())
                {
                    MetricDataPointCount++;
                    double value = metric.MetricType switch
                    {
                        MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
                        MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => point.GetSumDouble(),
                        MetricType.LongGauge => point.GetGaugeLastValueLong(),
                        MetricType.DoubleGauge => point.GetGaugeLastValueDouble(),
                        MetricType.Histogram => point.GetHistogramSum(),
                        _ => 0,
                    };
                    MetricSums[metric.Name] = MetricSums.GetValueOrDefault(metric.Name) + value;
                    if (!MetricValues.TryGetValue(metric.Name, out List<double>? values))
                    {
                        values = [];
                        MetricValues[metric.Name] = values;
                    }
                    values.Add(value);
                    List<KeyValuePair<string, object?>> tags = [];
                    foreach (KeyValuePair<string, object?> tag in point.Tags)
                    {
                        tags.Add(tag);
                    }
                    MetricTags.Add(tags);
                    if (!MetricTagsByName.TryGetValue(metric.Name, out List<IReadOnlyList<KeyValuePair<string, object?>>>? namedTags))
                    {
                        namedTags = [];
                        MetricTagsByName[metric.Name] = namedTags;
                    }
                    namedTags.Add(tags);
                }
            });
            if (collectorRegistration)
            {
                services.AddSqlObserverCollectorRuntime(
                    configuration,
                    environment,
                    tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(activityExporter)),
                    metrics => metrics.AddReader(new PeriodicExportingMetricReader(metricExporter, 10_000)));
            }
            else
            {
                services.AddSqlObserverServerObservability(
                    configuration,
                    environment,
                    tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(activityExporter)),
                    metrics => metrics.AddReader(new PeriodicExportingMetricReader(metricExporter, 10_000)));
            }
            _provider = services.BuildServiceProvider();
            _tracerProvider = _provider.GetRequiredService<TracerProvider>();
            _meterProvider = _provider.GetRequiredService<MeterProvider>();
        }

        public void Flush()
        {
            _tracerProvider.ForceFlush();
            _meterProvider.ForceFlush(5000);
        }

        public void Dispose()
        {
            _provider.Dispose();
        }

        private sealed class ContractEnvironment : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "ContractTesting";
            public string ApplicationName { get; set; } = "SqlObserver.IntegrationTests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        }

        private sealed class CollectingActivityExporter(List<Activity> activities) : BaseExporter<Activity>
        {
            public override ExportResult Export(in Batch<Activity> batch)
            {
                foreach (Activity activity in batch)
                {
                    activities.Add(activity);
                }
                return ExportResult.Success;
            }
        }

        private sealed class CollectingMetricExporter(List<Metric> metrics, Action<Metric> measurement) : BaseExporter<Metric>
        {
            public override ExportResult Export(in Batch<Metric> batch)
            {
                foreach (Metric metric in batch)
                {
                    metrics.Add(metric);
                }
                foreach (Metric metric in batch) measurement(metric);
                return ExportResult.Success;
            }
        }

    }
}

/// <summary>Release-only pending proof for collector telemetry without a PostgreSQL fixture.</summary>
public sealed class M12ObservabilityTelemetryCertificationTests
{
    [Fact]
    [Trait("Category", "RequiresM12ObservabilityRelease")]
    public async Task LiveReleaseCollectorTelemetryIsBoundedAndSensitiveDataFree()
    {
        using M12ObservabilityCertificationTests.SignalCapture signals = new(collectorRegistration: true);
        RepositoryReadinessObservation failed = await new PostgreSqlRepositoryReadinessMonitor(new M12ObservabilityCertificationTests.FailingCompatibilityPort())
            .CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.False(failed.IsReady);
        await M12CollectorTelemetryScenarios.RunAsync();
        signals.Flush();
        HashSet<string> expectedMetricNames = new(ObservabilityContract.RequiredMetricNames, StringComparer.Ordinal)
        {
            "sqlobserver.repository.readiness.checks",
        };
        Assert.Equal(10, signals.MetricDataPointCount);
        Assert.True(signals.MetricNames.SetEquals(expectedMetricNames));
        Assert.Equal(expectedMetricNames.Count, signals.MetricNames.Count);
        Assert.Equal(2, signals.MetricValues["sqlobserver.collector.duration"].Count);
        foreach (string metricName in expectedMetricNames.Where(static name => name != "sqlobserver.collector.duration"))
            Assert.Single(signals.MetricValues[metricName]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.repository.readiness.checks"]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.service.readiness.failures"]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.collector.retries"]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.collector.loss.items"]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.collector.lease.contention"]);
        Assert.Equal(1, signals.MetricSums["sqlobserver.collector.lease.loss"]);
        Assert.Equal(3000, signals.MetricSums["sqlobserver.collector.schedule.lag"]);
        Assert.Single(signals.MetricValues["sqlobserver.collector.schedule.lag"]);
        Assert.Equal(3000, signals.MetricValues["sqlobserver.collector.schedule.lag"][0]);
        Assert.True(signals.MetricSums["sqlobserver.service.readiness.duration"] > 0);
        Assert.True(signals.MetricSums["sqlobserver.collector.duration"] > 0);
        Assert.Equal(5, signals.ExportedActivities.Count);
        Assert.Equal(1, signals.ExportedActivities.Count(static activity => activity.OperationName == "repository.readiness"));
        Activity[] collectorActivities = signals.ExportedActivities.Where(static activity => activity.OperationName == "collector.execute").ToArray();
        Assert.Equal(4, collectorActivities.Length);
        Assert.Equal(2, collectorActivities.Count(static activity => activity.GetTagItem("sqlobserver.collector.outcome")?.ToString() == "Succeeded"));
        Assert.Equal(1, collectorActivities.Count(static activity => activity.GetTagItem("sqlobserver.collector.outcome")?.ToString() == "OutputInvalid"));
        Assert.Equal(1, collectorActivities.Count(static activity => activity.GetTagItem("sqlobserver.collector.outcome")?.ToString() == "Cancelled"));
        Assert.All(signals.ExportedActivities, activity =>
        {
            Assert.True(activity.TagObjects.Count() <= ObservabilityContract.MaximumAttributeCount);
            Assert.DoesNotContain(activity.TagObjects, static tag => tag.Key is "sqlobserver.target.id" or "db.statement" or "db.connection_string" or "enduser.id");
            Assert.All(activity.TagObjects, static tag => Assert.True(tag.Value?.ToString()?.Length <= ObservabilityContract.MaximumAttributeValueLength));
        });
        Assert.Equal(["db.operation.name", "db.system", "sqlobserver.repository.ready"], signals.ExportedActivities.Single(static activity => activity.OperationName == "repository.readiness").TagObjects.Select(static tag => tag.Key).OrderBy(static key => key));
        Assert.All(collectorActivities.Where(static activity => activity.GetTagItem("sqlobserver.collector.outcome")?.ToString() != "Cancelled"), static activity => Assert.Equal(
            ["sqlobserver.collector.attempts", "sqlobserver.collector.id", "sqlobserver.collector.manifest_version", "sqlobserver.collector.outcome"],
            activity.TagObjects.Select(static tag => tag.Key).OrderBy(static key => key)));
        Assert.Equal(["sqlobserver.collector.id", "sqlobserver.collector.manifest_version", "sqlobserver.collector.outcome"], collectorActivities.Single(static activity => activity.GetTagItem("sqlobserver.collector.outcome")?.ToString() == "Cancelled").TagObjects.Select(static tag => tag.Key).OrderBy(static key => key));
        Assert.All(collectorActivities, static activity => Assert.Equal("engine.core", activity.GetTagItem("sqlobserver.collector.id")));
        Assert.All(signals.MetricTags, tags =>
        {
            Assert.True(tags.Count <= ObservabilityContract.MaximumAttributeCount);
            Assert.DoesNotContain(tags, static tag => tag.Key is "sqlobserver.target.id" or "db.statement" or "db.connection_string" or "enduser.id");
            Assert.All(tags, static tag => Assert.True(tag.Value?.ToString()?.Length <= ObservabilityContract.MaximumAttributeValueLength));
        });
        AssertMetricTags(signals, "sqlobserver.repository.readiness.checks", ["ready", "status"], ("ready", "false"), ("status", "unavailable"));
        AssertMetricTags(signals, "sqlobserver.service.readiness.duration", ["ready", "status"], ("ready", "false"), ("status", "unavailable"));
        AssertMetricTags(signals, "sqlobserver.service.readiness.failures", ["status"], ("status", "unavailable"));
        AssertMetricTags(signals, "sqlobserver.collector.retries", ["collector.id"], ("collector.id", "engine.core"));
        AssertMetricTags(signals, "sqlobserver.collector.loss.items", ["collector.id", "loss.kind"], ("collector.id", "engine.core"), ("loss.kind", "OutputValidationFailure"));
        AssertMetricTags(signals, "sqlobserver.collector.schedule.lag", ["collector.id"], ("collector.id", "engine.core"));
        AssertMetricTags(signals, "sqlobserver.collector.lease.contention", ["collector.id"], ("collector.id", "engine.core"));
        AssertMetricTags(signals, "sqlobserver.collector.lease.loss", ["collector.id"], ("collector.id", "engine.core"));
        await M12ObservabilityCertificationTests.WriteResultAsync("m12-telemetry-observability", nameof(LiveReleaseCollectorTelemetryIsBoundedAndSensitiveDataFree), new { telemetry = true, otel = true, measurements = signals.MeterMeasurements, attributeCount = signals.ExportedActivities.Sum(static activity => activity.TagObjects.Count()), sensitiveDataFree = true });
    }

    private static void AssertMetricTags(M12ObservabilityCertificationTests.SignalCapture signals, string name, IReadOnlyList<string> keys, params (string Key, string Value)[] expected)
    {
        IReadOnlyList<KeyValuePair<string, object?>> tags = Assert.Single(signals.MetricTagsByName[name]);
        Assert.Equal(keys, tags.Select(static tag => tag.Key).OrderBy(static key => key));
        foreach ((string key, string value) in expected)
            Assert.Equal(value, tags.Single(tag => tag.Key == key).Value?.ToString());
    }
}

/// <summary>Deterministic collector paths used by the pending live telemetry proof.</summary>
internal static class M12CollectorTelemetryScenarios
{
    private static readonly DateTimeOffset RepositoryTime = new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId TargetId = new(Guid.Parse("8e1b51c7-f812-4321-a41e-d46a21ca7641"));
    private static readonly ObservationTargetRevision TargetRevision = new(7);

    internal static async Task<IReadOnlyList<CollectorWorkDisposition>> RunAsync()
    {
        CollectorManifest manifest = CreateManifest();
        CollectorRegistration success = CreateRegistration(manifest, (request, _) => ValueTask.FromResult(Success(manifest, request)));
        CollectorSchedulerCycleResult committed = await CreateScheduler(success, new ScenarioRepository(Work(manifest)), new ScenarioLease()).RunCycleAsync(CancellationToken.None);

        int attempts = 0;
        CollectorRegistration retry = CreateRegistration(manifest, (request, _) =>
        {
            attempts++;
            return ValueTask.FromResult(attempts == 1
                ? Result(manifest, request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure)
                : Success(manifest, request));
        });
        CollectorEngineResult retried = await new CollectorExecutionEngine().ExecuteAsync(retry, Work(manifest), new CollectorRunId(Guid.NewGuid()), CancellationToken.None);

        CollectorRegistration loss = CreateRegistration(manifest, (request, _) => ValueTask.FromResult(
            new CollectorExecutionResult(request.TargetId, request.TargetRevision, manifest.Id, 1, 1,
                CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
                new CollectorPayload([new MetricSample(new MetricSampleId(Guid.NewGuid()), TargetId, new MetricId("unknown.metric"), RepositoryTime, 1)]),
                new CollectorRunAccounting(1, 1, 1, 1), CollectorLossEvidence.None)));
        CollectorEngineResult lost = await new CollectorExecutionEngine().ExecuteAsync(loss, Work(manifest), new CollectorRunId(Guid.NewGuid()), CancellationToken.None);

        CollectorSchedulerCycleResult contended = await CreateScheduler(success, new ScenarioRepository(Work(manifest)), new ScenarioLease { Contended = true }).RunCycleAsync(CancellationToken.None);
        CollectorRegistration blocking = CreateRegistration(manifest, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        CollectorSchedulerCycleResult leaseLost = await CreateScheduler(blocking, new ScenarioRepository(Work(manifest)), new ScenarioLease { LoseOnRenewal = true, AcquireNearExpiry = true }).RunCycleAsync(CancellationToken.None);

        Assert.Equal(CollectorWorkDisposition.Committed, Assert.Single(committed.Dispositions));
        Assert.Equal(2, retried.Summary.AttemptCount);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, lost.Summary.Outcome);
        Assert.Equal(CollectorWorkDisposition.LeaseContended, Assert.Single(contended.Dispositions));
        Assert.Equal(CollectorWorkDisposition.LeaseLost, Assert.Single(leaseLost.Dispositions));
        return [.. committed.Dispositions, .. contended.Dispositions, .. leaseLost.Dispositions];
    }

    private static CollectorScheduler CreateScheduler(CollectorRegistration registration, ICollectorRuntimeRepositoryPort repository, IWorkerLeasePort leases) =>
        new(new CollectorRegistry([registration]), repository, leases, new CollectorExecutionEngine(),
            new WorkerExecutionId(Guid.Parse("82cacb79-e644-4804-a392-8050b91e99a7")),
            new CollectorSchedulerOptions(1, 1, new WorkerLeaseDuration(TimeSpan.FromSeconds(5)), new RepositoryCallTimeout(TimeSpan.FromSeconds(1))));

    private static CollectorManifest CreateManifest() => new(
        new CollectorId("engine.core"), new CollectorDisplayName("engine.core"), new CollectorManifestVersion(1),
        [new CapabilityId("connection.tds")],
        [new CollectorPermissionRequirement(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 17))],
        new SqlServerMajorVersionRange(15, 17), [SqlServerPlatform.Windows],
        new CollectorIntervalPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)),
        new CollectorExecutionLimits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 10_000, 33_554_432, CollectorEstimatedCost.Low),
        new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], [CollectorCatalogIds.CapabilityConnection],
        new CollectorResiliencePolicy(2, TimeSpan.Zero, 3, TimeSpan.FromMinutes(5)), CollectorOutputKind.Metrics);

    private static CollectorRegistration CreateRegistration(CollectorManifest manifest, Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> collect) =>
        new(1, new DelegateCollector(manifest, collect), new CollectorOutputValidator(new CollectorOutputContract(new CollectorOutputSchemaVersion(1), [new CollectorMetricOutputContract(new MetricId("engine.cpu.percent"), [])], 10_000, 0, 0)), new CollectorSha256Digest(new string('a', 64)), new CollectorSha256Digest(new string('b', 64)));

    private static CollectorDueWorkItem Work(CollectorManifest manifest) => new(TargetId, TargetRevision, new SqlServerConnectionPolicy(new SqlServerEndpoint(new SqlServerHostName("sql.test.example"), tcpPort: 1433), new SqlServerConnectTimeout(TimeSpan.FromSeconds(1))), manifest.Id, 1, 1, new CollectorScheduleRevision(1), RepositoryTime.AddSeconds(-1), RepositoryTime, CollectorCircuitSnapshot.Closed(RepositoryTime), Profile());

    private static CapabilityProfile Profile() => new(TargetId, TargetRevision, CollectorCatalogIds.CapabilityConnection, 1, 1,
        new SqlServerIdentity(new SqlServerVersion(16, 0, 1000, 1), new SqlServerEditionName("Test Edition"), SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows), CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos, true, false,
        [new CapabilityEvidence(new CapabilityId("connection.tds"), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)], [new PermissionEvidence(new SqlServerPermissionId("server.view-performance-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Granted)], TimeSpan.FromMilliseconds(1), 64, RepositoryTime.AddMinutes(-1), RepositoryTime.AddMinutes(5));

    private static CollectorExecutionResult Success(CollectorManifest manifest, CollectorExecutionRequest request) =>
        Result(manifest, request, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, [new MetricSample(new MetricSampleId(Guid.NewGuid()), TargetId, new MetricId("engine.cpu.percent"), RepositoryTime, 1)]);

    private static CollectorExecutionResult Result(CollectorManifest manifest, CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason, IReadOnlyList<MetricSample>? metrics = null)
    {
        metrics ??= [];
        CollectorPayload payload = new(metrics);
        return new CollectorExecutionResult(request.TargetId, request.TargetRevision, manifest.Id, 1, 1, outcome, reason, payload, new CollectorRunAccounting(metrics.Count, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
    }

    private sealed class DelegateCollector(CollectorManifest manifest, Func<CollectorExecutionRequest, CancellationToken, ValueTask<CollectorExecutionResult>> collect) : ISqlServerCollector
    {
        public CollectorManifest Manifest { get; } = manifest;
        public ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken) => collect(request, cancellationToken);
    }

    private sealed class ScenarioRepository(CollectorDueWorkItem work) : ICollectorRuntimeRepositoryPort
    {
        public ValueTask<CollectorCatalogReconcileResult> ReconcileCatalogAsync(ReconcileCollectorCatalogRequest request, CancellationToken token) => ValueTask.FromResult(new CollectorCatalogReconcileResult(request.Entries.Count, 0, 0, RepositoryTime));
        public ValueTask<CollectorDueWorkBatch> ListDueAsync(ListDueCollectorWorkRequest request, CancellationToken token) => ValueTask.FromResult(new CollectorDueWorkBatch([work], false));
        public ValueTask<CollectorClaimedWork?> ClaimDueAsync(ClaimDueCollectorWorkRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<CollectorRunStartResult> BeginRunAsync(BeginCollectorRunRequest request, CancellationToken token) => ValueTask.FromResult(new CollectorRunStartResult(CollectorRunStartStatus.Started, RepositoryTime, RepositoryTime));
        public ValueTask<CollectorRunCommitResult> CommitRunAsync(CommitCollectorRunRequest request, CancellationToken token) => ValueTask.FromResult(new CollectorRunCommitResult(CollectorRunCommitStatus.Committed, request.Payload.ItemCount, 0, 0, request.Payload.EstimatedSizeBytes, RepositoryTime));
    }

    private sealed class ScenarioLease : IWorkerLeasePort
    {
        internal bool Contended { get; init; }
        internal bool LoseOnRenewal { get; init; }
        internal bool AcquireNearExpiry { get; init; }
        public ValueTask<LeaseAcquisitionResult> AcquireAsync(AcquireWorkerLeaseRequest request, CancellationToken token)
        {
            if (Contended) return ValueTask.FromResult(LeaseAcquisitionResult.Contended(RepositoryTime));
            WorkerLeaseIdentity identity = new(request.Key, request.Owner, new FencingToken(1));
            WorkerLease lease = new(identity, RepositoryTime, RepositoryTime, RepositoryTime.AddSeconds(5));
            return ValueTask.FromResult(LeaseAcquisitionResult.Acquired(lease, AcquireNearExpiry ? RepositoryTime.AddMilliseconds(4_900) : RepositoryTime));
        }
        public ValueTask<LeaseRenewalResult> RenewAsync(RenewWorkerLeaseRequest request, CancellationToken token) =>
            ValueTask.FromResult(LoseOnRenewal ? LeaseRenewalResult.OwnershipLost(RepositoryTime.AddMilliseconds(4_950)) : LeaseRenewalResult.Renewed(new WorkerLease(request.Identity, RepositoryTime, RepositoryTime, RepositoryTime.AddSeconds(5)), RepositoryTime));
        public ValueTask<LeaseReleaseStatus> ReleaseAsync(ReleaseWorkerLeaseRequest request, CancellationToken token) => ValueTask.FromResult(LeaseReleaseStatus.Released);
        public ValueTask<LeaseOwnershipStatus> AssertOwnershipAsync(AssertWorkerLeaseRequest request, CancellationToken token) => ValueTask.FromResult(LeaseOwnershipStatus.Current);
    }
}
