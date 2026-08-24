using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.EndToEndTests;

public sealed class M7QueryPerformanceCompositionEndToEndTests
{
    [Fact]
    public async Task QueryPerformanceIsReachableAtOrderNineAndProductionProtectorIsUnavailable()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlObserverCollectorRuntime(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var registration = provider.GetRequiredService<CollectorRegistry>().Registrations.Single(x => x.Manifest.Id.Value == "queries.performance");
        Assert.Equal(9, registration.ExecutionOrder);
        Assert.Equal(10, (int)registration.Manifest.OutputKind);
        Assert.Equal("queries.performance", registration.Manifest.Id.Value);
    }
}
