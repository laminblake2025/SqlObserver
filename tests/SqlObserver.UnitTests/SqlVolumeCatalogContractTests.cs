using System.Security.Cryptography;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlVolumeCatalogContractTests
{
    [Fact]
    public void PinnedVolumeBundleMatchesDisabledManifestAndSource()
    {
        SqlServerVolumeCapacityAssetCatalog catalog = SqlServerVolumeCapacityAssetCatalog.LoadEmbedded();
        Assert.Equal(SqlServerVolumeCapacityAssetCatalog.ExpectedBundleChecksum, catalog.BundleChecksum);
        Assert.Equal(3, catalog.AssetNames.Count);
        Assert.Contains("\"defaultEnabled\":false", catalog.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("\"server.view-any-definition\"", catalog.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_volume_stats", catalog.QuerySql, StringComparison.OrdinalIgnoreCase);

        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SqlObserver.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException();
        string checksumPath = Path.Combine(root, "collectors", "manifests", "storage.volume.assets-v1.sha256");
        Assert.Equal(catalog.BundleChecksum,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(checksumPath))).ToLowerInvariant());
    }
}
