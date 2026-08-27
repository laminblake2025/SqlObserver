using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SqlObserver.Application.Ports;

namespace SqlObserver.Observability;

/// <summary>Stable identities and bounds for product-owned observability signals.</summary>
public static class ObservabilityContract
{
    public const string ActivitySourceName = "SqlObserver.Observability";
    public const string MeterName = "SqlObserver.Observability";
    public const string ServerServiceName = "SqlObserver.Server";
    public const string CollectorServiceName = "SqlObserver.Collector";
    public const int MaximumAttributeCount = 12;
    public const int MaximumAttributeValueLength = 64;
    public const int MaximumReadinessDurationMilliseconds = 5_000;
    public static readonly IReadOnlySet<string> RequiredMetricNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "sqlobserver.service.readiness.duration",
        "sqlobserver.service.readiness.failures",
        "sqlobserver.collector.duration",
        "sqlobserver.collector.retries",
        "sqlobserver.collector.loss.items",
        "sqlobserver.collector.schedule.lag",
        "sqlobserver.collector.lease.contention",
        "sqlobserver.collector.lease.loss",
    };
}

public sealed class ObservabilityOptions
{
    public string ServiceName { get; set; } = string.Empty;
    public Uri? OtlpEndpoint { get; set; }
    internal bool ContractTesting { get; set; }
}

/// <summary>Rejects endpoint values that could leak credentials or silently use cleartext production transport.</summary>
public static class OtlpEndpointPolicy
{
    public static bool IsAllowed(Uri endpoint, bool contractTesting)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            return false;
        if (endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!contractTesting || !endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            endpoint.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(endpoint.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
    }

    public static void Validate(Uri? endpoint, bool contractTesting)
    {
        if (endpoint is not null && !IsAllowed(endpoint, contractTesting))
            throw new OptionsValidationException(
                nameof(ObservabilityOptions),
                typeof(ObservabilityOptions),
                ["OTLP endpoint must be credential-free, query/fragment-free, and HTTPS; HTTP is allowed only for loopback ContractTesting."]);
    }
}

/// <summary>Registers OTel SDK instrumentation without selecting a backend or retention policy.</summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddSqlObserverObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        IHostEnvironment? environment = null) =>
        AddSqlObserverObservabilityCore(services, configuration, serviceName, environment, null, null);

    /// <summary>
    /// Composes the same production registration with bounded in-process
    /// exporters for ContractTesting. No exporter callback is accepted by a
    /// production environment.
    /// </summary>
    public static IServiceCollection AddSqlObserverObservabilityForContractTesting(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        IHostEnvironment environment,
        Action<TracerProviderBuilder> configureTracing,
        Action<MeterProviderBuilder> configureMetrics)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configureTracing);
        ArgumentNullException.ThrowIfNull(configureMetrics);
        return AddSqlObserverObservabilityCore(services, configuration, serviceName, environment, configureTracing, configureMetrics);
    }

    private static IServiceCollection AddSqlObserverObservabilityCore(
        IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        IHostEnvironment? environment,
        Action<TracerProviderBuilder>? configureTracing,
        Action<MeterProviderBuilder>? configureMetrics)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (serviceName is not (ObservabilityContract.ServerServiceName or ObservabilityContract.CollectorServiceName))
            throw new ArgumentException("The service name is not a supported SqlObserver host.", nameof(serviceName));

        bool contractTesting = environment?.IsEnvironment("ContractTesting") == true ||
            string.Equals(configuration["DOTNET_ENVIRONMENT"], "ContractTesting", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "ContractTesting", StringComparison.OrdinalIgnoreCase);
        RejectAmbientCredentials(configuration);
        Uri? endpoint = ReadEndpoint(configuration, "OTEL_EXPORTER_OTLP_ENDPOINT") ??
            ReadEndpoint(configuration, "SqlObserver:Observability:OtlpEndpoint");
        Uri? tracesEndpoint = ReadEndpoint(configuration, "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT") ?? endpoint;
        Uri? metricsEndpoint = ReadEndpoint(configuration, "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT") ?? endpoint;
        Uri? logsEndpoint = ReadEndpoint(configuration, "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT");
        OtlpEndpointPolicy.Validate(endpoint, contractTesting);
        OtlpEndpointPolicy.Validate(tracesEndpoint, contractTesting);
        OtlpEndpointPolicy.Validate(metricsEndpoint, contractTesting);
        OtlpEndpointPolicy.Validate(logsEndpoint, contractTesting);
        if ((configureTracing is not null || configureMetrics is not null) && !contractTesting)
            throw new InvalidOperationException("In-process observability exporters are restricted to ContractTesting.");
        services.AddOptions<ObservabilityOptions>()
            .Configure(options =>
            {
                options.ServiceName = serviceName;
                options.OtlpEndpoint = endpoint;
                options.ContractTesting = contractTesting;
            })
            .Validate(options => options.ServiceName == serviceName &&
                (options.OtlpEndpoint is null || OtlpEndpointPolicy.IsAllowed(options.OtlpEndpoint, contractTesting)),
                "The observability configuration is invalid.")
            .ValidateOnStart();

        OpenTelemetryBuilder otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(ObservabilityContract.ActivitySourceName);
                tracing.AddSource("SqlObserver.Collectors");
                configureTracing?.Invoke(tracing);
                if (tracesEndpoint is not null)
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = tracesEndpoint);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ObservabilityContract.MeterName);
                metrics.AddMeter("SqlObserver.Collectors");
                metrics.AddMeter("SqlObserver.Collectors.Scheduler");
                configureMetrics?.Invoke(metrics);
                if (metricsEndpoint is not null)
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = metricsEndpoint);
            });
        _ = otel;
        return services;
    }

    private static Uri ParseEndpoint(string text)
    {
        if (text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out Uri? endpoint))
            throw new OptionsValidationException(nameof(ObservabilityOptions), typeof(ObservabilityOptions), ["OTLP endpoint must be an absolute URI."]);
        return endpoint;
    }

    private static Uri? ReadEndpoint(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : ParseEndpoint(value);
    }

    private static void RejectAmbientCredentials(IConfiguration configuration)
    {
        // Headers and certificate/key settings are intentionally not accepted
        // by this foundation package: they are credential material and must be
        // owned by the deployment integration, never ambient process config.
        string[] forbiddenKeys =
        [
            "OTEL_EXPORTER_OTLP_HEADERS",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
            "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
            "OTEL_EXPORTER_OTLP_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_TRACES_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_TRACES_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_TRACES_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_METRICS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_METRICS_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_METRICS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
            "OTEL_EXPORTER_OTLP_LOGS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_LOGS_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_LOGS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_TRACES_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_METRICS_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_LOGS_PRIVATE_KEY",
            "SqlObserver:Observability:Headers",
            "SqlObserver:Observability:Certificate",
            "SqlObserver:Observability:ClientCertificate",
            "SqlObserver:Observability:ClientKey",
        ];
        foreach (string key in forbiddenKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
                throw new OptionsValidationException(nameof(ObservabilityOptions), typeof(ObservabilityOptions), [$"Observability credential/header setting '{key}' is not accepted."]);
        }

        foreach (KeyValuePair<string, string?> entry in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
                continue;
            string key = entry.Key.ToUpperInvariant();
            bool ambientOtelCredential = key.StartsWith("OTEL_EXPORTER_OTLP", StringComparison.Ordinal) &&
                (key.Contains("HEADER", StringComparison.Ordinal) ||
                 key.Contains("CERTIFICATE", StringComparison.Ordinal) ||
                 key.Contains("KEY", StringComparison.Ordinal));
            if (ambientOtelCredential || IsSensitiveObservabilityPath(entry.Key))
            {
                throw new OptionsValidationException(nameof(ObservabilityOptions), typeof(ObservabilityOptions), [$"Observability credential/header setting '{entry.Key}' is not accepted."]);
            }
        }
    }

    private static bool IsSensitiveObservabilityPath(string key)
    {
        string[] segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals("SqlObserver", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("Observability", StringComparison.OrdinalIgnoreCase))
            return false;

        for (int index = 2; index < segments.Length; index++)
        {
            string segment = segments[index].Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
            if (segment is "header" or "headers" or "certificate" or "certificates" or "cert" or
                "clientcertificate" or "clientcertificates" or "clientkey" or "privatekey" or
                "key" or "keys" or "credential" or "credentials" or "secret" or "secrets" or
                "token" or "password" or "apikey" or "authorization" or "bearer" ||
                segment.EndsWith("header", StringComparison.Ordinal) ||
                segment.EndsWith("headers", StringComparison.Ordinal) ||
                segment.EndsWith("certificate", StringComparison.Ordinal) ||
                segment.EndsWith("certificates", StringComparison.Ordinal) ||
                segment.EndsWith("credential", StringComparison.Ordinal) ||
                segment.EndsWith("credentials", StringComparison.Ordinal) ||
                segment.EndsWith("clientkey", StringComparison.Ordinal) ||
                segment.EndsWith("privatekey", StringComparison.Ordinal) ||
                segment.EndsWith("keymaterial", StringComparison.Ordinal) ||
                segment.EndsWith("keyfile", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>A bounded, fail-closed repository readiness observation.</summary>
public sealed record RepositoryReadinessObservation(
    bool IsReady,
    int PostgreSqlMajorVersion,
    PostgreSqlCompatibilityStatus Compatibility,
    DateTimeOffset CheckedAtUtc);

public interface IRepositoryReadinessMonitor
{
    ValueTask<RepositoryReadinessObservation> CheckAsync(RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}

/// <summary>Checks readiness through the existing compatibility port; no second SQL path is introduced.</summary>
public sealed class PostgreSqlRepositoryReadinessMonitor : IRepositoryReadinessMonitor
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(5);
    private static readonly ActivitySource Activities = new(ObservabilityContract.ActivitySourceName);
    private static readonly Meter Metrics = new(ObservabilityContract.MeterName);
    private static readonly Counter<long> Checks = Metrics.CreateCounter<long>("sqlobserver.repository.readiness.checks");
    private static readonly Histogram<double> ReadinessDuration = Metrics.CreateHistogram<double>("sqlobserver.service.readiness.duration", "ms");
    private static readonly Counter<long> ReadinessFailures = Metrics.CreateCounter<long>("sqlobserver.service.readiness.failures");
    private readonly IPostgreSqlCompatibilityPort _compatibility;

    public PostgreSqlRepositoryReadinessMonitor(IPostgreSqlCompatibilityPort compatibility) =>
        _compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));

    public async ValueTask<RepositoryReadinessObservation> CheckAsync(RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        TimeSpan effectiveTimeout = timeout.Value <= MaximumTimeout ? timeout.Value : MaximumTimeout;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);
        RepositoryCallTimeout boundedTimeout = new(effectiveTimeout);
        long started = Stopwatch.GetTimestamp();
        using Activity? activity = Activities.StartActivity("repository.readiness", ActivityKind.Internal);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.operation.name", "compatibility");
        Task<PostgreSqlCompatibilityResult>? compatibilityTask = null;
        try
        {
            compatibilityTask = _compatibility
                .CheckCompatibilityAsync(new PostgreSqlCompatibilityRequest(boundedTimeout), deadline.Token)
                .AsTask();
            PostgreSqlCompatibilityResult result = await compatibilityTask
                .WaitAsync(effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);
            int major = result.ServerVersion.Major;
            bool ready = result.IsCompatible &&
                result.Status == PostgreSqlCompatibilityStatus.Compatible &&
                major == 18;
            PostgreSqlCompatibilityStatus safeStatus = ready
                ? PostgreSqlCompatibilityStatus.Compatible
                : NormalizeStatus(result.Status, major);
            var observation = new RepositoryReadinessObservation(
                ready,
                major,
                safeStatus,
                result.CheckedAtUtc);
            activity?.SetTag("sqlobserver.repository.ready", observation.IsReady);
            activity?.SetTag("sqlobserver.repository.postgresql.major", observation.PostgreSqlMajorVersion);
            activity?.SetTag("sqlobserver.repository.compatibility", observation.Compatibility.ToString());
            Record(observation.IsReady, observation.Compatibility.ToString(), started);
            return observation;
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("sqlobserver.repository.ready", false);
            Record(false, "timeout", started);
            return new RepositoryReadinessObservation(false, 0, PostgreSqlCompatibilityStatus.RequiredCapabilityMissing, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("sqlobserver.repository.ready", false);
            Record(false, "timeout", started);
            return new RepositoryReadinessObservation(false, 0, PostgreSqlCompatibilityStatus.RequiredCapabilityMissing, DateTimeOffset.UtcNow);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("sqlobserver.repository.ready", false);
            Record(false, "unavailable", started);
            return new RepositoryReadinessObservation(false, 0, PostgreSqlCompatibilityStatus.RequiredCapabilityMissing, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (compatibilityTask is not null)
                _ = ObserveLateCompletionAsync(compatibilityTask);
        }
    }

    private static PostgreSqlCompatibilityStatus NormalizeStatus(PostgreSqlCompatibilityStatus status, int major) =>
        major != 18
            ? PostgreSqlCompatibilityStatus.UnsupportedMajorVersion
            : status == PostgreSqlCompatibilityStatus.Compatible
                ? PostgreSqlCompatibilityStatus.RequiredCapabilityMissing
                : status;

    private static async Task ObserveLateCompletionAsync(Task<PostgreSqlCompatibilityResult> task)
    {
        try
        {
            _ = await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A compatibility port that ignores cancellation must not leak a
            // late fault into the process after readiness has failed closed.
        }
    }

    private static void Record(bool ready, string status, long started)
    {
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        milliseconds = Math.Clamp(milliseconds, 0, ObservabilityContract.MaximumReadinessDurationMilliseconds);
        var tags = new TagList { { "ready", ready ? "true" : "false" }, { "status", status } };
        Checks.Add(1, tags);
        ReadinessDuration.Record(milliseconds, tags);
        if (!ready)
            ReadinessFailures.Add(1, new TagList { { "status", status } });
    }
}
