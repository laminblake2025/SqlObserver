using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Collector;

/// <summary>Loads the immutable, disabled-by-default CPU component collector contract.</summary>
public sealed class CpuComponentsAssetCatalog
{
    private const string Prefix = "SqlObserver.Collector.CpuComponentsAssets.";
    public const string ExpectedBundleChecksum = "650fdd81cc50ed82c5d9e3db2f1685a276556b4d416e61f2c2359c9bb5f70116";
    private static readonly string[] Names =
    [
        "cpu.components.v1.schema.json",
        "cpu.components.v1.json",
        "cpu.target-witness.sqlserver15-17-windows.v2.sql",
    ];
    private readonly IReadOnlyDictionary<string, string> _assets;
    private readonly string _bundleChecksum;

    private CpuComponentsAssetCatalog(IReadOnlyDictionary<string, string> assets, string bundleChecksum)
    {
        _assets = assets;
        _bundleChecksum = bundleChecksum;
    }

    public string BundleChecksum => _bundleChecksum;
    public IReadOnlyList<string> AssetNames => _assets.Keys.ToArray();
    public string ManifestJson => _assets[Names[1]];
    public string QuerySql => _assets[Names[2]];

    public static CpuComponentsAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(CpuComponentsAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "cpu.components.assets-v1.sha256");
        if (checksumBytes.Length == 0 || checksumBytes.Contains((byte)'\r') ||
            checksumBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidDataException("CPU component checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length ||
            !Convert.ToHexString(SHA256.HashData(checksumBytes)).Equals(ExpectedBundleChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CPU component asset bundle checksum is not approved.");

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64)
                throw new InvalidDataException("CPU component asset manifest order or digest is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ||
                !Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"CPU component asset {Names[i]} failed its pinned checksum.");
            assets.Add(Names[i], new UTF8Encoding(false, true).GetString(bytes));
        }

        using JsonDocument manifest = JsonDocument.Parse(assets[Names[1]]);
        JsonElement root = manifest.RootElement;
        if (root.GetProperty("$schema").GetString() != Names[0] ||
            root.GetProperty("schemaVersion").GetInt32() != 6 ||
            root.GetProperty("collectorId").GetString() != "cpu.components" ||
            root.GetProperty("collectorVersion").GetInt32() != 1 ||
            root.GetProperty("outputSchemaVersion").GetInt32() != 1 ||
            root.GetProperty("queryResource").GetString() != Names[2] ||
            root.GetProperty("defaultEnabled").GetBoolean())
            throw new InvalidDataException("CPU component collector manifest identity or rollout status is invalid.");
        return new CpuComponentsAssetCatalog(assets, ExpectedBundleChecksum);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException($"CPU component asset {name} is missing.");
        if (stream.Length is <= 0 or > 4_194_304)
            throw new InvalidDataException("CPU component asset exceeds its byte bound.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
