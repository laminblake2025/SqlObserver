using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Checksum-pinned SQL Server 2022 group collector catalog.</summary>
public sealed class SqlServerQueryStoreGroupAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.QueryStoreGroupedSourceAssets.";
    public const string ExpectedBundleChecksum =
        "4d6c86e95950d0a60c72d7876283c505058b96ee25a102cf967aa8ffa220aa8a";
    private static readonly string[] Names =
    [
        "queries.performance-groups.v1.schema.json",
        "queries.performance-groups.v1.json",
        "runtime.sqlserver16-windows.v1.sql",
        "waits.sqlserver16-windows.v1.sql",
    ];

    private SqlServerQueryStoreGroupAssetCatalog(string manifestJson,
        string manifestChecksum)
    {
        ManifestJson = manifestJson;
        ManifestChecksum = manifestChecksum;
    }

    public string ManifestJson { get; }
    public string ManifestChecksum { get; }

    public static SqlServerQueryStoreGroupAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerQueryStoreGroupAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "collector.assets-v1.sha256");
        if (checksumBytes.Contains((byte)'\r') ||
            checksumBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(
                ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Query Store group collector bundle digest is not approved.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length)
            throw new InvalidDataException("Query Store group collector asset count changed.");

        var bytes = new byte[Names.Length][];
        for (int index = 0; index < Names.Length; index++)
        {
            string[] parts = lines[index].Split("  ", StringSplitOptions.None);
            bytes[index] = Read(assembly, Names[index]);
            if (parts.Length != 2 || parts[1] != Names[index] ||
                bytes[index].Contains((byte)'\r') ||
                bytes[index].AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
                !Convert.ToHexString(SHA256.HashData(bytes[index])).Equals(
                    parts[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Query Store group collector asset checksum changed.");
        }

        string schema = new UTF8Encoding(false, true).GetString(bytes[0]);
        string manifest = new UTF8Encoding(false, true).GetString(bytes[1]);
        ValidateSchemaAndManifest(schema, manifest);
        return new SqlServerQueryStoreGroupAssetCatalog(manifest,
            Convert.ToHexString(SHA256.HashData(bytes[1])).ToLowerInvariant());
    }

    private static void ValidateSchemaAndManifest(string schema, string manifest)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(schema);
        using JsonDocument manifestDocument = JsonDocument.Parse(manifest);
        JsonElement shape = schemaDocument.RootElement;
        JsonElement root = manifestDocument.RootElement;
        JsonElement target = root.GetProperty("supportedTargets");
        JsonElement cadence = root.GetProperty("cadence");
        JsonElement bounds = root.GetProperty("executionBounds");
        JsonElement resources = root.GetProperty("queryResources");
        JsonElement permissions = root.GetProperty("requiredPermissionsByMajor");
        if (shape.GetProperty("$schema").GetString() !=
                "https://json-schema.org/draft/2020-12/schema" ||
            root.GetProperty("$schema").GetString() != Names[0] ||
            root.GetProperty("schemaVersion").GetInt32() != 6 ||
            root.GetProperty("collectorId").GetString() != "queries.performance-groups" ||
            root.GetProperty("collectorVersion").GetInt32() != 1 ||
            root.GetProperty("outputSchemaVersion").GetInt32() != 1 ||
            root.GetProperty("outputKind").GetString() != "query-store-groups" ||
            root.GetProperty("defaultEnabled").GetBoolean() ||
            root.GetProperty("operationalMode").GetString() != "passive" ||
            target.GetProperty("minimumMajorVersion").GetInt32() != 16 ||
            target.GetProperty("maximumMajorVersion").GetInt32() != 16 ||
            cadence.GetProperty("defaultIntervalSeconds").GetInt32() != 1800 ||
            cadence.GetProperty("minimumIntervalSeconds").GetInt32() != 300 ||
            !cadence.GetProperty("nonOverlappingPerTarget").GetBoolean() ||
            bounds.GetProperty("connectTimeoutSeconds").GetInt32() != 5 ||
            bounds.GetProperty("commandTimeoutSeconds").GetInt32() != 300 ||
            bounds.GetProperty("maximumRows").GetInt32() != 2000 ||
            bounds.GetProperty("maximumResponseBytes").GetInt32() != 8 * 1024 * 1024 ||
            resources.GetProperty("runtime").GetString() != Names[2] ||
            resources.GetProperty("waits").GetString() != Names[3] ||
            permissions.GetProperty("16")[0].GetString() !=
                "database.view-performance-state")
            throw new InvalidDataException("Query Store group collector manifest is not the reviewed SQL Server 2022 contract.");
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException("Query Store group collector asset is missing.");
        if (stream.Length is <= 0 or > 4_194_304)
            throw new InvalidDataException("Query Store group collector asset exceeds its byte bound.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
