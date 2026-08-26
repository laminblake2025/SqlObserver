using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Loads the fixed M10 replication bundle and rejects resource/checksum drift.</summary>
public sealed class SqlServerReplicationAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.M10ReplicationAssets.";
    private static readonly string[] Names =
    [
        "replication.health.v1.schema.json", "replication.health.v1.json",
        "replication.health.sqlserver15-windows.v1.sql", "replication.health.sqlserver16-windows.v1.sql", "replication.health.sqlserver17-windows.v1.sql"
    ];
    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServerReplicationAssetCatalog(IReadOnlyDictionary<string, string> assets, string checksum) { this.assets = assets; BundleChecksum = checksum; }
    public string BundleChecksum { get; }
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public string Get(string name) => assets.TryGetValue(name, out string? value) ? value : throw new KeyNotFoundException("M10 replication asset is not registered.");
    public string GetQuery(int major) => Get(major switch { 15 => "replication.health.sqlserver15-windows.v1.sql", 16 => "replication.health.sqlserver16-windows.v1.sql", 17 => "replication.health.sqlserver17-windows.v1.sql", _ => throw new ArgumentOutOfRangeException(nameof(major)) });
    public static SqlServerReplicationAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerReplicationAssetCatalog).Assembly;
        byte[] checksumBytes = Read(assembly, "m10-replication.assets.sha256");
        if (checksumBytes.Length == 0 || checksumBytes.Contains((byte)'\r') || checksumBytes[0] is 0xEF or 0xFF or 0xFE) throw new InvalidDataException("M10 checksum manifest must be UTF-8 LF without BOM.");
        string[] lines = Encoding.UTF8.GetString(checksumBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("M10 replication checksum manifest has an unexpected entry count.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i] || parts[0].Length != 64 || !parts[0].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("M10 replication checksum manifest order or digest shape is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException($"M10 replication asset {Names[i]} must be UTF-8 LF without BOM.");
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actual.Equals(parts[0], StringComparison.Ordinal)) throw new InvalidDataException($"M10 replication asset checksum failed for {Names[i]}.");
            loaded.Add(Names[i], Encoding.UTF8.GetString(bytes));
        }
        return new SqlServerReplicationAssetCatalog(loaded, Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant());
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException($"M10 replication asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("M10 replication asset exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
