using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentModuleCompositionTests
{
    [Fact]
    public async Task CollectorResolvesFreshRepositoryAuthorityWithoutRegisteringActivityScheduleOrAdministration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var resolver = provider.GetRequiredService<ISqlAgentModuleEnrollmentResolver>();
        Assert.Same(provider.GetRequiredService<PostgreSqlCollectorDataPlane>().SqlAgentModuleEnrollments, resolver);
        Assert.IsType<SqlServerAgentActivityModuleCaptureReader>(provider.GetRequiredService<SqlServerAgentActivityModuleCaptureReader>());
        Assert.Null(provider.GetService<ISqlAgentModuleEnrollmentAdministrationPort>());
        Assert.Null(provider.GetService<PostgreSqlTargetControlPlane>());
        Assert.DoesNotContain(provider.GetRequiredService<CollectorRegistry>().Registrations,
            item => item.Manifest.Id.Value == "sql-agent.activity");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(new(Guid.NewGuid()), new(1), cancelled.Token));
    }
}
