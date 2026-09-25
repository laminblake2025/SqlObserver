using System.Security.Cryptography;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class CapabilityV4ContractTests
{
    [Fact]
    public void EmbeddedV4BundlePinsMetadataPermissionAndReusesV3Queries()
    {
        SqlServerCapabilityV4AssetCatalog catalog = SqlServerCapabilityV4AssetCatalog.LoadEmbedded();

        Assert.Equal(4, catalog.ManifestVersion);
        Assert.Equal(SqlServerCapabilityV4AssetCatalog.ExpectedBundleChecksum, catalog.BundleChecksum);
        Assert.Equal(5, catalog.AssetNames.Count);
        Assert.Contains("\"metadataPermissions\":[\"server.view-any-definition\"]",
            catalog.Get("capability.connection.v4.json"), StringComparison.Ordinal);
        foreach (int major in new[] { 15, 16, 17 })
        {
            Assert.Equal(SqlServerCapabilityV3AssetCatalog.LoadEmbedded().GetSupportedQuery(major),
                catalog.GetSupportedQuery(major));
        }

        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SqlObserver.slnx")))
        {
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException();
        }
        byte[] checksum = File.ReadAllBytes(Path.Combine(root, "collectors", "manifests", "capability.connection.assets-v4.sha256"));
        Assert.Equal(SqlServerCapabilityV4AssetCatalog.ExpectedBundleChecksum,
            Convert.ToHexString(SHA256.HashData(checksum)).ToLowerInvariant());
    }
}
