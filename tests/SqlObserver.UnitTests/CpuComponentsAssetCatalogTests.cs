using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Collector;
using SqlObserver.Collectors;

namespace SqlObserver.UnitTests;

public sealed class CpuComponentsAssetCatalogTests
{
    [Fact]
    public async Task ProductionRegistrationMatchesPinnedDisabledByDefaultContract()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var assets = provider.GetRequiredService<CpuComponentsAssetCatalog>();
        var registration = Assert.Single(provider.GetRequiredService<CollectorRegistry>().Registrations,
            item => item.Manifest.Id.Value == "cpu.components");
        var runtime = registration.Manifest;
        using var document = JsonDocument.Parse(assets.ManifestJson);
        var manifest = document.RootElement;
        Assert.Equal(CpuComponentsAssetCatalog.ExpectedBundleChecksum, assets.BundleChecksum);
        Assert.Equal(runtime.Id.Value, manifest.GetProperty("collectorId").GetString());
        Assert.Equal(runtime.DisplayName.Value, manifest.GetProperty("displayName").GetString());
        Assert.Equal(runtime.ManifestVersion.Value, manifest.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(runtime.OutputSchemaVersion.Value, manifest.GetProperty("outputSchemaVersion").GetInt32());
        Assert.Equal(runtime.Limits.MaxRows, manifest.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
        Assert.Equal(runtime.Limits.MaxResponseBytes, manifest.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32());
        Assert.Equal(runtime.Limits.CommandTimeout.TotalSeconds, manifest.GetProperty("executionBounds").GetProperty("commandTimeoutSeconds").GetInt32());
        Assert.Equal(runtime.Intervals.DefaultInterval.TotalSeconds, manifest.GetProperty("cadence").GetProperty("defaultIntervalSeconds").GetInt32());
        Assert.Equal(runtime.Intervals.HardMinimumInterval.TotalSeconds, manifest.GetProperty("cadence").GetProperty("minimumIntervalSeconds").GetInt32());
        Assert.False(manifest.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(runtime.DependsOn.Select(item => item.Value), manifest.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("cpu-components", manifest.GetProperty("outputKind").GetString());
        Assert.Contains("sys.dm_exec_connections", assets.QuerySql, StringComparison.Ordinal);
    }
}
