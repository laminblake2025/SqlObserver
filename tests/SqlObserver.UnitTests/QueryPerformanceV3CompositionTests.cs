using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class QueryPerformanceV3CompositionTests
{
    [Fact]
    public async Task ProductionComposesStagedV3WithRealProtectionAndKeepsCatalogOnV1()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var staged = provider.GetRequiredService<SqlServerQueryPerformanceV3Collector>();
        Assert.Same(staged, provider.GetRequiredService<SqlServerQueryPerformanceV3Collector>());
        Assert.Equal(3, staged.Manifest.ManifestVersion.Value);
        Assert.Equal(3, staged.Manifest.OutputSchemaVersion.Value);
        Assert.Equal(3, SqlServerQueryPerformanceV3Collector.OutputContract.SchemaVersion.Value);
        var protection = Assert.IsType<QuerySensitiveContentProtector>(provider.GetRequiredService<IQuerySensitiveContentProtector>());
        Assert.False(protection.IsAvailable);
        var registered = provider.GetRequiredService<CollectorRegistry>().GetRequired(CollectorCatalogIds.QueryPerformance);
        Assert.IsType<SqlServerQueryPerformanceCollector>(registered.Collector);
        Assert.Equal(1, registered.Manifest.ManifestVersion.Value);
    }
}
