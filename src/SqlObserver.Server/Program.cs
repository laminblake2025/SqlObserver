using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Security;
using SqlObserver.Server;

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
    options.Limits.MaxRequestBodySize = RequestBodyLimitMiddleware.MaximumRequestBytes);
builder.Services.AddSingleton(static services =>
    WindowsAuthorizationConfiguration.CreateResolver(
        services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(static services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    string repositoryConfiguration = configuration.GetConnectionString("SqlObserverRepository") ??
        throw new InvalidOperationException("The SqlObserver repository is not configured.");
    return PostgreSqlTargetControlPlane.Create(repositoryConfiguration, "SqlObserver.Server");
});
builder.Services.AddSingleton<IObservationTargetRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().Targets);
builder.Services.AddSingleton<ICapabilityProfileRepositoryPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().CapabilityProfiles);
builder.Services.AddSingleton<IAdministrativeAuditPort>(static services =>
    services.GetRequiredService<PostgreSqlTargetControlPlane>().AdministrativeAudit);
builder.Services.AddSingleton<IObservationTargetOnboardingService, ObservationTargetOnboardingService>();
builder.Services.AddSingleton<IObservationTargetManagementService, ObservationTargetManagementService>();
builder.Services.AddSingleton<IObservationTargetQueryService, ObservationTargetQueryService>();
builder.Services.AddSingleton<IObservationTargetStatusQueryService, ObservationTargetStatusQueryService>();

WebApplication app = builder.Build();

app.UseMiddleware<SafeApiExceptionMiddleware>();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapSqlObserverScaffoldEndpoints();
app.MapObservationTargetEndpoints();

await app.RunAsync();

/// <summary>Exposes the production entry point to contract-test hosting.</summary>
public partial class Program;
