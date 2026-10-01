using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.Mcp;
using SqlObserver.Security;
using SqlObserver.Server;
using SqlObserver.Reporting;
using SqlObserver.Observability;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Server");
if (DevelopmentAuthentication.ValidateConfiguration(builder.Environment, builder.Configuration))
{
    builder.Services.AddAuthentication(DevelopmentAuthentication.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthentication.SchemeName, static _ => { });
}
else
{
    AuthenticationBuilder authentication = builder.Services
        .AddAuthentication(NegotiateDefaults.AuthenticationScheme);
    if (!builder.Environment.IsEnvironment("ContractTesting")) authentication.AddNegotiate();
}
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
    new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection();
builder.Services.AddSqlObserverServerObservability(builder.Configuration, builder.Environment);
builder.Services.AddOptions<OperationalHealthServerOptions>().Validate(options => options.RequestTimeout > TimeSpan.Zero && options.RequestTimeout <= TimeSpan.FromMinutes(2), "Operational health timeout must be positive and bounded.").ValidateOnStart();
builder.Services.AddRequestTimeouts(options =>
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(15),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
    });
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new CanonicalUtcDateTimeOffsetConverter());
    options.SerializerOptions.MaxDepth = 32;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
AdministrativeMutationRateLimitSettings administrativeMutationLimits =
    AdministrativeMutationRateLimitSettings.FromConfiguration(builder.Configuration);
builder.Services.AddRateLimiter(options => options.AddPolicy(
    AdministrativeMutationRateLimitPolicy.PolicyName,
    new AdministrativeMutationRateLimitPolicy(administrativeMutationLimits)));
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = RequestBodyLimitMiddleware.KestrelMaximumRequestBytes);
builder.Services.AddSingleton(static services =>
    WindowsAuthorizationConfiguration.CreateResolver(
        services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IIdentityFingerprintKeyProvider>(_ =>
    new ConfigurationIdentityFingerprintKeyProvider(() => builder.Configuration["SqlObserver:IdentityFingerprintKey"]));
builder.Services.AddSingleton<IdentityFingerprintKey>(services =>
    services.GetRequiredService<IIdentityFingerprintKeyProvider>().GetRequiredKey());
builder.Services.AddSingleton(static services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    string repositoryConfiguration = configuration.GetConnectionString("SqlObserverRepository") ??
        throw new InvalidOperationException("The SqlObserver repository is not configured.");
    return PostgreSqlTargetControlPlane.Create(repositoryConfiguration, "SqlObserver.Server", services.GetRequiredService<IdentityFingerprintKey>());
});
builder.Services.AddSingleton<IObservationTargetRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().Targets);
builder.Services.AddSingleton<IObservationTargetGroupingPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().TargetGrouping);
builder.Services.AddSingleton<ICapabilityProfileRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().CapabilityProfiles);
builder.Services.AddSingleton<IAdministrativeAuditPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().AdministrativeAudit);
builder.Services.AddSingleton<IMcpInvocationAuditPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().McpInvocationAudit);
builder.Services.AddSingleton<IMetricSeriesProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().MetricSeriesProjections);
builder.Services.AddSingleton<IStorageForecastProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().StorageForecastProjections);
builder.Services.AddSingleton<IDiagnosticEventProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().DiagnosticEventProjections);
builder.Services.AddSingleton<IIncidentEvidenceProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().IncidentEvidenceProjections);
builder.Services.AddSingleton<IIncidentListProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().IncidentListProjections);
builder.Services.AddSingleton<IWorkerLeasePort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().WorkerLeases);
builder.Services.AddSingleton(static _ => new WorkerExecutionId(Guid.NewGuid()));
builder.Services.AddSingleton<IHealthProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().HealthProjections);
builder.Services.AddSingleton<ISqlVolumeReadRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().SqlVolumeReads);
builder.Services.AddSingleton<ISqlVolumeReadService, SqlVolumeReadService>();
builder.Services.AddSingleton<IDatabaseFileIoTrendRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().DatabaseFileIoTrends);
builder.Services.AddSingleton<IDatabaseFileIoTrendQueryService, DatabaseFileIoTrendQueryService>();
builder.Services.AddSingleton<IBufferNodePleWindowRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().BufferNodePleWindows);
builder.Services.AddSingleton<IBufferNodePleWindowQueryService, BufferNodePleWindowQueryService>();
builder.Services.AddSingleton<SqlVolumeCursorProtector>();
builder.Services.AddSingleton<IObservationTargetOnboardingService, ObservationTargetOnboardingService>();
builder.Services.AddSingleton<IObservationTargetManagementService, ObservationTargetManagementService>();
builder.Services.AddSingleton<IObservationTargetGroupingService, ObservationTargetGroupingService>();
builder.Services.AddSingleton<IObservationTargetQueryService, ObservationTargetQueryService>();
builder.Services.AddSingleton<IObservationTargetStatusQueryService, ObservationTargetStatusQueryService>();
builder.Services.AddSingleton<IHealthProjectionQueryService, HealthProjectionQueryService>();
builder.Services.AddSingleton<IOverviewHistoryRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().OverviewHistory);
builder.Services.AddSingleton<IOverviewLatestStatusRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().OverviewLatestStatus);
builder.Services.AddSingleton<IBackupFleetLatestRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().BackupFleetLatest);
builder.Services.AddSingleton<IOverviewQueryService, OverviewQueryService>();
builder.Services.AddSingleton<IActivityProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().ActivityProjections);
builder.Services.AddSingleton<IActivityProjectionQueryService, ActivityProjectionQueryService>();
builder.Services.AddSingleton<IServerWaitTrendRepositoryPort>(static services =>
    (PostgreSqlActivityProjectionPort)services.GetRequiredService<PostgreSqlTargetControlPlane>().ActivityProjections);
builder.Services.AddSingleton<IServerWaitTrendQueryService, ServerWaitTrendQueryService>();
builder.Services.AddSingleton<ILiveActivityRepository>(s => s.GetRequiredService<PostgreSqlTargetControlPlane>().LiveActivity);
builder.Services.AddSingleton<ILiveActivityProtector>(_ => new LiveActivityProtector(builder.Configuration["SqlObserver:LiveActivity:ProtectedKeyPath"]));
builder.Services.AddSingleton<ILiveActivityQueryService>(s => new LiveActivityQueryService(s.GetRequiredService<ILiveActivityRepository>(), s.GetRequiredService<ILiveActivityProtector>(), TimeProvider.System));
builder.Services.AddSingleton<IDeadlockProjectionRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().DeadlockProjections);
builder.Services.AddSingleton<IDeadlockProjectionQueryService, DeadlockProjectionQueryService>();
builder.Services.AddSingleton<IQueryPerformanceApiRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryPerformanceApiProjections);
builder.Services.AddSingleton<IQueryPerformanceApiQueryService, QueryPerformanceApiQueryService>();
builder.Services.AddSingleton<IQueryTextReadRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryTextReads);
builder.Services.AddSingleton<IQueryPlanReadRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryPlanReads);
builder.Services.AddSingleton<IQueryPlanWaitReadRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryPlanWaitReads);
builder.Services.AddSingleton<IQueryStoreGroupPlanTrendRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryStoreGroupPlanTrends);
builder.Services.AddSingleton<IQueryStoreWaitRankingRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryStoreWaitRankings);
builder.Services.AddSingleton<IDatabaseOptionChangeRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().DatabaseOptionChanges);
builder.Services.AddSingleton<IServerConfigurationChangeRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().ServerConfigurationChanges);
builder.Services.AddSingleton<IServerConfigurationSnapshotRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().ServerConfigurationSnapshots);
builder.Services.AddSingleton<IConfigurationCollectionAdministrationPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().ConfigurationCollectionAdministration);
builder.Services.AddSingleton<IQueryStorePlanRegressionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryStorePlanRegressions);
builder.Services.AddSingleton<SqlObserver.Domain.SensitiveData.IQuerySensitiveContentProtector>(_ =>
    new SqlObserver.Infrastructure.Windows.QuerySensitiveContentProtector(builder.Configuration["SqlObserver:QueryContent:ProtectedKeyPath"]));
builder.Services.AddSingleton<IQueryTextReadService, QueryTextReadService>();
builder.Services.AddSingleton<IQueryPlanReadService, QueryPlanReadService>();
builder.Services.AddSingleton<IQueryPlanWaitReadService, QueryPlanWaitReadService>();
builder.Services.AddSingleton<IQueryStoreGroupPlanTrendQueryService, QueryStoreGroupPlanTrendQueryService>();
builder.Services.AddSingleton<IQueryStoreWaitRankingQueryService, QueryStoreWaitRankingQueryService>();
builder.Services.AddSingleton<IDatabaseOptionChangeQueryService, DatabaseOptionChangeQueryService>();
builder.Services.AddSingleton<IServerConfigurationChangeQueryService, ServerConfigurationChangeQueryService>();
builder.Services.AddSingleton<IServerConfigurationSnapshotQueryService, ServerConfigurationSnapshotQueryService>();
builder.Services.AddSingleton<IConfigurationCollectionAdministrationService, ConfigurationCollectionAdministrationService>();
builder.Services.AddSingleton<IBackupZoneQualificationAdministrationPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().BackupZoneQualifications);
builder.Services.AddSingleton<BackupZoneQualificationAdministrationService>();
builder.Services.AddSingleton<IBackupFreshnessPolicyPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().BackupFreshnessPolicies);
builder.Services.AddSingleton<BackupFreshnessPolicyService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ApiCursorSigner>(static services =>
{
    string? encoded = services.GetRequiredService<IConfiguration>()["SqlObserver:Api:CursorSigningKey"];
    if (string.IsNullOrWhiteSpace(encoded))
    {
        LoggerMessage.Define(LogLevel.Warning,
            new EventId(1, "ApiCursorSigningEphemeral"),
            "API cursor signing uses a process-local key; existing cursors expire after restart or routing to another server. Configure SqlObserver:Api:CursorSigningKey for stable paging.")(
                services.GetRequiredService<ILogger<ApiCursorSigner>>(), null);
        return new ApiCursorSigner(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }
    try { return new ApiCursorSigner(Convert.FromBase64String(encoded)); }
    catch (Exception exception) when (exception is FormatException or ArgumentException)
    {
        throw new InvalidOperationException("SqlObserver:Api:CursorSigningKey must be base64 for a 32-to-4096-byte key.", exception);
    }
});
builder.Services.AddSingleton<ServerConfigurationChangeCursorCodec>();
builder.Services.AddSingleton<ServerConfigurationSnapshotCursorCodec>();
builder.Services.AddSingleton<AvailabilityGroupEvidenceCursorCodec>();
builder.Services.AddSingleton<BackupEvidenceCursorCodec>();
builder.Services.AddSingleton<SavedIncidentListCursorCodec>();
builder.Services.AddSingleton<IBackupEvidencePort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().BackupEvidence);
builder.Services.AddSingleton<BackupAssessmentQueryService>();
builder.Services.AddSingleton<IQueryStorePlanRegressionQueryService, QueryStorePlanRegressionQueryService>();
builder.Services.AddSingleton<IAlertRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Alerts);
builder.Services.AddSingleton<IAlertRuleReadPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().AlertRuleReads);
builder.Services.AddSingleton<IMaintenanceWindowReadPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().MaintenanceWindowReads);
builder.Services.AddSingleton<IAlertDestinationReadPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().AlertDestinationReads);
builder.Services.AddSingleton<IOperationalHealthRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().OperationalHealth);
builder.Services.AddSingleton<IAvailabilityGroupEvidenceRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().AvailabilityGroupEvidence);
builder.Services.AddSingleton<IAvailabilityGroupEvidenceQueryService, AvailabilityGroupEvidenceQueryService>();
// The Server retains read/query and administrative analytics ports for its
// HTTP API; the derivation/backfill workers themselves are owned by Collector.
builder.Services.AddSingleton<IAnalyticsRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Analytics);
builder.Services.AddSingleton<IAnalyticsSurfaceRepositoryPort>(static services => (IAnalyticsSurfaceRepositoryPort)services.GetRequiredService<PostgreSqlTargetControlPlane>().Analytics);
builder.Services.AddSingleton<IRetentionRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Retention);
builder.Services.AddSingleton<IRetentionPolicyRepositoryPort>(static services => (IRetentionPolicyRepositoryPort)services.GetRequiredService<PostgreSqlTargetControlPlane>().Analytics);
builder.Services.AddSingleton<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddSingleton<SqlMetricBaselineQueryService>();
builder.Services.AddSingleton<ISavedIncidentRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().SavedIncidents);
builder.Services.AddSingleton<ISavedIncidentService, SavedIncidentService>();
builder.Services.AddSingleton<IRetentionService, RetentionService>();
builder.Services.AddSingleton<IRetentionPolicyService, RetentionPolicyService>();
builder.Services.AddSingleton<IOperationalHealthQueryService, OperationalHealthQueryService>();
builder.Services.AddSingleton<IAlertQueryService, AlertQueryService>();
builder.Services.AddSingleton<IAlertRuleQueryService, AlertRuleQueryService>();
builder.Services.AddSingleton<IMaintenanceWindowQueryService, MaintenanceWindowQueryService>();
builder.Services.AddSingleton<IAlertDestinationQueryService, AlertDestinationQueryService>();
builder.Services.AddSingleton<IAlertDestinationApprovalPort, ConfiguredAlertDestinationApproval>();
builder.Services.AddSingleton<IAlertDnsResolver, SystemAlertDnsResolver>();
builder.Services.AddSingleton<IEventLogAlertWriter, WindowsEventLogAlertWriter>();
builder.Services.AddSingleton<IAlertAdministrationService, AlertAdministrationService>();
builder.Services.AddSingleton<IMetricSeriesQueryService, MetricSeriesQueryService>();
builder.Services.AddSingleton<IStorageForecastQueryService, StorageForecastQueryService>();
builder.Services.AddSingleton<IDiagnosticEventQueryService, DiagnosticEventQueryService>();
builder.Services.AddSingleton<IIncidentEvidenceQueryService, IncidentEvidenceQueryService>();
builder.Services.AddSingleton<IIncidentListQueryService, IncidentListQueryService>();
builder.Services.AddSingleton<IReportRepository>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Reports);
builder.Services.AddSingleton<IReportAuditPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().ReportAudit);
builder.Services.AddSingleton<IPostgreSqlCompatibilityPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Compatibility);
builder.Services.AddSingleton<IRepositoryReadinessMonitor, PostgreSqlRepositoryReadinessMonitor>();
builder.Services.AddSingleton<IReportService, ReportService>();
builder.Services.AddSingleton<ReportCursorProtector>();
builder.Services.AddSqlObserverMcp(builder.Configuration);
// Contract-test hosts intentionally do not configure (or open) the production
// PostgreSQL control plane.  Resolving the hosted worker in that environment
// would eagerly construct its repository/lease dependencies and can turn an
// otherwise isolated API test into a production connection attempt.  Keep the
// worker enabled for every real host while fencing it out of contract tests.
WebApplication app = builder.Build();
WebInterfaceAssetCatalog? webInterface = WebInterfaceAssetCatalog.Load(builder.Configuration);

app.UseMiddleware<SafeApiExceptionMiddleware>();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
app.UseAuthorization();
app.UseMiddleware<ApiMutationOriginMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.MapSqlObserverServiceStatusEndpoints(includeRootDescriptor: webInterface is null);
app.MapObservationTargetEndpoints();
app.MapTargetHealthEndpoints();
app.MapBackupZoneQualificationEndpoints();
app.MapBackupFreshnessPolicyEndpoints();
app.MapSqlVolumeEndpoints();
app.MapDatabaseFileIoTrendEndpoints();
app.MapBufferNodePleWindowEndpoints();
app.MapOverviewEndpoints();
app.MapTargetActivityEndpoints();
app.MapLiveActivityEndpoints();
app.MapTargetDeadlockEndpoints();
app.MapTargetQueryPerformanceApiEndpoints();
app.MapAlertEndpoints();
app.MapOperationalHealthEndpoints();
app.MapAvailabilityGroupEvidenceEndpoints();
app.MapBackupAssessmentEndpoints();
app.MapAnalyticsEndpoints();
app.MapSavedIncidentEndpoints();
app.MapReportEndpoints();
app.MapSqlObserverMcp().RequireAuthorization();
if (webInterface is not null)
{
    app.MapSqlObserverWebInterface(webInterface);
}

await app.RunAsync();

/// <summary>Exposes the production entry point to contract-test hosting.</summary>
public partial class Program;
