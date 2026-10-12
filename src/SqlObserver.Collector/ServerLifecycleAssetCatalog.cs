using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Collector;

/// <summary>Loads the immutable, disabled-by-default server lifecycle collector contract.</summary>
public sealed class ServerLifecycleAssetCatalog
{
    private const string Prefix = "SqlObserver.Collector.ServerLifecycleAssets.";
    public const string ExpectedBundleChecksum = "fc783bea8bc4f56f76a9f8c7b84e46f2205c1f20b852275ef8676fc90ef658d2";
    private static readonly string[] Names =
    [
        "server.lifecycle.v1.schema.json",
        "server.lifecycle.v1.json",
        "server.lifecycle.sqlserver15-17-windows.v1.sql",
    ];
    private readonly IReadOnlyDictionary<string, string> _assets;
    private readonly string _bundleChecksum;

    private ServerLifecycleAssetCatalog(IReadOnlyDictionary<string, string> assets, string bundleChecksum)
    {
        _assets = assets;
        _bundleChecksum = bundleChecksum;
    }

    public string BundleChecksum => _bundleChecksum;
    public IReadOnlyList<string> AssetNames => _assets.Keys.ToArray();
    public string ManifestJson => _assets[Names[1]];
    public string QuerySql => _assets[Names[2]];

    public static ServerLifecycleAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(ServerLifecycleAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "server.lifecycle.assets-v1.sha256");
        if (checksumBytes.Length == 0 || checksumBytes.Contains((byte)'\r') ||
            checksumBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidDataException("server lifecycle checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length ||
            !Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("server lifecycle asset bundle checksum is not approved.");

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64)
                throw new InvalidDataException("server lifecycle asset manifest order or digest is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"server lifecycle asset {Names[i]} failed its pinned checksum.");
            assets.Add(Names[i], new UTF8Encoding(false, true).GetString(bytes));
        }

        using JsonDocument manifest = JsonDocument.Parse(assets[Names[1]]);
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("$schema").GetString() != Names[0] ||
            root.GetProperty("schemaVersion").GetInt32() != 6 ||
            root.GetProperty("collectorId").GetString() != "server.lifecycle" ||
            root.GetProperty("collectorVersion").GetInt32() != 1 ||
            root.GetProperty("outputSchemaVersion").GetInt32() != 1 ||
            root.GetProperty("queryResource").GetString() != Names[2] ||
            root.GetProperty("defaultEnabled").GetBoolean())
            throw new InvalidDataException("server lifecycle collector manifest identity or rollout status is invalid.");
        return new ServerLifecycleAssetCatalog(assets, ExpectedBundleChecksum);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException($"server lifecycle asset {name} is missing.");
        if (stream.Length is <= 0 or > 4_194_304)
            throw new InvalidDataException("server lifecycle asset exceeds its byte bound.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
