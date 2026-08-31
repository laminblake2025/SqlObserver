using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Ports;
using SqlObserver.Observability;

namespace SqlObserver.UnitTests;

public sealed class M12ObservabilityContractTests
{
    [Fact]
    public void EndpointPolicyRequiresHttpsExceptContractTestingLoopback()
    {
        Assert.True(OtlpEndpointPolicy.IsAllowed(new Uri("https://telemetry.example/v1/otlp"), false));
        Assert.False(OtlpEndpointPolicy.IsAllowed(new Uri("http://telemetry.example/v1/otlp"), true));
        Assert.True(OtlpEndpointPolicy.IsAllowed(new Uri("http://127.0.0.1:4318"), true));
        Assert.False(OtlpEndpointPolicy.IsAllowed(new Uri("https://user:secret@telemetry.example"), false));
        Assert.False(OtlpEndpointPolicy.IsAllowed(new Uri("https://telemetry.example/v1/otlp?token=secret"), false));
    }

    [Fact]
    public async Task ReadinessMonitorFailsClosedOnCompatibilityFailure()
    {
        var monitor = new PostgreSqlRepositoryReadinessMonitor(new FailingCompatibilityPort());
        RepositoryReadinessObservation result = await monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Equal(0, result.PostgreSqlMajorVersion);
    }

    [Fact]
    public async Task ReadinessMonitorInternallyCapsTimeoutAtFiveSeconds()
    {
        var port = new CapturingCompatibilityPort();
        var monitor = new PostgreSqlRepositoryReadinessMonitor(port);
        _ = await monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(5), port.Timeout);
    }

    [Fact]
    public async Task ReadinessMonitorRejectsCompatibleFlagForPostgreSql17()
    {
        var monitor = new PostgreSqlRepositoryReadinessMonitor(new FixedCompatibilityPort(
            new PostgreSqlCompatibilityResult(new PostgreSqlVersion(17, 6), PostgreSqlCompatibilityStatus.Compatible, DateTimeOffset.UtcNow)));
        RepositoryReadinessObservation result = await monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Equal(17, result.PostgreSqlMajorVersion);
        Assert.Equal(PostgreSqlCompatibilityStatus.UnsupportedMajorVersion, result.Compatibility);
    }

    [Fact]
    public async Task ReadinessMonitorNormalizesInconsistentCompatibilityStatus()
    {
        var monitor = new PostgreSqlRepositoryReadinessMonitor(new FixedCompatibilityPort(
            new PostgreSqlCompatibilityResult(new PostgreSqlVersion(18, 4), PostgreSqlCompatibilityStatus.RequiredCapabilityMissing, DateTimeOffset.UtcNow)));
        RepositoryReadinessObservation result = await monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(1)), CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.Equal(PostgreSqlCompatibilityStatus.RequiredCapabilityMissing, result.Compatibility);
    }

    [Fact]
    public async Task ReadinessMonitorBoundsNonCooperativeCompatibilityPort()
    {
        var port = new NonCooperativeCompatibilityPort();
        var monitor = new PostgreSqlRepositoryReadinessMonitor(port);
        Stopwatch clock = Stopwatch.StartNew();
        RepositoryReadinessObservation result = await monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromMilliseconds(100)), CancellationToken.None);
        Assert.False(result.IsReady);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        port.Complete();
    }

    [Fact]
    public async Task ReadinessMonitorPreservesExternalCancellation()
    {
        var monitor = new PostgreSqlRepositoryReadinessMonitor(new CancellationCompatibilityPort());
        using var cancellation = new CancellationTokenSource();
        Task<RepositoryReadinessObservation> check = monitor.CheckAsync(new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await check);
    }

    [Fact]
    public void RegistrationUsesExactHostServiceNames()
    {
        var configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = new();
        services.AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<ObservabilityOptions>));
        Assert.Throws<ArgumentException>(() => services.AddSqlObserverObservability(configuration, "SqlObserver.Other"));
    }

    [Fact]
    public void RegistrationRejectsAmbientHeadersAndCertificateMaterial()
    {
        foreach (string key in new[]
        {
            "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_EXPORTER_OTLP_TRACES_HEADERS", "OTEL_EXPORTER_OTLP_METRICS_HEADERS", "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
            "OTEL_EXPORTER_OTLP_CERTIFICATE", "OTEL_EXPORTER_OTLP_TRACES_CERTIFICATE", "OTEL_EXPORTER_OTLP_METRICS_CERTIFICATE", "OTEL_EXPORTER_OTLP_LOGS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_KEY", "OTEL_EXPORTER_OTLP_TRACES_CLIENT_KEY", "OTEL_EXPORTER_OTLP_METRICS_CLIENT_KEY", "OTEL_EXPORTER_OTLP_LOGS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_TRACES_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_METRICS_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_LOGS_PRIVATE_KEY",
        })
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://telemetry.example",
                    [key] = "secret",
                })
                .Build();
            Assert.Throws<OptionsValidationException>(() => new ServiceCollection().AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName));
        }
    }

    [Fact]
    public void RegistrationRejectsNestedObservabilityCredentialDescendants()
    {
        foreach (string key in new[]
        {
            "SqlObserver:Observability:Headers:Authorization",
            "SqlObserver:Observability:LogsHeaders:token",
            "SqlObserver:Observability:ExportCertificate:ClientKey",
            "SqlObserver:Observability:Transport:Private_Key",
            "sQLoBsErVeR:obServability:Telemetry:Authorization",
        })
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SqlObserver:Observability:OtlpEndpoint"] = "https://telemetry.example",
                    [key] = "secret",
                })
                .Build();
            Assert.Throws<OptionsValidationException>(() => new ServiceCollection().AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName));
        }
    }

    [Fact]
    public void RegistrationAllowsCredentialFreeObservabilityEndpoint()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlObserver:Observability:OtlpEndpoint"] = "https://telemetry.example/v1/otlp",
            })
            .Build();

        ServiceCollection services = new();
        services.AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<ObservabilityOptions>));
    }

    [Fact]
    public void RegistrationRejectsNonHttpsServiceSpecificEndpoints()
    {
        foreach (string key in new[] { "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" })
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "http://telemetry.example" })
                .Build();
            Assert.Throws<OptionsValidationException>(() => new ServiceCollection().AddSqlObserverObservability(configuration, ObservabilityContract.ServerServiceName));
        }
    }

    private sealed class FailingCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        public ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<PostgreSqlCompatibilityResult>(new InvalidOperationException("unavailable"));
    }

    private sealed class CapturingCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        public TimeSpan Timeout { get; private set; }

        public ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken)
        {
            Timeout = request.Timeout.Value;
            return ValueTask.FromResult(new PostgreSqlCompatibilityResult(
                new PostgreSqlVersion(18, 4),
                PostgreSqlCompatibilityStatus.Compatible,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FixedCompatibilityPort(PostgreSqlCompatibilityResult result) : IPostgreSqlCompatibilityPort
    {
        public ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }

    private sealed class NonCooperativeCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        private readonly TaskCompletionSource<PostgreSqlCompatibilityResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken) =>
            new(_completion.Task);

        public void Complete() => _completion.TrySetException(new InvalidOperationException("late deterministic fault"));
    }

    private sealed class CancellationCompatibilityPort : IPostgreSqlCompatibilityPort
    {
        public async ValueTask<PostgreSqlCompatibilityResult> CheckCompatibilityAsync(PostgreSqlCompatibilityRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
