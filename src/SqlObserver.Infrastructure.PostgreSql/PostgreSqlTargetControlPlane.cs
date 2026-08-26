using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>
/// Owns the repository data source and exposes only application ports to the Server composition root.
/// </summary>
public sealed class PostgreSqlTargetControlPlane : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private PostgreSqlTargetControlPlane(NpgsqlDataSource dataSource, IdentityFingerprintKey fingerprintKey)
    {
        _dataSource = dataSource;
        Targets = new PostgreSqlObservationTargetPort(dataSource);
        CapabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
        AdministrativeAudit = new PostgreSqlAdministrativeAuditPort(dataSource);
        WorkerLeases = new PostgreSqlWorkerLeasePort(dataSource);
        HealthProjections = new PostgreSqlHealthProjectionPort(dataSource);
        ActivityProjections = new PostgreSqlActivityProjectionPort(dataSource);
        DeadlockProjections = new PostgreSqlDeadlockProjectionPort(dataSource);
        QueryPerformanceApiProjections = new PostgreSqlQueryPerformanceApiProjectionPort(dataSource);
        CollectorRuntime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource, fingerprintKey);
        Alerts = new PostgreSqlAlertRepositoryPort(dataSource);
        OperationalHealth = new PostgreSqlOperationalHealthProjectionPort(dataSource);
        Analytics = new PostgreSqlAnalyticsRepositoryPort(dataSource, fingerprintKey);
        AnalyticsDerivation = (IAnalyticsDerivationStore)Analytics;
        AnalyticsBackfill = new PostgreSqlAnalyticsBackfillStore(dataSource);
        ReplicationDistributionBindings = new PostgreSqlReplicationDistributionBindingResolver(dataSource);
    }

    public IObservationTargetRepositoryPort Targets { get; }

    public ICapabilityProfileRepositoryPort CapabilityProfiles { get; }

    public IAdministrativeAuditPort AdministrativeAudit { get; }

    public IWorkerLeasePort WorkerLeases { get; }

    public IHealthProjectionRepositoryPort HealthProjections { get; }

    public IActivityProjectionRepositoryPort ActivityProjections { get; }

    public IDeadlockProjectionRepositoryPort DeadlockProjections { get; }
    public IQueryPerformanceApiRepositoryPort QueryPerformanceApiProjections { get; }

    public ICollectorRuntimeRepositoryPort CollectorRuntime { get; }
    public IAlertRepositoryPort Alerts { get; }
    public IOperationalHealthRepositoryPort OperationalHealth { get; }

    public IAnalyticsRepositoryPort Analytics { get; }

    public IAnalyticsDerivationStore AnalyticsDerivation { get; }

    public IAnalyticsBackfillStore AnalyticsBackfill { get; }

    public IReplicationDistributionBindingResolver ReplicationDistributionBindings { get; }

    public IRetentionRepositoryPort Retention => (IRetentionRepositoryPort)Analytics;

    public static PostgreSqlTargetControlPlane Create(
        string repositoryConfiguration,
        string applicationName,
        IdentityFingerprintKey fingerprintKey)
    {
        return new PostgreSqlTargetControlPlane(
            PostgreSqlDataSourceFactory.Create(repositoryConfiguration, applicationName),
            fingerprintKey ?? throw new ArgumentNullException(nameof(fingerprintKey)));
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
