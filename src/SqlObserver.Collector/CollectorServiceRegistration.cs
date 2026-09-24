using Microsoft.Extensions.Hosting;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Analytics;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Security;
using SqlObserver.Reporting;
using SqlObserver.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace SqlObserver.Collector;

/// <summary>Defines the collector host's least-surface composition for runtime and test verification.</summary>
public static class CollectorServiceRegistration
{
    public static IServiceCollection AddSqlObserverCollectorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
        => AddSqlObserverCollectorRuntimeCore(services, configuration, environment, null, null);

    /// <summary>Composes the production collector registration with test-only in-process exporters.</summary>
    public static IServiceCollection AddSqlObserverCollectorRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<TracerProviderBuilder> configureTracing,
        Action<MeterProviderBuilder> configureMetrics) =>
        AddSqlObserverCollectorRuntimeCore(services, configuration, environment, configureTracing, configureMetrics);

    private static IServiceCollection AddSqlObserverCollectorRuntimeCore(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment,
        Action<TracerProviderBuilder>? configureTracing,
        Action<MeterProviderBuilder>? configureMetrics)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        string repositoryConfiguration = configuration.GetConnectionString("SqlObserverRepository") ??
            throw new InvalidOperationException("The SqlObserver repository is not configured.");

        services.AddSingleton(_ => PostgreSqlCollectorDataPlane.Create(
            repositoryConfiguration,
            "SqlObserver.Collector",
            _.GetRequiredService<IdentityFingerprintKey>()));
        if (configureTracing is null && configureMetrics is null)
        {
            services.AddSqlObserverObservability(configuration, ObservabilityContract.CollectorServiceName, environment);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(environment);
            services.AddSqlObserverObservabilityForContractTesting(
                configuration,
                ObservabilityContract.CollectorServiceName,
                environment,
                configureTracing ?? (_ => { }),
                configureMetrics ?? (_ => { }));
        }
        services.AddSingleton<IPostgreSqlCompatibilityPort>(static provider => provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Compatibility);
        services.AddSingleton<IRepositoryReadinessMonitor, PostgreSqlRepositoryReadinessMonitor>();
        services.AddSingleton<IWorkerLeasePort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().WorkerLeases);
        services.AddSingleton<PostgreSqlPartitionMaintenancePort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().PartitionMaintenance);
        services.AddSingleton<ICapabilityProfileRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().CapabilityProfiles);
        services.AddSingleton<ICollectorRuntimeRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Runtime);
        services.AddSingleton<IAlertRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Alerts);
        services.AddSingleton<IAnalyticsRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Analytics);
        services.AddSingleton<IAnalyticsDerivationStore>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().AnalyticsDerivation);
        services.AddSingleton<IAnalyticsBackfillStore>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().AnalyticsBackfill);
        services.AddSingleton<IReportExpiryRepository>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Reports);
        services.AddSingleton<IAlertEvaluationSource, PostgreSqlAlertEvaluationSource>();
        services.AddSingleton<IAlertDestinationConfigurationResolver>(_ => new ConfigurationAlertDestinationResolver(key => configuration[key]));
        services.AddSingleton<IAlertDnsResolver, SystemAlertDnsResolver>();
        services.AddSingleton<IAlertDestinationHttpClientFactory, AddressBoundAlertDestinationHttpClientFactory>();
        services.AddSingleton(static provider => new HttpClient(AddressBoundConnectHandler.Create(provider.GetRequiredService<IAlertDnsResolver>(), AlertNetworkPolicy.IsApprovedAddress)));
        services.AddSingleton<HttpsWebhookAlertDestination>();
        services.AddSingleton<WindowsEventLogAlertDestination>();
        services.AddSingleton<IAlertDestinationPort, AlertDestinationDispatcher>();
        services.AddSingleton<IReplicationDistributionBindingResolver>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().ReplicationDistributionBindings);
        services.AddSingleton<ISqlServerCapabilityDiscoveryPort>(static provider =>
            new SqlServerCapabilityDiscoveryPort(provider.GetRequiredService<IReplicationDistributionBindingResolver>()));
        services.AddSingleton<ICapabilityDiscoveryService, CapabilityDiscoveryService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IQuerySensitiveContentProtector, UnavailableQuerySensitiveContentProtector>();
        services.AddSingleton(static _ => new WorkerExecutionId(Guid.NewGuid()));
        services.AddSingleton(static _ => SqlServerCollectorAssetCatalog.LoadEmbedded());
        services.AddSingleton(static provider => new SqlServerCoreEngineCollector(
            provider.GetRequiredService<SqlServerCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerDatabaseInventoryCollector(
            provider.GetRequiredService<SqlServerCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerDatabaseFilesCollector(
            provider.GetRequiredService<SqlServerCollectorAssetCatalog>()));
        services.AddSingleton(static _ => SqlServerActivityCollectorAssetCatalog.LoadEmbedded());
        services.AddSingleton(static _ => SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded());
        services.AddSingleton(static _ => SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded());
        services.AddSingleton(static _ => SqlServerOperationalHealthAssetCatalog.LoadEmbedded());
        services.AddSingleton<SqlServerBackupsStatusCollector>();
        services.AddSingleton<SqlServerSqlAgentFailuresCollector>();
        services.AddSingleton<SqlServerTempDbHealthCollector>();
        services.AddSingleton<SqlServerAvailabilityGroupsHealthCollector>();
        services.AddSingleton(static provider => new SqlServerActivitySessionsCollector(
            provider.GetRequiredService<SqlServerActivityCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerActivityRequestsCollector(
            provider.GetRequiredService<SqlServerActivityCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerServerWaitsCollector(
            provider.GetRequiredService<SqlServerActivityCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerCurrentBlockingCollector(
            provider.GetRequiredService<SqlServerActivityCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerDeadlockCollector(
            provider.GetRequiredService<SqlServerDeadlockCollectorAssetCatalog>()));
        services.AddSingleton(static provider => new SqlServerQueryPerformanceCollector(
            provider.GetRequiredService<SqlServerQueryPerformanceCollectorAssetCatalog>()));
        services.AddSingleton(static _ => SqlServerReplicationAssetCatalog.LoadEmbedded());
        services.AddSingleton(static provider => new SqlServerReplicationCollector(
            provider.GetRequiredService<SqlServerReplicationAssetCatalog>(),
            provider.GetRequiredService<IReplicationDistributionBindingResolver>(),
            provider.GetRequiredService<IdentityFingerprintKey>()));
        services.AddSingleton(static _ => HostMetricsAssetCatalog.LoadEmbedded());
        // Identity HMAC material is supplied by the secret-backed collector
        // configuration. There is deliberately no process-local fallback:
        // without the configured key the collector cannot safely establish
        // stable host identity and startup fails closed with the provider's
        // actionable configuration error.
        services.AddSingleton<IIdentityFingerprintKeyProvider>(_ =>
            new ConfigurationIdentityFingerprintKeyProvider(() => configuration["SqlObserver:IdentityFingerprintKey"]));
        services.AddSingleton<IdentityFingerprintKey>(provider =>
            provider.GetRequiredService<IIdentityFingerprintKeyProvider>().GetRequiredKey());
        // Host collection runs through a real, fixed CIM/performance adapter.
        // It inherits this Windows service identity and returns Unsupported on
        // non-Windows hosts.  Entries are explicit persisted bindings; an
        // absent entry never falls back to a SQL endpoint or synthetic host.
        services.AddSingleton<IWindowsHostDataReader, WindowsIntegratedHostDataReader>();
        // Host identity/profile is resolved from control.host_binding and
        // control.host_profile in PostgreSQL.  Configuration is not an
        // authority and therefore cannot silently supply a default host.
        services.AddSingleton<IHostTargetStore>(static provider =>
            new PostgreSqlHostTargetStore(provider.GetRequiredService<PostgreSqlCollectorDataPlane>()));
        // Always compose the terminating operation boundary so every provider
        // call is owned by a killable session.
        services.AddSingleton<IWindowsHostQuerySessionFactory>(static provider =>
            new TerminatingWindowsHostQuerySessionFactory(provider.GetRequiredService<IWindowsHostDataReader>()));
        services.AddSingleton<WindowsHostMetricSource>(static provider =>
            new WindowsHostMetricSource(provider.GetRequiredService<IWindowsHostQuerySessionFactory>(), provider.GetRequiredService<IdentityFingerprintKey>()));
        services.AddSingleton<IHostMetricsCollector, HostMetricsCollector>();
        services.AddSingleton<IHostTargetResolver, PersistedHostTargetResolver>();
        services.AddSingleton<HostMetricsCollectorAdapter>();
        services.AddSingleton(static provider => CreateRegistry(provider));
        services.AddSingleton<CollectorExecutionEngine>();
        services.AddSingleton<IDeadlockActivitySnapshotTrigger, DeadlockActivitySnapshotTrigger>();
        services.AddSingleton(provider => new CollectorScheduler(
            provider.GetRequiredService<CollectorRegistry>(),
            provider.GetRequiredService<ICollectorRuntimeRepositoryPort>(),
            provider.GetRequiredService<IWorkerLeasePort>(),
            provider.GetRequiredService<CollectorExecutionEngine>(),
            provider.GetRequiredService<WorkerExecutionId>(),
            new CollectorSchedulerOptions(
                maxItemsPerCycle: ListDueCollectorWorkRequest.MaximumItems,
                maxConcurrency: ReadMaxConcurrency(configuration),
                new WorkerLeaseDuration(TimeSpan.FromSeconds(30)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5))),
            provider.GetRequiredService<IDeadlockActivitySnapshotTrigger>()));
        services.AddHostedService<CapabilityDiscoveryWorker>();
        services.AddSingleton<ILiveActivityRepository>(s => s.GetRequiredService<PostgreSqlCollectorDataPlane>().LiveActivity);
        services.AddSingleton<ILiveActivityProtector>(_ => new LiveActivityProtector(configuration["SqlObserver:LiveActivity:ProtectedKeyPath"]));
        services.AddSingleton<ILiveActivityCollector, SqlServerLiveActivityCollector>();
        services.AddHostedService<LiveActivityWorker>();
        services.AddHostedService<CollectionWorker>();
        services.AddHostedService<RetentionMaintenanceWorker>();
        services.AddHostedService<AlertEvaluationWorker>();
        services.AddHostedService<AlertDeliveryWorker>();
        services.AddHostedService<ReportExpiryWorker>();
        bool contractTesting = environment?.IsEnvironment("ContractTesting") == true ||
            string.Equals(configuration["DOTNET_ENVIRONMENT"], "ContractTesting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "ContractTesting", StringComparison.OrdinalIgnoreCase);
        if (!contractTesting)
        {
            services.AddHostedService<AnalyticsBackfillWorker>();
            services.AddHostedService<AnalyticsDerivationWorker>();
        }
        return services;
    }

    private static int ReadMaxConcurrency(IConfiguration configuration)
    {
        string? configured = configuration["SqlObserver:Collector:MaxConcurrency"];
        if (string.IsNullOrWhiteSpace(configured)) return 16;
        if (!int.TryParse(configured, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) || value is < 1 or > 64)
            throw new InvalidOperationException("SqlObserver:Collector:MaxConcurrency must be an integer from 1 to 64.");
        return value;
    }

    private static CollectorRegistry CreateRegistry(IServiceProvider provider)
    {
        SqlServerCollectorAssetCatalog catalog =
            provider.GetRequiredService<SqlServerCollectorAssetCatalog>();
        var bundleDigest = new CollectorSha256Digest(catalog.BundleChecksum);
        SqlServerCollectorAsset coreAsset = catalog.Get(CollectorCatalogIds.EngineCore);
        SqlServerCollectorAsset inventoryAsset = catalog.Get(CollectorCatalogIds.DatabaseInventory);
        SqlServerCollectorAsset filesAsset = catalog.Get(CollectorCatalogIds.DatabaseFiles);
        SqlServerActivityCollectorAssetCatalog activityCatalog =
            provider.GetRequiredService<SqlServerActivityCollectorAssetCatalog>();
        var activityBundleDigest = new CollectorSha256Digest(activityCatalog.BundleChecksum);
        SqlServerCollectorAsset sessionsAsset = activityCatalog.Get(new CollectorId("activity.sessions"));
        SqlServerCollectorAsset requestsAsset = activityCatalog.Get(new CollectorId("activity.requests"));
        SqlServerCollectorAsset waitsAsset = activityCatalog.Get(new CollectorId("waits.server"));
        SqlServerCollectorAsset blockingAsset = activityCatalog.Get(new CollectorId("blocking.current"));
        SqlServerDeadlockCollectorAssetCatalog deadlockCatalog = provider.GetRequiredService<SqlServerDeadlockCollectorAssetCatalog>();
        SqlServerQueryPerformanceCollectorAssetCatalog queryCatalog = provider.GetRequiredService<SqlServerQueryPerformanceCollectorAssetCatalog>();
        SqlServerOperationalHealthAssetCatalog m9Catalog = provider.GetRequiredService<SqlServerOperationalHealthAssetCatalog>();
        var m9Bundle = new CollectorSha256Digest(m9Catalog.BundleChecksum);
        SqlServerReplicationAssetCatalog replicationCatalog = provider.GetRequiredService<SqlServerReplicationAssetCatalog>();
        var replicationBundle = new CollectorSha256Digest(replicationCatalog.BundleChecksum);
        HostMetricsAssetCatalog hostCatalog = provider.GetRequiredService<HostMetricsAssetCatalog>();
        var hostBundle = new CollectorSha256Digest(hostCatalog.BundleChecksum);
        static CollectorSha256Digest Digest(string json) => new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant());
        return new CollectorRegistry(
        [
            new CollectorRegistration(
                executionOrder: 1,
                provider.GetRequiredService<SqlServerCoreEngineCollector>(),
                new CollectorOutputValidator(SqlServerCoreEngineCollector.OutputContract),
                new CollectorSha256Digest(coreAsset.ManifestChecksum),
                bundleDigest),
            new CollectorRegistration(
                executionOrder: 2,
                provider.GetRequiredService<SqlServerDatabaseInventoryCollector>(),
                new CollectorOutputValidator(SqlServerDatabaseInventoryCollector.OutputContract),
                new CollectorSha256Digest(inventoryAsset.ManifestChecksum),
                bundleDigest),
            new CollectorRegistration(
                executionOrder: 3,
                provider.GetRequiredService<SqlServerDatabaseFilesCollector>(),
                new CollectorOutputValidator(SqlServerDatabaseFilesCollector.OutputContract),
                new CollectorSha256Digest(filesAsset.ManifestChecksum),
                bundleDigest),
            new CollectorRegistration(
                executionOrder: 4,
                provider.GetRequiredService<SqlServerActivitySessionsCollector>(),
                new CollectorOutputValidator(SqlServerActivitySessionsCollector.OutputContract),
                new CollectorSha256Digest(sessionsAsset.ManifestChecksum),
                activityBundleDigest),
            new CollectorRegistration(
                executionOrder: 5,
                provider.GetRequiredService<SqlServerActivityRequestsCollector>(),
                new CollectorOutputValidator(SqlServerActivityRequestsCollector.OutputContract),
                new CollectorSha256Digest(requestsAsset.ManifestChecksum),
                activityBundleDigest),
            new CollectorRegistration(
                executionOrder: 6,
                provider.GetRequiredService<SqlServerServerWaitsCollector>(),
                new CollectorOutputValidator(SqlServerServerWaitsCollector.OutputContract),
                new CollectorSha256Digest(waitsAsset.ManifestChecksum),
                activityBundleDigest),
            new CollectorRegistration(
                executionOrder: 7,
                provider.GetRequiredService<SqlServerCurrentBlockingCollector>(),
                new CollectorOutputValidator(SqlServerCurrentBlockingCollector.OutputContract),
                new CollectorSha256Digest(blockingAsset.ManifestChecksum),
                activityBundleDigest),
            new CollectorRegistration(
                executionOrder: 8,
                provider.GetRequiredService<SqlServerDeadlockCollector>(),
                new CollectorOutputValidator(SqlServerDeadlockCollector.OutputContract),
                new CollectorSha256Digest(deadlockCatalog.Asset.ManifestChecksum),
                new CollectorSha256Digest(deadlockCatalog.BundleChecksum)),
            new CollectorRegistration(
                executionOrder: 9,
                provider.GetRequiredService<SqlServerQueryPerformanceCollector>(),
                new CollectorOutputValidator(SqlServerQueryPerformanceCollector.OutputContract),
                new CollectorSha256Digest(queryCatalog.Asset.ManifestChecksum),
                new CollectorSha256Digest(queryCatalog.BundleChecksum)),
            new CollectorRegistration(10, provider.GetRequiredService<SqlServerBackupsStatusCollector>(), new CollectorOutputValidator(M9Manifest.OutputContract(1537)), Digest(m9Catalog.Get("backups.status.v1.json")), m9Bundle),
            new CollectorRegistration(11, provider.GetRequiredService<SqlServerSqlAgentFailuresCollector>(), new CollectorOutputValidator(M9Manifest.OutputContract(512)), Digest(m9Catalog.Get("sql-agent.failures.v1.json")), m9Bundle),
            new CollectorRegistration(12, provider.GetRequiredService<SqlServerTempDbHealthCollector>(), new CollectorOutputValidator(M9Manifest.OutputContract(128)), Digest(m9Catalog.Get("tempdb.health.v1.json")), m9Bundle),
            new CollectorRegistration(13, provider.GetRequiredService<SqlServerAvailabilityGroupsHealthCollector>(), new CollectorOutputValidator(M9Manifest.OutputContract(2048)), Digest(m9Catalog.Get("availability-groups.health.v1.json")), m9Bundle),
            new CollectorRegistration(14, provider.GetRequiredService<HostMetricsCollectorAdapter>(), new CollectorOutputValidator(HostMetricsCollectorAdapter.OutputContract), Digest(hostCatalog.Get("host.metrics.v1.json")), hostBundle),
            new CollectorRegistration(15, provider.GetRequiredService<SqlServerReplicationCollector>(), new CollectorOutputValidator(SqlServerReplicationCollector.OutputContract), Digest(replicationCatalog.Get("replication.health.v1.json")), replicationBundle),
        ]);
    }
}

internal sealed class PostgreSqlHostTargetStore(PostgreSqlCollectorDataPlane dataPlane) : IHostTargetStore
{
    public ValueTask<HostTarget?> ReadAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken) =>
        dataPlane.ReadHostTargetAsync(targetId, revision, cancellationToken);
}
