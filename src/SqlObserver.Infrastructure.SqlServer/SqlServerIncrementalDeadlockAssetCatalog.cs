using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SqlObserver.Collector.Abstractions;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged v2 contract; production registration remains v1 until alert qualification.</summary>
public sealed class SqlServerIncrementalDeadlockAssetCatalog
{
    public const string ApprovedBundleChecksum = "efcd15c53d0a75b30277446730f774dc40ffe6fa4ff78c1435967de56bc954fa";
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.XeFileSourceAssets.";
    private SqlServerIncrementalDeadlockAssetCatalog(CollectorManifest manifest, string manifestChecksum, XeFileSourceBundle source)
    { Manifest = manifest; ManifestChecksum = manifestChecksum; Source = source; }
    public CollectorManifest Manifest { get; }
    public string ManifestChecksum { get; }
    public static string BundleChecksum => ApprovedBundleChecksum;
    internal XeFileSourceBundle Source { get; }

    public static SqlServerIncrementalDeadlockAssetCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServerIncrementalDeadlockAssetCatalog).Assembly;
        byte[] checksums = Read("collector.assets-v2.sha256", 1024), json = Read("collector.v2.json", 16_384);
        if (Hash(checksums) != ApprovedBundleChecksum) throw new InvalidDataException("Incremental deadlock bundle is not approved.");
        string digest = Hash(json);
        string expectedChecksums = $"{digest}  collector.v2.json\n{XeFileSourceBundle.ExpectedBundleChecksum}  assets.sha256\n";
        if (Encoding.UTF8.GetString(checksums) != expectedChecksums) throw new InvalidDataException("Incremental deadlock manifest checksum failed.");
        SqlServerDeadlockCollectorAssetCatalog baseline = SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded();
        JsonNode expected = JsonNode.Parse(baseline.Asset.ManifestJson)!;
        expected["collectorVersion"] = 2;
        expected["outputSchemaVersion"] = 2;
        expected["displayName"] = "SQL Server incremental system_health deadlock evidence";
        expected["executionBounds"]!["maximumRows"] = 8193;
        for (int major = 15; major <= 17; major++)
            expected["queryResources"]!["supportedByMajor"]![major.ToString(System.Globalization.CultureInfo.InvariantCulture)] = $"read.sqlserver{major}-windows.v1.sql";
        if (!JsonNode.DeepEquals(expected, JsonNode.Parse(json))) throw new InvalidDataException("Incremental deadlock contract differs from its reviewed bounds.");
        CollectorManifest prior = baseline.Asset.Manifest;
        var manifest = new CollectorManifest(prior.Id, new("SQL Server incremental system_health deadlock evidence"), new(2),
            prior.RequiredCapabilities, prior.RequiredPermissions, prior.SupportedVersions, prior.SupportedPlatforms, prior.Intervals,
            new(prior.Limits.ConnectTimeout, prior.Limits.CommandTimeout, 8193, prior.Limits.MaxResponseBytes, prior.Limits.EstimatedCost),
            prior.Fallback, new(2), prior.OperationalMode, prior.SupportedEngineEditions, prior.DependsOn, prior.Resilience, prior.OutputKind);
        return new(manifest, digest, XeFileSourceBundle.LoadEmbedded());

        byte[] Read(string name, int bound)
        {
            using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException("Incremental deadlock asset is missing.");
            if (stream.Length > bound) throw new InvalidDataException("Incremental deadlock asset exceeds its bound.");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
        static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    }
}
