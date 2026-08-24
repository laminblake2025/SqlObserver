using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Owns the repository data source and exposes only application ports to the Server composition root.
/// </summary>
public sealed class PostgreSqlTargetControlPlane : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private PostgreSqlTargetControlPlane(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        Targets = new PostgreSqlObservationTargetPort(dataSource);
        CapabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
        AdministrativeAudit = new PostgreSqlAdministrativeAuditPort(dataSource);
        WorkerLeases = new PostgreSqlWorkerLeasePort(dataSource);
        HealthProjections = new PostgreSqlHealthProjectionPort(dataSource);
        CollectorRuntime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource);
    }

    public IObservationTargetRepositoryPort Targets { get; }

    public ICapabilityProfileRepositoryPort CapabilityProfiles { get; }

    public IAdministrativeAuditPort AdministrativeAudit { get; }

    public IWorkerLeasePort WorkerLeases { get; }

    public IHealthProjectionRepositoryPort HealthProjections { get; }

    public ICollectorRuntimeRepositoryPort CollectorRuntime { get; }

    public static PostgreSqlTargetControlPlane Create(
        string repositoryConfiguration,
        string applicationName)
    {
        return new PostgreSqlTargetControlPlane(
            PostgreSqlDataSourceFactory.Create(repositoryConfiguration, applicationName));
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
