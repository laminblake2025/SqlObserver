using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged protected cache producer; production catalog activation requires separate qualification.</summary>
internal sealed class SqlServerQueryPerformanceV3Collector : ISqlServerCollector
{
    internal static CollectorOutputContract OutputContract { get; } = new(new(3), [], 0, 0, 0,
        maxQueryPerformanceObservations: 20_000, maxPlanCacheRuntimeObservations: 2_000, maxPlanCacheContentObservations: 8);
    private readonly SqlServerQueryPerformanceV2Collector combined;
    internal SqlServerQueryPerformanceV3Collector(ISqlServerConnectionFactory? factory = null,
        IQuerySensitiveContentProtector? protector = null)
        : this(SqlServerQueryPerformanceV3AssetCatalog.LoadEmbedded(), factory ??
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName), protector, null, null)
    { }
    internal SqlServerQueryPerformanceV3Collector(SqlServerQueryPerformanceV3AssetCatalog catalog,
        ISqlServerConnectionFactory factory, IQuerySensitiveContentProtector? protector,
        Func<CollectorExecutionRequest, CancellationToken, Task<PlanCacheRuntimeTargetRead>>? cacheRead,
        ISqlServerQueryPerformanceExecutionPort? queryStorePort) =>
        combined = new(catalog, factory, protector, cacheRead, queryStorePort);
    public CollectorManifest Manifest => combined.Manifest;
    public ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken token) =>
        combined.CollectAsync(request, token);
}
