using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class ServerLifecycleAssetCatalogTests
{
    [Fact]
    public async Task ProductionRegistrationMatchesPinnedDefaultOffManifestAndUnchangedSource()
    {
        await using var provider = Provider();
        var catalog = provider.GetRequiredService<ServerLifecycleAssetCatalog>();
        var registration = provider.GetRequiredService<CollectorRegistry>().GetRequired(new("server.lifecycle"));
        var runtime = registration.Manifest;
        using var document = JsonDocument.Parse(catalog.ManifestJson);
        var manifest = document.RootElement;
        Assert.Equal(21, registration.ExecutionOrder);
        Assert.IsType<SqlServerLifecycleCollector>(registration.Collector);
        Assert.Equal(CollectorOutputKind.ServerLifecycle, runtime.OutputKind);
        Assert.Equal(ServerLifecycleAssetCatalog.ExpectedBundleChecksum, registration.AssetBundleDigest.Value);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.ManifestJson))).ToLowerInvariant(), registration.ManifestDigest.Value);
        Assert.False(manifest.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(6, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(runtime.Id.Value, manifest.GetProperty("collectorId").GetString());
        Assert.Equal(runtime.DisplayName.Value, manifest.GetProperty("displayName").GetString());
        Assert.Equal(runtime.ManifestVersion.Value, manifest.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(runtime.OutputSchemaVersion.Value, manifest.GetProperty("outputSchemaVersion").GetInt32());
        Assert.Equal("server-lifecycle", manifest.GetProperty("outputKind").GetString());
        Assert.Equal("passive", manifest.GetProperty("operationalMode").GetString());
        Assert.Equal(CollectorOperationalMode.Passive, runtime.OperationalMode);
        Assert.Equal(runtime.DependsOn.Select(p => p.Value), manifest.GetProperty("dependsOn").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(runtime.RequiredCapabilities.Select(p => p.Value), manifest.GetProperty("requiredCapabilities").EnumerateArray().Select(p => p.GetString()));
        Assert.All(runtime.RequiredPermissions, p => Assert.Equal(PermissionEvidenceScope.Server, p.Scope));
        for (int major = 15; major <= 17; major++)
            Assert.Equal(runtime.RequiredPermissions.Where(p => p.ApplicableVersions.Contains(major)).Select(p => p.PermissionId.Value),
                manifest.GetProperty("requiredPermissionsByMajor").GetProperty(major.ToString(CultureInfo.InvariantCulture)).EnumerateArray().Select(p => p.GetString()));
        var target = manifest.GetProperty("supportedTargets");
        Assert.Equal(runtime.SupportedVersions.MinimumMajor, target.GetProperty("minimumMajorVersion").GetInt32());
        Assert.Equal(runtime.SupportedVersions.MaximumMajor, target.GetProperty("maximumMajorVersion").GetInt32());
        Assert.Equal(runtime.SupportedPlatforms.Select(p => p.ToString()), target.GetProperty("platforms").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(runtime.SupportedEngineEditions.Select(p => (int)p), target.GetProperty("engineEditions").EnumerateArray().Select(p => p.GetInt32()));
        var limits = manifest.GetProperty("executionBounds");
        Assert.Equal(runtime.Limits.ConnectTimeout.TotalSeconds, limits.GetProperty("connectTimeoutSeconds").GetInt32());
        Assert.Equal(runtime.Limits.CommandTimeout.TotalSeconds, limits.GetProperty("commandTimeoutSeconds").GetInt32());
        Assert.Equal(runtime.Limits.MaxRows, limits.GetProperty("maximumRows").GetInt32());
        Assert.Equal(runtime.Limits.MaxResponseBytes, limits.GetProperty("maximumResponseBytes").GetInt32());
        Assert.Equal(runtime.Limits.EstimatedCost.ToString().ToLowerInvariant(), manifest.GetProperty("estimatedCostClass").GetString());
        var cadence = manifest.GetProperty("cadence");
        Assert.Equal(runtime.Intervals.DefaultInterval.TotalSeconds, cadence.GetProperty("defaultIntervalSeconds").GetInt32());
        Assert.Equal(runtime.Intervals.HardMinimumInterval.TotalSeconds, cadence.GetProperty("minimumIntervalSeconds").GetInt32());
        Assert.True(cadence.GetProperty("nonOverlappingPerTarget").GetBoolean());
        var resilience = manifest.GetProperty("resilience");
        Assert.Equal(runtime.Resilience.MaximumAttempts, resilience.GetProperty("maximumAttempts").GetInt32());
        Assert.Equal(runtime.Resilience.TransientRetryDelay.TotalMilliseconds, resilience.GetProperty("transientRetryDelayMilliseconds").GetInt32());
        Assert.Equal(runtime.Resilience.CircuitFailureThreshold, resilience.GetProperty("circuitFailureThreshold").GetInt32());
        Assert.Equal(runtime.Resilience.CircuitOpenDuration.TotalSeconds, resilience.GetProperty("circuitOpenSeconds").GetInt32());
        Assert.Equal(runtime.Fallback.Mode.ToString().ToLowerInvariant(), manifest.GetProperty("fallback").GetProperty("mode").GetString());
        Assert.Equal(SqlServerLifecycleCollector.MaximumRows, registration.OutputValidator.Contract.MaxOperationalHealthObservations);
        foreach (int major in new[] { 15, 16, 17 })
            Assert.Equal(provider.GetRequiredService<SqlServerLifecycleAssetCatalog>().GetQuery(major), catalog.QuerySql);
        Assert.Equal("ff1ac5e01a9cd64f5be0302ac4a5f2195418e3dc8cfbf6f0ae7097d16a6eaa45",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.QuerySql))).ToLowerInvariant());
    }

    [Fact]
    public async Task LifecycleRegistrationRejectsWrongOrderAndForeignOutputContract()
    {
        await using var provider = Provider();
        var registry = provider.GetRequiredService<CollectorRegistry>();
        var lifecycle = registry.GetRequired(new("server.lifecycle"));
        var wrongOrder = new CollectorRegistration(22, lifecycle.Collector, lifecycle.OutputValidator, lifecycle.ManifestDigest, lifecycle.AssetBundleDigest);
        Assert.Throws<ArgumentException>(() => new CollectorRegistry(registry.Registrations.Where(r => r != lifecycle).Append(wrongOrder).ToArray()));
        Assert.Throws<ArgumentException>(() => new CollectorRegistration(21, lifecycle.Collector,
            new CollectorOutputValidator(CpuComponentsCollector.OutputContract), lifecycle.ManifestDigest, lifecycle.AssetBundleDigest));
    }

    private static ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlObserverRepository"] = "Host=127.0.0.1;Port=1;Database=composition_only;Username=sqlobserver_collector",
            ["SqlObserver:IdentityFingerprintKey"] = new string('a', 64),
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddSqlObserverCollectorRuntime(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
