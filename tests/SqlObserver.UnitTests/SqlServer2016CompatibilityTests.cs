using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServer2016CompatibilityTests
{
    [Theory]
    [InlineData(13)]
    [InlineData(14)]
    public void ExactCoreAndActivityVariantsPreserveNewerEvidenceAndBounds(int major)
    {
        var original = SqlServerCollectorAssetCatalog.LoadEmbedded().Collectors
            .Concat(SqlServerActivityCollectorAssetCatalog.LoadEmbedded().Collectors);
        var catalog = SqlServer2016AssetCatalog.LoadEmbedded();
        foreach (var asset in original)
        {
            var extended = catalog.Extend(asset);
            Assert.Equal(13, extended.Manifest.SupportedVersions.MinimumMajor);
            Assert.Equal(asset.Manifest.Limits, extended.Manifest.Limits);
            Assert.Equal(asset.Manifest.Intervals, extended.Manifest.Intervals);
            Assert.Equal(asset.Manifest.OutputSchemaVersion, extended.Manifest.OutputSchemaVersion);
            Assert.Equal(asset.Manifest.DependsOn, extended.Manifest.DependsOn);
            foreach (int newer in new[] { 15, 16, 17 }) Assert.Equal(asset.GetQuery(newer), extended.GetQuery(newer));
            Assert.Throws<ArgumentOutOfRangeException>(() => extended.GetQuery(12));
            Assert.Throws<ArgumentOutOfRangeException>(() => extended.GetQuery(18));
            Assert.Contains("TOP (@maximum_rows)", extended.GetQuery(major), StringComparison.Ordinal);
            Assert.Contains(extended.Manifest.RequiredPermissions, p => p.ApplicableVersions.Contains(major) && p.PermissionId.Value == "server.view-state");
            Assert.DoesNotContain(extended.Manifest.RequiredPermissions, p => p.ApplicableVersions.Contains(major) && p.PermissionId.Value == "server.view-performance-state");
            using var json = JsonDocument.Parse(extended.ManifestJson);
            Assert.Contains($"sqlserver{major}-windows", json.RootElement.GetProperty("queryResources").GetProperty("supportedByMajor").GetProperty(major.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString()!, StringComparison.Ordinal);
        }
        string engine = SqlServerCollectorAssetCatalog.LoadWithSqlServer2016Support().Get(new("engine.core")).GetQuery(major);
        Assert.Contains("scheduler.total_cpu_usage_ms", engine, StringComparison.Ordinal);
        Assert.Contains("DATEDIFF_BIG", engine, StringComparison.Ordinal);
        Assert.Contains("log_reuse_wait_desc", SqlServerCollectorAssetCatalog.LoadVersion2WithSqlServer2016Support().Get(new("database.inventory")).GetQuery(major), StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryAvoidsUnavailable2016HostDmvAndPinsActual2017Version()
    {
        var catalog = SqlServer2016AssetCatalog.LoadEmbedded();
        Assert.DoesNotContain("sys.dm_os_host_info", catalog.GetCapabilitySql(13), StringComparison.Ordinal);
        Assert.Contains("= 13 AND", catalog.GetCapabilitySql(13), StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_host_info", catalog.GetCapabilitySql(14), StringComparison.Ordinal);
        Assert.Contains("=14;", catalog.GetCapabilitySql(14), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.GetCapabilitySql(12));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public async Task TamperedContractFailsBeforeAnyRepositoryConnection(int index, bool bundle)
    {
        var core = SqlServerCollectorAssetCatalog.LoadWithSqlServer2016Support();
        var activity = SqlServerActivityCollectorAssetCatalog.LoadWithSqlServer2016Support();
        var entries = core.Collectors.Select(a => (Asset:a, Bundle:core.BundleChecksum))
            .Concat(activity.Collectors.Select(a => (Asset:a, Bundle:activity.BundleChecksum)))
            .Select((a, i) => new CollectorCatalogEntry(i + 1, a.Asset.Manifest, new(a.Asset.ManifestChecksum), new(a.Bundle))).ToArray();
        var original = entries[index];
        entries[index] = new(original.ExecutionOrder, original.Manifest,
            bundle ? original.ManifestDigest : new(new string('0', 64)),
            bundle ? new(new string('0', 64)) : original.AssetBundleDigest);
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Timeout=1");
        var request = new ReconcileCollectorCatalogRequest(entries,
            new(new("collector/catalog/reconcile"), new(Guid.NewGuid()), new(1)), new(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgreSqlCollectorRuntimeRepositoryPort(source).ReconcileCatalogAsync(request, default).AsTask());
    }
}
