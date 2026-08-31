using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Checksum-pinned M9 assets. The v1 capability bundle is deliberately not read or modified.</summary>
public sealed class SqlServerOperationalHealthAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.M9OperationalHealthAssets.";
    private static readonly string[] Names =
    [
        "collector-manifest.v5.schema.json", "backups.status.v1.json", "sql-agent.failures.v1.json",
        "tempdb.health.v1.json", "availability-groups.health.v1.json",
        "backups.status.sqlserver15-windows.v1.sql", "backups.status.sqlserver16-windows.v1.sql", "backups.status.sqlserver17-windows.v1.sql",
        "sql-agent.failures.sqlserver15-windows.v1.sql", "sql-agent.failures.sqlserver16-windows.v1.sql", "sql-agent.failures.sqlserver17-windows.v1.sql",
        "tempdb.health.sqlserver15-windows.v1.sql", "tempdb.health.sqlserver16-windows.v1.sql", "tempdb.health.sqlserver17-windows.v1.sql",
        "availability-groups.health.sqlserver15-windows.v1.sql", "availability-groups.health.sqlserver16-windows.v1.sql", "availability-groups.health.sqlserver17-windows.v1.sql"
    ];

    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServerOperationalHealthAssetCatalog(IReadOnlyDictionary<string, string> assets, string checksum) { this.assets = assets; BundleChecksum = checksum; }
    public string BundleChecksum { get; }
    public IReadOnlyList<string> AssetNames => assets.Keys.ToArray();
    public string Get(string name) => assets.TryGetValue(name, out string? value) ? value : throw new KeyNotFoundException("M9 asset is not registered.");
    public string Get(SqlObserver.Domain.Capabilities.CollectorId collectorId) => Get(collectorId.Value switch { "backups.status" => "backups.status.v1.json", "sql-agent.failures" => "sql-agent.failures.v1.json", "tempdb.health" => "tempdb.health.v1.json", "availability-groups.health" => "availability-groups.health.v1.json", _ => throw new KeyNotFoundException("M9 collector is not registered.") });

    public static SqlServerOperationalHealthAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerOperationalHealthAssetCatalog).Assembly;
        byte[] manifest = Read(assembly, "m9-operational-health.assets.sha256");
        string[] lines = Encoding.UTF8.GetString(manifest).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("M9 asset manifest has an unexpected entry count.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] parts = lines[i].Split("  ", StringSplitOptions.None);
            if (parts.Length != 2 || parts[1] != Names[i]) throw new InvalidDataException("M9 asset manifest order is invalid.");
            byte[] bytes = Read(assembly, Names[i]);
            string checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!checksum.Equals(parts[0], StringComparison.Ordinal)) throw new InvalidDataException($"M9 asset checksum failed for {Names[i]}.");
            if (bytes.AsSpan().Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException("M9 asset must be UTF-8 LF without BOM.");
            loaded.Add(Names[i], Encoding.UTF8.GetString(bytes));
        }
        return new SqlServerOperationalHealthAssetCatalog(loaded, Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant());
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException($"M9 asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("M9 asset exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
