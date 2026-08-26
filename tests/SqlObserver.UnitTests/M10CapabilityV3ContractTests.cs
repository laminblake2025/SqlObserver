using System.Security.Cryptography;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class M10CapabilityV3ContractTests
{
    private static readonly string[] AssetNames =
    [
        "capability.connection.v3.schema.json",
        "capability.connection.v3.json",
        "capability.connection.sqlserver15-windows.v3.sql",
        "capability.connection.sqlserver16-windows.v3.sql",
        "capability.connection.sqlserver17-windows.v3.sql",
    ];
    private static readonly int[] SupportedMajors = [15, 16, 17];

    [Fact]
    public void EmbeddedBundleIsTheApprovedFiveAssetCapabilityV3Contract()
    {
        SqlServerCapabilityV3AssetCatalog catalog = SqlServerCapabilityV3AssetCatalog.LoadEmbedded();

        Assert.Equal(3, catalog.ManifestVersion);
        Assert.Equal(SqlServerCapabilityV3AssetCatalog.ExpectedBundleChecksum, catalog.BundleChecksum);
        Assert.Equal(AssetNames, catalog.AssetNames);
        Assert.Equal(
            SqlServerCapabilityV3AssetCatalog.ExpectedBundleChecksum,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FindRootFile("collectors/manifests/capability.connection.assets-v3.sha256")))).ToLowerInvariant());
    }

    [Fact]
    public void ManifestAndQueriesKeepTheV3FeatureAndMajorAllowlist()
    {
        SqlServerCapabilityV3AssetCatalog catalog = SqlServerCapabilityV3AssetCatalog.LoadEmbedded();
        string manifest = catalog.Get("capability.connection.v3.json");

        Assert.Contains("\"features\":[\"feature.replication\",\"feature.host-binding\"]", manifest, StringComparison.Ordinal);
        Assert.Contains("capability.connection.sqlserver15-windows.v3.sql", manifest, StringComparison.Ordinal);
        Assert.Contains("capability.connection.sqlserver16-windows.v3.sql", manifest, StringComparison.Ordinal);
        Assert.Contains("capability.connection.sqlserver17-windows.v3.sql", manifest, StringComparison.Ordinal);
        Assert.All(SupportedMajors, major => Assert.Contains($"SELECT", catalog.GetSupportedQuery(major), StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRootFile(string relativePath)
    {
        string path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "SqlObserver.slnx")))
        {
            path = Directory.GetParent(path)?.FullName ?? throw new DirectoryNotFoundException();
        }

        return Path.Combine(path, relativePath);
    }
}
