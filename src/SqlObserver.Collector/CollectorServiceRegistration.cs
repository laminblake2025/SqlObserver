using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Collector;

/// <summary>Defines the collector host's least-surface composition for runtime and test verification.</summary>
public static class CollectorServiceRegistration
{
    public static IServiceCollection AddSqlObserverCollectorRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        string repositoryConfiguration = configuration.GetConnectionString("SqlObserverRepository") ??
            throw new InvalidOperationException("The SqlObserver repository is not configured.");

        services.AddSingleton(_ => PostgreSqlCollectorDataPlane.Create(
            repositoryConfiguration,
            "SqlObserver.Collector"));
        services.AddSingleton<IWorkerLeasePort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().WorkerLeases);
        services.AddSingleton<ICapabilityProfileRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().CapabilityProfiles);
        services.AddSingleton<ICollectorRuntimeRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Runtime);
        services.AddSingleton<IAlertRepositoryPort>(static provider =>
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Alerts);
        services.AddSingleton<IAlertEvaluationSource, PostgreSqlAlertEvaluationSource>();
        services.AddSingleton<IAlertDestinationConfigurationResolver>(_ => new ConfigurationAlertDestinationResolver(key => configuration[key]));
        services.AddSingleton<IAlertDnsResolver, SystemAlertDnsResolver>();
        services.AddSingleton<IAlertDestinationHttpClientFactory, AddressBoundAlertDestinationHttpClientFactory>();
        services.AddSingleton(static provider => new HttpClient(AddressBoundConnectHandler.Create(provider.GetRequiredService<IAlertDnsResolver>(), AlertNetworkPolicy.IsApprovedAddress)));
        services.AddSingleton<HttpsWebhookAlertDestination>();
        services.AddSingleton<WindowsEventLogAlertDestination>();
        services.AddSingleton<IAlertDestinationPort, AlertDestinationDispatcher>();
        services.AddSingleton<ISqlServerCapabilityDiscoveryPort, SqlServerCapabilityDiscoveryPort>();
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
        services.AddSingleton(static provider => CreateRegistry(provider));
        services.AddSingleton<CollectorExecutionEngine>();
        services.AddSingleton(static provider => new CollectorScheduler(
            provider.GetRequiredService<CollectorRegistry>(),
            provider.GetRequiredService<ICollectorRuntimeRepositoryPort>(),
            provider.GetRequiredService<IWorkerLeasePort>(),
            provider.GetRequiredService<CollectorExecutionEngine>(),
            provider.GetRequiredService<WorkerExecutionId>(),
            new CollectorSchedulerOptions(
                maxItemsPerCycle: ListDueCollectorWorkRequest.MaximumItems,
                maxConcurrency: 4,
                new WorkerLeaseDuration(TimeSpan.FromSeconds(30)),
                new RepositoryCallTimeout(TimeSpan.FromSeconds(5)))));
        services.AddHostedService<CapabilityDiscoveryWorker>();
        services.AddHostedService<CollectionWorker>();
        services.AddHostedService<AlertEvaluationWorker>();
        services.AddHostedService<AlertDeliveryWorker>();
        return services;
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
        ]);
    }
}
