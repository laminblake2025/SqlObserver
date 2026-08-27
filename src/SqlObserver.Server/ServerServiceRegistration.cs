using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SqlObserver.Observability;

namespace SqlObserver.Server;

/// <summary>Owns the Server host's production observability composition.</summary>
public static class ServerServiceRegistration
{
    public static IServiceCollection AddSqlObserverServerObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null) =>
        AddSqlObserverServerObservabilityCore(services, configuration, environment, null, null);

    /// <summary>Composes the production Server registration with test-only in-process exporters.</summary>
    public static IServiceCollection AddSqlObserverServerObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<TracerProviderBuilder> configureTracing,
        Action<MeterProviderBuilder> configureMetrics) =>
        AddSqlObserverServerObservabilityCore(services, configuration, environment, configureTracing, configureMetrics);

    private static IServiceCollection AddSqlObserverServerObservabilityCore(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment,
        Action<TracerProviderBuilder>? configureTracing,
        Action<MeterProviderBuilder>? configureMetrics)
    {
        if (configureTracing is null && configureMetrics is null)
        {
            services.AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName, environment);
        }
        else
        {
            ArgumentNullException.ThrowIfNull(environment);
            services.AddSqlObserverObservabilityForContractTesting(
                configuration,
                ObservabilityContract.ServerServiceName,
                environment,
                configureTracing ?? (_ => { }),
                configureMetrics ?? (_ => { }));
        }

        return services;
    }
}
