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

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Server");
AuthenticationBuilder authentication = builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme);
if (!builder.Environment.IsEnvironment("ContractTesting"))
{
    authentication.AddNegotiate();
}
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
    new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection();
builder.Services.AddOptions<OperationalHealthServerOptions>().Validate(options => options.RequestTimeout > TimeSpan.Zero && options.RequestTimeout <= TimeSpan.FromMinutes(2), "Operational health timeout must be positive and bounded.").ValidateOnStart();
builder.Services.AddRequestTimeouts(options =>
    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(15),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
    });
builder.Services.ConfigureHttpJsonOptions(options =>
{
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
builder.Services.AddSingleton<IWorkerLeasePort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().WorkerLeases);
builder.Services.AddSingleton(static _ => new WorkerExecutionId(Guid.NewGuid()));
builder.Services.AddSingleton<IHealthProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().HealthProjections);
builder.Services.AddSingleton<IObservationTargetOnboardingService, ObservationTargetOnboardingService>();
builder.Services.AddSingleton<IObservationTargetManagementService, ObservationTargetManagementService>();
builder.Services.AddSingleton<IObservationTargetQueryService, ObservationTargetQueryService>();
builder.Services.AddSingleton<IObservationTargetStatusQueryService, ObservationTargetStatusQueryService>();
builder.Services.AddSingleton<IHealthProjectionQueryService, HealthProjectionQueryService>();
builder.Services.AddSingleton<IActivityProjectionRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().ActivityProjections);
builder.Services.AddSingleton<IActivityProjectionQueryService, ActivityProjectionQueryService>();
builder.Services.AddSingleton<IDeadlockProjectionRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().DeadlockProjections);
builder.Services.AddSingleton<IDeadlockProjectionQueryService, DeadlockProjectionQueryService>();
builder.Services.AddSingleton<IQueryPerformanceApiRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().QueryPerformanceApiProjections);
builder.Services.AddSingleton<IQueryPerformanceApiQueryService, QueryPerformanceApiQueryService>();
builder.Services.AddSingleton<IAlertRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Alerts);
builder.Services.AddSingleton<IOperationalHealthRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().OperationalHealth);
// The Server retains read/query and administrative analytics ports for its
// HTTP API; the derivation/backfill workers themselves are owned by Collector.
builder.Services.AddSingleton<IAnalyticsRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Analytics);
builder.Services.AddSingleton<IAnalyticsSurfaceRepositoryPort>(static services => (IAnalyticsSurfaceRepositoryPort)services.GetRequiredService<PostgreSqlTargetControlPlane>().Analytics);
builder.Services.AddSingleton<IRetentionRepositoryPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Retention);
builder.Services.AddSingleton<IAnalyticsQueryService, AnalyticsQueryService>();
builder.Services.AddSingleton<IRetentionService, RetentionService>();
builder.Services.AddSingleton<IRetentionPolicyService, RetentionPolicyService>();
builder.Services.AddSingleton<IOperationalHealthQueryService, OperationalHealthQueryService>();
builder.Services.AddSingleton<IAlertQueryService, AlertQueryService>();
builder.Services.AddSingleton<IAlertDestinationApprovalPort, ConfiguredAlertDestinationApproval>();
builder.Services.AddSingleton<IAlertDnsResolver, SystemAlertDnsResolver>();
builder.Services.AddSingleton<IEventLogAlertWriter, WindowsEventLogAlertWriter>();
builder.Services.AddSingleton<IAlertAdministrationService, AlertAdministrationService>();
builder.Services.AddSingleton<IMetricSeriesQueryService, MetricSeriesQueryService>();
builder.Services.AddSingleton<IStorageForecastQueryService, StorageForecastQueryService>();
builder.Services.AddSingleton<IDiagnosticEventQueryService, DiagnosticEventQueryService>();
builder.Services.AddSingleton<IIncidentEvidenceQueryService, IncidentEvidenceQueryService>();
builder.Services.AddSingleton<IReportRepository>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().Reports);
builder.Services.AddSingleton<IReportAuditPort>(static services => services.GetRequiredService<PostgreSqlTargetControlPlane>().ReportAudit);
builder.Services.AddSingleton<IReportService, ReportService>();
builder.Services.AddSingleton<ReportCursorProtector>();
builder.Services.AddSqlObserverMcp();
// Contract-test hosts intentionally do not configure (or open) the production
// PostgreSQL control plane.  Resolving the hosted worker in that environment
// would eagerly construct its repository/lease dependencies and can turn an
// otherwise isolated API test into a production connection attempt.  Keep the
// worker enabled for every real host while fencing it out of contract tests.
WebApplication app = builder.Build();

app.UseMiddleware<SafeApiExceptionMiddleware>();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseMiddleware<McpHttpAuditBoundaryMiddleware>();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
app.MapSqlObserverScaffoldEndpoints();
app.MapObservationTargetEndpoints();
app.MapTargetHealthEndpoints();
app.MapTargetActivityEndpoints();
app.MapTargetDeadlockEndpoints();
app.MapTargetQueryPerformanceApiEndpoints();
app.MapAlertEndpoints();
app.MapOperationalHealthEndpoints();
app.MapAnalyticsEndpoints();
app.MapReportEndpoints();
app.MapSqlObserverMcp().RequireAuthorization();

await app.RunAsync();

/// <summary>Exposes the production entry point to contract-test hosting.</summary>
public partial class Program;
