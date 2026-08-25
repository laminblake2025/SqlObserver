using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.EndToEndTests;

public sealed class CollectorCompositionEndToEndTests
{
    [Fact]
    public async Task CollectorHostResolvesRestrictedDataPlaneSchedulerAndWorkersWithoutIo()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlObserverRepository"] =
                    "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlObserverCollectorRuntime(configuration);

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<PostgreSqlCollectorDataPlane>(
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>());
        Assert.Same(
            provider.GetRequiredService<PostgreSqlCollectorDataPlane>().Runtime,
            provider.GetRequiredService<ICollectorRuntimeRepositoryPort>());
        Assert.IsType<CollectorScheduler>(provider.GetRequiredService<CollectorScheduler>());
        Assert.Equal(
            ["engine.core", "database.inventory", "database.files", "activity.sessions",
                "activity.requests", "waits.server", "blocking.current", "deadlocks.system-health", "queries.performance"],
            provider.GetRequiredService<CollectorRegistry>()
                .Registrations
                .Select(static registration => registration.Manifest.Id.Value));
        Assert.Equal(1, provider.GetRequiredService<CollectorRegistry>()
            .Registrations.Single(static registration => registration.Manifest.Id.Value == "deadlocks.system-health")
            .Manifest.ManifestVersion.Value);
        Assert.Equal(4, provider.GetServices<IHostedService>().Count());
        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType == typeof(PostgreSqlTargetControlPlane));
    }
}
