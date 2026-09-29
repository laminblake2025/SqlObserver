using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Loads the immutable, disabled-by-default server configuration collector contract.</summary>
public sealed class SqlServerConfigurationAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.ConfigurationAssets.";
    public const string ExpectedBundleChecksum = "0eb242b28bd428bbcb5bc3ea146c6fa8739226fbc779da470e431832a156a4c9";
    private static readonly string[] Names =
    [
        "engine.configurations.v1.schema.json",
        "engine.configurations.v1.json",
        "engine.configurations.sqlserver15-windows.v1.sql",
        "engine.configurations.sqlserver16-windows.v1.sql",
        "engine.configurations.sqlserver17-windows.v1.sql",
    ];
    private readonly IReadOnlyDictionary<string, string> _assets;
    private readonly string _bundleChecksum;

    private SqlServerConfigurationAssetCatalog(IReadOnlyDictionary<string, string> assets, string bundleChecksum)
    {
        _assets = assets;
        _bundleChecksum = bundleChecksum;
    }

    public string BundleChecksum => _bundleChecksum;
    public string ManifestJson => _assets[Names[1]];

    public static SqlServerConfigurationAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerConfigurationAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "engine.configurations.assets-v1.sha256");
        if (checksumBytes.Contains((byte)'\r') ||
            checksumBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
            !Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(ExpectedBundleChecksum,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Server configuration asset bundle checksum is not approved.");
        string[] lines = new UTF8Encoding(false, true).GetString(checksumBytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length)
            throw new InvalidDataException("Server configuration asset bundle is incomplete.");

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64)
                throw new InvalidDataException("Server configuration asset order or digest is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') ||
                bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[0],
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Server configuration asset {Names[i]} failed its pinned checksum.");
            assets.Add(Names[i], new UTF8Encoding(false, true).GetString(bytes));
        }

        using JsonDocument manifest = JsonDocument.Parse(assets[Names[1]]);
        JsonElement root = manifest.RootElement;
        JsonElement queries = root.GetProperty("queryResources");
        if (root.GetProperty("$schema").GetString() != Names[0] ||
            root.GetProperty("schemaVersion").GetInt32() != 6 ||
            root.GetProperty("collectorId").GetString() != "engine.configurations" ||
            root.GetProperty("collectorVersion").GetInt32() != 1 ||
            root.GetProperty("outputSchemaVersion").GetInt32() != 1 ||
            root.GetProperty("outputKind").GetString() != "server-configurations" ||
            root.GetProperty("defaultEnabled").GetBoolean() ||
            queries.GetProperty("15").GetString() != Names[2] ||
            queries.GetProperty("16").GetString() != Names[3] ||
            queries.GetProperty("17").GetString() != Names[4])
            throw new InvalidDataException("Server configuration collector identity or rollout status is invalid.");
        return new SqlServerConfigurationAssetCatalog(assets, ExpectedBundleChecksum);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        string prefix = name.EndsWith(".sql", StringComparison.Ordinal) ?
            "SqlObserver.Infrastructure.SqlServer.ConfigurationSourceAssets." : Prefix;
        using Stream stream = assembly.GetManifestResourceStream(prefix + name)
            ?? throw new InvalidDataException($"Server configuration asset {name} is missing.");
        if (stream.Length is <= 0 or > 4_194_304)
            throw new InvalidDataException("Server configuration asset exceeds its byte bound.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
