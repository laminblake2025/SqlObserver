using SqlObserver.Collector;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Collector");
builder.Services.AddSingleton(static services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    string repositoryConfiguration = configuration.GetConnectionString("SqlObserverRepository") ??
        throw new InvalidOperationException("The SqlObserver repository is not configured.");
    return PostgreSqlTargetControlPlane.Create(repositoryConfiguration, "SqlObserver.Collector");
});
builder.Services.AddSingleton<IWorkerLeasePort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().WorkerLeases);
builder.Services.AddSingleton<ICapabilityProfileRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().CapabilityProfiles);
builder.Services.AddSingleton<ISqlServerCapabilityDiscoveryPort, SqlServerCapabilityDiscoveryPort>();
builder.Services.AddSingleton<ICapabilityDiscoveryService, CapabilityDiscoveryService>();
builder.Services.AddHostedService<CapabilityDiscoveryWorker>();

IHost host = builder.Build();
await host.RunAsync();
