using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlObserver.Application.Ports;
using SqlObserver.Analytics;
using SqlObserver.Collector;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.EndToEndTests;

/// <summary>
/// Verifies the production collector composition without opening PostgreSQL.
/// The registration graph is the runtime boundary for analytics ownership:
/// Server serves reads/mutations, while Collector owns derivation/backfill.
/// </summary>
public sealed class M10AnalyticsCompositionEndToEndTests
{
    [Fact]
    public async Task CollectorOwnsAnalyticsWorkersAndRestrictedRepositoryPorts()
    {
        IConfiguration configuration = Configuration("Production");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        PostgreSqlCollectorDataPlane dataPlane = provider.GetRequiredService<PostgreSqlCollectorDataPlane>();
        Assert.Same(dataPlane.Analytics, provider.GetRequiredService<IAnalyticsRepositoryPort>());
        Assert.Same(dataPlane.AnalyticsDerivation, provider.GetRequiredService<IAnalyticsDerivationStore>());
        Assert.Same(dataPlane.AnalyticsBackfill, provider.GetRequiredService<IAnalyticsBackfillStore>());

        IHostedService[] workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(workers, static worker => worker is ReportExpiryWorker);
        Assert.Contains(workers, static worker => worker is AnalyticsDerivationWorker);
        Assert.Contains(workers, static worker => worker is AnalyticsBackfillWorker);
        Assert.Contains(workers, static worker => worker is LiveActivityWorker);
        Assert.Equal(9, workers.Length);
    }

    [Fact]
    public async Task ContractTestingCompositionDoesNotStartAnalyticsWorkers()
    {
        IConfiguration configuration = Configuration("ContractTesting");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        IHostedService[] workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(workers, static worker => worker is ReportExpiryWorker);
        Assert.DoesNotContain(workers, static worker => worker is AnalyticsDerivationWorker or AnalyticsBackfillWorker);
        Assert.Contains(workers, static worker => worker is LiveActivityWorker);
        Assert.Equal(7, workers.Length);
    }

    private static IConfiguration Configuration(string environment) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] =
                "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
            ["DOTNET_ENVIRONMENT"] = environment,
        })
        .Build();
}
