using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.Windows;

/// <summary>Loads the fixed host.metrics manifest/schema bundle and verifies every byte.</summary>
public sealed class HostMetricsAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.Windows.M10HostAssets.";
    private static readonly string[] Names = ["host.metrics.v1.schema.json", "host.metrics.v1.json"];
    private readonly IReadOnlyDictionary<string, string> assets;

    private HostMetricsAssetCatalog(IReadOnlyDictionary<string, string> assets, string checksum)
    {
        this.assets = assets;
        BundleChecksum = checksum;
    }

    public string BundleChecksum { get; }
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public string Get(string name) => assets.TryGetValue(name, out string? value)
        ? value : throw new KeyNotFoundException("Host metrics asset is not registered.");

    public static HostMetricsAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(HostMetricsAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "m10-host.assets.sha256");
        if (checksumBytes.Length == 0 || checksumBytes.Contains((byte)'\r') || checksumBytes[0] is 0xEF or 0xFF or 0xFE)
            throw new InvalidDataException("Host metrics checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("Host metrics checksum manifest has an unexpected entry count.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64 || parts[0].Any(static c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                throw new InvalidDataException("Host metrics checksum manifest order or digest shape is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                throw new InvalidDataException($"Host metrics asset {Names[i]} must be UTF-8 LF without BOM.");
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actual.Equals(parts[0], StringComparison.Ordinal)) throw new InvalidDataException($"Host metrics asset checksum failed for {Names[i]}.");
            loaded.Add(Names[i], Encoding.UTF8.GetString(bytes));
        }
        return new HostMetricsAssetCatalog(loaded, Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant());
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException($"Host metrics asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Host metrics asset exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
