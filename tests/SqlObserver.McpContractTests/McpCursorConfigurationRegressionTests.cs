using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

public sealed class McpCursorConfigurationRegressionTests
{
    private const string ConfigurationKey = "SqlObserver:Mcp:CursorSigningKey";

    [Fact]
    public void HierarchicalConfigurationRegistersSignerWithoutLegacyEnvironment()
    {
        using IHost host = Host(Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()), out _);
        Assert.NotNull(host.Services.GetService<McpCursorSigner>());
    }

    [Fact]
    public void LegacyEnvironmentConfigurationKeyRemainsSupported()
    {
        using IHost host = Host(null, out _, Convert.ToBase64String(new byte[32]));
        Assert.NotNull(host.Services.GetService<McpCursorSigner>());
    }

    [Fact]
    public void MalformedPreferredKeyDoesNotFallBackToLegacyKey()
    {
        using IHost host = Host("not-base64-secret", out _, Convert.ToBase64String(new byte[32]));
        Assert.Null(host.Services.GetService<McpCursorSigner>());
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(32, true)]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void ConfiguredKeyRetainsDecodedByteBounds(int length, bool supported)
    {
        using IHost host = Host(Convert.ToBase64String(new byte[length]), out _);
        Assert.Equal(supported, host.Services.GetService<McpCursorSigner>() is not null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64-secret")]
    [InlineData("c2hvcnQ=")]
    public async Task UnavailableSignerWarnsOnceWithoutLoggingSecret(string? configuredKey)
    {
        using IHost host = Host(configuredKey, out RecordingLogger logs);
        await host.StartAsync();
        await host.StopAsync();
        Assert.Null(host.Services.GetService<McpCursorSigner>());
        string message = Assert.Single(logs.Warnings);
        Assert.Contains("cursor", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuation", message, StringComparison.OrdinalIgnoreCase);
        if (configuredKey is not null) Assert.DoesNotContain(configuredKey, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSignerRegistrationDoesNotWarn(bool registerBeforeComposition)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        IServiceCollection services = builder.Services;
        var logs = new RecordingLogger();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var signer = new McpCursorSigner(new byte[32]);
        if (registerBeforeComposition) services.AddSingleton(signer);
        services.AddSqlObserverMcp();
        if (!registerBeforeComposition) services.AddSingleton(signer);
        using IHost host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();
        Assert.Same(signer, host.Services.GetService<McpCursorSigner>());
        Assert.Empty(logs.Warnings);
    }

    private static IHost Host(string? configuredKey, out RecordingLogger logger, string? legacy = null)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        IServiceCollection services = builder.Services;
        logger = new RecordingLogger();
        RecordingLogger captured = logger;
        services.AddLogging(builder => builder.AddProvider(captured));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ConfigurationKey] = configuredKey, ["SQLOBSERVER_MCP_CURSOR_KEY"] = legacy }).Build());
        services.AddSqlObserverMcp();
        return builder.Build();
    }

    private sealed class RecordingLogger : ILoggerProvider, ILogger
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception)); }
        public void Dispose() { }
    }
}
