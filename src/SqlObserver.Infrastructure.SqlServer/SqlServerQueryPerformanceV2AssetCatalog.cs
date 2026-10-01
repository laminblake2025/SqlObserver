using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Staged assets only; loading these does not register or activate a collector.</summary>
internal sealed class SqlServerQueryPerformanceV2AssetCatalog
{
    internal const string BundleChecksum = "2674c820b6b5e2ff6c2485e06b313b4b11eed446b216ca2cfa8e09c9ea310c4c";
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.QueryPerformanceV2Assets.";
    private static readonly string[] Paths = [
        "collectors/manifests/queries.performance.v2.json",
        "collectors/manifests/queries.performance.v2.schema.json",
        "collectors/manifests/m7-query-performance.assets.sha256",
        "collectors/plan-cache-runtime/assets.sha256",
        "collectors/plan-cache-identity/read.v1.sql",
    ];
    private SqlServerQueryPerformanceV2AssetCatalog(SqlServerCollectorAsset asset,
        SqlServerQueryPerformanceCollectorAssetCatalog queryStore)
    { Asset = asset; QueryStore = queryStore; }
    internal SqlServerCollectorAsset Asset { get; }
    internal SqlServerQueryPerformanceCollectorAssetCatalog QueryStore { get; }

    internal static SqlServerQueryPerformanceV2AssetCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServerQueryPerformanceV2AssetCatalog).Assembly;
        byte[] index = Read("m7-query-performance-v2.assets.sha256");
        byte[] manifest = Read("queries.performance.v2.json"), schema = Read("queries.performance.v2.schema.json");
        SqlServerQueryPerformanceCollectorAssetCatalog queryStore = SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded(assembly);
        _ = PlanCacheRuntimeSourceBundle.LoadEmbedded(assembly);
        _ = PlanCacheDatabaseIdentitySource.LoadEmbedded();
        Validate(index, manifest, schema, queryStore.BundleChecksum);
        string json = new UTF8Encoding(false, true).GetString(manifest);
        var parsed = SqlServerQueryPerformanceCollectorAssetCatalog.ParseManifest(json);
        var queries = new Dictionary<int, string>();
        foreach (int major in new[] { 15, 16, 17 }) queries.Add(major, queryStore.Asset.GetQuery(major));
        return new(new SqlServerCollectorAsset(parsed.Manifest, json, Digest(manifest), queries), queryStore);

        byte[] Read(string name)
        {
            using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
                ?? throw new InvalidDataException("Query performance v2 asset is missing.");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
    }

    internal static void Validate(byte[] index, byte[] manifest, byte[] schema, string queryStoreChecksum)
    {
        if (Digest(index) != BundleChecksum) throw new InvalidDataException("Query performance v2 bundle is not approved.");
        string[] lines = new UTF8Encoding(false, true).GetString(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] digests = [Digest(manifest), Digest(schema), queryStoreChecksum,
            PlanCacheRuntimeSourceBundle.ExpectedBundleChecksum, PlanCacheDatabaseIdentitySource.ExpectedSqlChecksum];
        if (lines.Length != Paths.Length) throw new InvalidDataException("Query performance v2 asset inventory is invalid.");
        for (int i = 0; i < Paths.Length; i++)
            if (lines[i] != digests[i] + "  " + Paths[i]) throw new InvalidDataException("Query performance v2 nested asset differs from its approved pin.");
        using var manifestDocument = JsonDocument.Parse(manifest);
        using var schemaDocument = JsonDocument.Parse(schema);
        if (!JsonElement.DeepEquals(manifestDocument.RootElement, schemaDocument.RootElement.GetProperty("const")))
            throw new InvalidDataException("Query performance v2 manifest differs from its exact schema.");
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
