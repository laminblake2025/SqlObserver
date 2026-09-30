using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Verified, staged backup v2 source; the active M9 collector still uses v1.</summary>
public sealed class SqlServerBackupV2AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.M9BackupV2Assets.";
    private const string ExpectedBundleChecksum = "b567ab905ded0118ee10c6f22f77189f8e7979f738e79f5c8ab3362cb76bbcf7";
    private static readonly string[] Names =
    [
        "collector-manifest.v7.schema.json",
        "backups.status.v2.json",
        "backups.status.sqlserver15-windows.v2.sql",
        "backups.status.sqlserver16-windows.v2.sql",
        "backups.status.sqlserver17-windows.v2.sql",
    ];

    private readonly IReadOnlyDictionary<string, string> assets;

    private SqlServerBackupV2AssetCatalog(IReadOnlyDictionary<string, string> assets)
    {
        this.assets = assets;
    }

    public string Schema => assets["collector-manifest.v7.schema.json"];

    public string Manifest => assets["backups.status.v2.json"];

    public string GetQuery(int major) => assets[major switch
    {
        15 => "backups.status.sqlserver15-windows.v2.sql",
        16 => "backups.status.sqlserver16-windows.v2.sql",
        17 => "backups.status.sqlserver17-windows.v2.sql",
        _ => throw new ArgumentOutOfRangeException(nameof(major)),
    }];

    public static SqlServerBackupV2AssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerBackupV2AssetCatalog).Assembly;
        byte[] bundle = Read(assembly, "m9-backups-v2.assets.sha256");
        string digest = Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
        if (digest != ExpectedBundleChecksum)
            throw new InvalidDataException("The backup v2 asset bundle is not approved.");
        string[] entries = DecodeLf(bundle).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length != Names.Length)
            throw new InvalidDataException("The backup v2 asset list has an unexpected length.");

        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            string[] entry = entries[i].Split("  ", StringSplitOptions.None);
            if (entry.Length != 2 || entry[1] != Names[i] || entry[0].Length != 64)
                throw new InvalidDataException("The backup v2 asset list has an unexpected entry.");
            byte[] bytes = Read(assembly, Names[i]);
            string checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (checksum != entry[0])
                throw new InvalidDataException($"backup v2 asset checksum failed for {Names[i]}.");
            loaded.Add(Names[i], DecodeLf(bytes));
        }
        return new SqlServerBackupV2AssetCatalog(loaded);
    }

    private static string DecodeLf(byte[] bytes)
    {
        if (bytes.AsSpan().Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new InvalidDataException("backup v2 assets must be UTF-8 LF without BOM.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException($"backup v2 asset {name} is missing.");
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("backup v2 asset exceeds its byte bound.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
