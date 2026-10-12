using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>V4 adds a metadata-visibility permission probe without changing the v3 SQL row shape.</summary>
public sealed class SqlServerCapabilityV4AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.CapabilityV4Assets.";
    public const string ExpectedBundleChecksum = "1270d2249cf823816996ac89152b923bb605a95282f9089bdba8727d468b9c77";
    private static readonly string[] Names = ["capability.connection.v4.schema.json", "capability.connection.v4.json", "capability.connection.sqlserver15-windows.v3.sql", "capability.connection.sqlserver16-windows.v3.sql", "capability.connection.sqlserver17-windows.v3.sql"];
    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServerCapabilityV4AssetCatalog(IReadOnlyDictionary<string, string> assets, string checksum) { this.assets = assets; BundleChecksum = checksum; }
    public string BundleChecksum { get; }
    public int ManifestVersion => assets.Count == Names.Length ? 4 : throw new InvalidDataException("Capability v4 asset catalog is incomplete.");
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public string Get(string name) => assets.TryGetValue(name, out string? value) ? value : throw new KeyNotFoundException("Capability v4 asset is not registered.");
    public string GetSupportedQuery(int major) => Get(major switch { 15 => "capability.connection.sqlserver15-windows.v3.sql", 16 => "capability.connection.sqlserver16-windows.v3.sql", 17 => "capability.connection.sqlserver17-windows.v3.sql", _ => throw new ArgumentOutOfRangeException(nameof(major)) });
    public static SqlServerCapabilityV4AssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerCapabilityV4AssetCatalog).Assembly;
        byte[] checksum = Read(assembly, "capability.connection.assets-v4.sha256");
        if (checksum.Length == 0 || checksum.Contains((byte)'\r') || checksum[0] is 0xEF or 0xFF or 0xFE) throw new InvalidDataException("Capability v4 checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksum).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("Capability v4 checksum manifest has an unexpected entry count.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64 || !parts[0].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("Capability v4 checksum manifest order or digest shape is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException($"Capability v4 asset {Names[i]} must be UTF-8 LF without BOM.");
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(parts[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Capability v4 asset checksum failed for {Names[i]}.");
            loaded.Add(Names[i], Encoding.UTF8.GetString(bytes));
        }
        using JsonDocument schema = JsonDocument.Parse(loaded[Names[0]]);
        using JsonDocument manifest = JsonDocument.Parse(loaded[Names[1]]);
        ValidateManifest(schema.RootElement, manifest.RootElement);
        string bundleChecksum = Convert.ToHexString(SHA256.HashData(checksum)).ToLowerInvariant();
        if (!string.Equals(bundleChecksum, ExpectedBundleChecksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Capability v4 bundle checksum is not the approved runtime asset bundle.");
        }
        return new SqlServerCapabilityV4AssetCatalog(loaded, bundleChecksum);
    }

    private static void ValidateManifest(JsonElement schema, JsonElement manifest)
    {
        if (schema.GetProperty("title").GetString() is not "SqlObserver passive capability discovery manifest v4" ||
            manifest.GetProperty("$schema").GetString() is not "capability.connection.v4.schema.json" ||
            manifest.GetProperty("schemaVersion").GetInt32() != 4 ||
            manifest.GetProperty("collectorId").GetString() is not "capability.connection" ||
            manifest.GetProperty("collectorVersion").GetInt32() != 4 ||
            manifest.GetProperty("outputSchemaVersion").GetInt32() != 4 ||
            manifest.GetProperty("operationalMode").GetString() is not "passive")
        {
            throw new InvalidDataException("Capability v4 manifest identity or schema parity is invalid.");
        }

        string[] features = manifest.GetProperty("features").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        if (!features.SequenceEqual(["feature.replication", "feature.host-binding"]))
        {
            throw new InvalidDataException("Capability v4 feature parity is invalid.");
        }

        if (!manifest.GetProperty("metadataPermissions").EnumerateArray()
                .Select(static item => item.GetString()).SequenceEqual(["server.view-any-definition"]))
            throw new InvalidDataException("Capability v4 metadata permission parity is invalid.");

        JsonElement queries = manifest.GetProperty("queryResources").GetProperty("supportedByMajor");
        foreach (int major in new[] { 15, 16, 17 })
        {
            string expected = $"capability.connection.sqlserver{major}-windows.v3.sql";
            if (queries.GetProperty(major.ToString(CultureInfo.InvariantCulture)).GetString() != expected)
            {
                throw new InvalidDataException("Capability v4 query-resource parity is invalid.");
            }
        }
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        string resourcePrefix = name.EndsWith(".v3.sql", StringComparison.Ordinal)
            ? "SqlObserver.Infrastructure.SqlServer.CapabilityV3Assets."
            : Prefix;
        using Stream stream = assembly.GetManifestResourceStream(resourcePrefix + name) ?? throw new InvalidDataException($"Capability v4 asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Capability v4 asset exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
