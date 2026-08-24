using Npgsql;
using SqlObserver.Application.Ports;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Owns the restricted PostgreSQL collector-role data source and collector application ports.</summary>
public sealed class PostgreSqlCollectorDataPlane : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private PostgreSqlCollectorDataPlane(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
        Runtime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource);
        WorkerLeases = new PostgreSqlWorkerLeasePort(dataSource);
        CapabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
    }

    public ICollectorRuntimeRepositoryPort Runtime { get; }
    public IWorkerLeasePort WorkerLeases { get; }
    public ICapabilityProfileRepositoryPort CapabilityProfiles { get; }

    public static PostgreSqlCollectorDataPlane Create(
        string repositoryConfiguration,
        string applicationName) =>
        new(PostgreSqlDataSourceFactory.Create(repositoryConfiguration, applicationName));

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
