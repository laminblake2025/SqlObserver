using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Separate v3 capability assets; v1 and v2 bundles remain immutable and unopened.</summary>
public sealed class SqlServerCapabilityV3AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.CapabilityV3Assets.";
    public const string ExpectedBundleChecksum = "54eeb9f20404e8e9610994c140702347f45a43a64eb7405c47bbe618d5f95bba";
    private static readonly string[] Names = ["capability.connection.v3.schema.json", "capability.connection.v3.json", "capability.connection.sqlserver15-windows.v3.sql", "capability.connection.sqlserver16-windows.v3.sql", "capability.connection.sqlserver17-windows.v3.sql"];
    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServerCapabilityV3AssetCatalog(IReadOnlyDictionary<string, string> assets, string checksum) { this.assets = assets; BundleChecksum = checksum; }
    public string BundleChecksum { get; }
    public int ManifestVersion => assets.Count == Names.Length ? 3 : throw new InvalidDataException("Capability v3 asset catalog is incomplete.");
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public string Get(string name) => assets.TryGetValue(name, out string? value) ? value : throw new KeyNotFoundException("Capability v3 asset is not registered.");
    public string GetSupportedQuery(int major) => Get(major switch { 15 => "capability.connection.sqlserver15-windows.v3.sql", 16 => "capability.connection.sqlserver16-windows.v3.sql", 17 => "capability.connection.sqlserver17-windows.v3.sql", _ => throw new ArgumentOutOfRangeException(nameof(major)) });
    public static SqlServerCapabilityV3AssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerCapabilityV3AssetCatalog).Assembly;
        byte[] checksum = Read(assembly, "capability.connection.assets-v3.sha256");
        if (checksum.Length == 0 || checksum.Contains((byte)'\r') || checksum[0] is 0xEF or 0xFF or 0xFE) throw new InvalidDataException("Capability v3 checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksum).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("Capability v3 checksum manifest has an unexpected entry count.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64 || !parts[0].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("Capability v3 checksum manifest order or digest shape is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException($"Capability v3 asset {Names[i]} must be UTF-8 LF without BOM.");
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Capability v3 asset checksum failed for {Names[i]}.");
            loaded.Add(Names[i], Encoding.UTF8.GetString(bytes));
        }
        using JsonDocument schema = JsonDocument.Parse(loaded[Names[0]]);
        using JsonDocument manifest = JsonDocument.Parse(loaded[Names[1]]);
        ValidateManifest(schema.RootElement, manifest.RootElement);
        string bundleChecksum = Convert.ToHexString(SHA256.HashData(checksum)).ToLowerInvariant();
        if (!string.Equals(bundleChecksum, ExpectedBundleChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capability v3 bundle checksum is not the approved runtime asset bundle.");
        }
        return new SqlServerCapabilityV3AssetCatalog(loaded, bundleChecksum);
    }

    private static void ValidateManifest(JsonElement schema, JsonElement manifest)
    {
        if (schema.GetProperty("title").GetString() is not "SqlObserver passive capability discovery manifest v3" ||
            manifest.GetProperty("$schema").GetString() is not "capability.connection.v3.schema.json" ||
            manifest.GetProperty("schemaVersion").GetInt32() != 3 ||
            manifest.GetProperty("collectorId").GetString() is not "capability.connection" ||
            manifest.GetProperty("collectorVersion").GetInt32() != 3 ||
            manifest.GetProperty("outputSchemaVersion").GetInt32() != 3 ||
            manifest.GetProperty("operationalMode").GetString() is not "passive")
        {
            throw new InvalidDataException("Capability v3 manifest identity or schema parity is invalid.");
        }

        string[] features = manifest.GetProperty("features").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        if (!features.SequenceEqual(["feature.replication", "feature.host-binding"]))
        {
            throw new InvalidDataException("Capability v3 feature parity is invalid.");
        }

        JsonElement queries = manifest.GetProperty("queryResources").GetProperty("supportedByMajor");
        foreach (int major in new[] { 15, 16, 17 })
        {
            string expected = $"capability.connection.sqlserver{major}-windows.v3.sql";
            if (queries.GetProperty(major.ToString(CultureInfo.InvariantCulture)).GetString() != expected)
            {
                throw new InvalidDataException("Capability v3 query-resource parity is invalid.");
            }
        }
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException($"Capability v3 asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Capability v3 asset exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
