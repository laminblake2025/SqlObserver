using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Security;
using SqlObserver.Reporting;

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
        LiveActivity = new PostgreSqlLiveActivityRepository(dataSource);
        Targets = new PostgreSqlObservationTargetPort(dataSource);
        CapabilityProfiles = new PostgreSqlCapabilityProfilePort(dataSource);
        AdministrativeAudit = new PostgreSqlAdministrativeAuditPort(dataSource);
        McpInvocationAudit = new PostgreSqlMcpInvocationAuditPort(dataSource);
        WorkerLeases = new PostgreSqlWorkerLeasePort(dataSource);
        HealthProjections = new PostgreSqlHealthProjectionPort(dataSource);
        SqlVolumeReads = new PostgreSqlSqlVolumeReadPort(dataSource);
        OverviewHistory = new PostgreSqlOverviewHistoryPort(dataSource);
        ActivityProjections = new PostgreSqlActivityProjectionPort(dataSource);
        DeadlockProjections = new PostgreSqlDeadlockProjectionPort(dataSource);
        QueryPerformanceApiProjections = new PostgreSqlQueryPerformanceApiProjectionPort(dataSource);
        QueryTextReads = new PostgreSqlQueryTextReadRepositoryPort(dataSource);
        QueryPlanReads = new PostgreSqlQueryPlanReadRepositoryPort(dataSource);
        QueryPlanWaitReads = new PostgreSqlQueryPlanWaitReadRepositoryPort(dataSource);
        CollectorRuntime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource, fingerprintKey);
        Alerts = new PostgreSqlAlertRepositoryPort(dataSource);
        OperationalHealth = new PostgreSqlOperationalHealthProjectionPort(dataSource);
        Analytics = new PostgreSqlAnalyticsRepositoryPort(dataSource, fingerprintKey);
        AnalyticsDerivation = (IAnalyticsDerivationStore)Analytics;
        AnalyticsBackfill = new PostgreSqlAnalyticsBackfillStore(dataSource);
        ReplicationDistributionBindings = new PostgreSqlReplicationDistributionBindingResolver(dataSource);
        Reports = new PostgreSqlReportRepository(dataSource);
        ReportAudit = new PostgreSqlReportAuditPort(dataSource);
        Compatibility = new PostgreSqlCompatibilityPort(dataSource);
    }

    public IObservationTargetRepositoryPort Targets { get; }
    public ILiveActivityRepository LiveActivity { get; }

    public ICapabilityProfileRepositoryPort CapabilityProfiles { get; }

    public IAdministrativeAuditPort AdministrativeAudit { get; }

    /// <summary>Append-only terminal audit for MCP calls; compose into the MCP adapter.</summary>
    public IMcpInvocationAuditPort McpInvocationAudit { get; }

    public IMcpAuditPort McpAudit => (IMcpAuditPort)McpInvocationAudit;

    public IWorkerLeasePort WorkerLeases { get; }

    public IHealthProjectionRepositoryPort HealthProjections { get; }
    public ISqlVolumeReadRepositoryPort SqlVolumeReads { get; }
    public IOverviewHistoryRepositoryPort OverviewHistory { get; }

    public IActivityProjectionRepositoryPort ActivityProjections { get; }

    public IDeadlockProjectionRepositoryPort DeadlockProjections { get; }
    public IQueryPerformanceApiRepositoryPort QueryPerformanceApiProjections { get; }
    public IQueryTextReadRepositoryPort QueryTextReads { get; }
    public IQueryPlanReadRepositoryPort QueryPlanReads { get; }
    public IQueryPlanWaitReadRepositoryPort QueryPlanWaitReads { get; }

    public ICollectorRuntimeRepositoryPort CollectorRuntime { get; }
    public IAlertRepositoryPort Alerts { get; }
    public IOperationalHealthRepositoryPort OperationalHealth { get; }

    public IAnalyticsRepositoryPort Analytics { get; }

    // Narrow MCP read projections share the analytics data source but remain
    // separate application ports so the composition root cannot accidentally
    // expose an arbitrary analytics repository to a tool.
    public IMetricSeriesProjectionRepositoryPort MetricSeriesProjections => (IMetricSeriesProjectionRepositoryPort)Analytics;
    public IStorageForecastProjectionRepositoryPort StorageForecastProjections => (IStorageForecastProjectionRepositoryPort)Analytics;
    public IDiagnosticEventProjectionRepositoryPort DiagnosticEventProjections => (IDiagnosticEventProjectionRepositoryPort)Analytics;
    public IIncidentEvidenceProjectionRepositoryPort IncidentEvidenceProjections => (IIncidentEvidenceProjectionRepositoryPort)Analytics;
    public IIncidentListProjectionRepositoryPort IncidentListProjections => (IIncidentListProjectionRepositoryPort)Analytics;

    public IAnalyticsDerivationStore AnalyticsDerivation { get; }

    public IAnalyticsBackfillStore AnalyticsBackfill { get; }

    public IReplicationDistributionBindingResolver ReplicationDistributionBindings { get; }

    public IReportRepository Reports { get; }
    public IReportAuditPort ReportAudit { get; }

    public IPostgreSqlCompatibilityPort Compatibility { get; }

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
