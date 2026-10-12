using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged assets only; loading these does not register or activate a collector.</summary>
internal sealed class SqlServerQueryPerformanceV3AssetCatalog
{
    internal const string BundleChecksum = "8a707984375b0d29ca144ded142b836c0ece121d419508601e64179169a631e8";
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.QueryPerformanceV3Assets.";
    private static readonly string[] Paths = [
        "collectors/manifests/queries.performance.v3.json",
        "collectors/manifests/queries.performance.v3.schema.json",
        "collectors/manifests/m7-query-performance.assets.sha256",
        "collectors/plan-cache-runtime/assets.sha256",
        "collectors/plan-cache-identity/read.v1.sql",
        "collectors/plan-cache-content/assets.sha256",
    ];
    private SqlServerQueryPerformanceV3AssetCatalog(SqlServerCollectorAsset asset,
        SqlServerQueryPerformanceCollectorAssetCatalog queryStore)
    { Asset = asset; QueryStore = queryStore; }
    internal SqlServerCollectorAsset Asset { get; }
    internal SqlServerQueryPerformanceCollectorAssetCatalog QueryStore { get; }

    internal static SqlServerQueryPerformanceV3AssetCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServerQueryPerformanceV3AssetCatalog).Assembly;
        byte[] index = Read("m7-query-performance-v3.assets.sha256");
        byte[] manifest = Read("queries.performance.v3.json"), schema = Read("queries.performance.v3.schema.json");
        SqlServerQueryPerformanceCollectorAssetCatalog queryStore = SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(assembly);
        _ = PlanCacheRuntimeSourceBundle.LoadEmbedded(assembly);
        _ = PlanCacheDatabaseIdentitySource.LoadEmbedded();
        _ = PlanCacheContentSourceBundle.LoadEmbedded();
        Validate(index, manifest, schema, queryStore.BundleChecksum);
        string json = new UTF8Encoding(false, true).GetString(manifest);
        var parsed = SqlServerQueryPerformanceCollectorAssetCatalog.ParseManifest(json);
        var queries = new Dictionary<int, string>();
        foreach (int major in new[] { 15, 16, 17 }) queries.Add(major, queryStore.Asset.GetQuery(major));
        return new(new SqlServerCollectorAsset(parsed.Manifest, json, Digest(manifest), queries), queryStore);

        byte[] Read(string name)
        {
            using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
                ?? throw new InvalidDataException("Query performance v3 asset is missing.");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
    }

    internal static void Validate(byte[] index, byte[] manifest, byte[] schema, string queryStoreChecksum)
    {
        if (Digest(index) != BundleChecksum) throw new InvalidDataException("Query performance v3 bundle is not approved.");
        string[] lines = new UTF8Encoding(false, true).GetString(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] digests = [Digest(manifest), Digest(schema), queryStoreChecksum,
            PlanCacheRuntimeSourceBundle.ExpectedBundleChecksum, PlanCacheDatabaseIdentitySource.ExpectedSqlChecksum,
            PlanCacheContentSourceBundle.ExpectedBundleChecksum];
        if (lines.Length != Paths.Length) throw new InvalidDataException("Query performance v3 asset inventory is invalid.");
        for (int i = 0; i < Paths.Length; i++)
            if (lines[i] != digests[i] + "  " + Paths[i]) throw new InvalidDataException("Query performance v3 nested asset differs from its approved pin.");
        using var manifestDocument = JsonDocument.Parse(manifest);
        using var schemaDocument = JsonDocument.Parse(schema);
        if (!JsonElement.DeepEquals(manifestDocument.RootElement, schemaDocument.RootElement.GetProperty("const")))
            throw new InvalidDataException("Query performance v3 manifest differs from its exact schema.");
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
