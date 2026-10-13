using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Pinned Windows SQL Server 2016/2017 core and activity variants; historical bundles stay immutable.</summary>
public sealed class SqlServer2016AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.SqlServer2016Assets.";
    private const string ChecksumName = "sqlserver2016.compatibility.assets-v1.sha256";
    public const string ExpectedBundleChecksum = "1a0e3c0639bd0ef14c90a3b961fded7f622ba0ab6147c01a5f0d0d05295e5790";
    private static readonly string[] Names =
    [
        "sqlserver2016.compatibility.v1.json",
        "collector-manifest.sqlserver2016.core.v1.schema.json",
        "collector-manifest.sqlserver2016.activity.v1.schema.json",
        "capability.connection.bootstrap-2016-compatible.v1.sql",
        "capability.connection.sqlserver13-windows.v4.sql",
        "capability.connection.sqlserver14-windows.v4.sql",
        "engine.core.sqlserver13-windows.v1.sql", "engine.core.sqlserver14-windows.v1.sql",
        "database.inventory.sqlserver13-windows.v1.sql", "database.inventory.sqlserver14-windows.v1.sql",
        "database.inventory.sqlserver13-windows.v2.sql", "database.inventory.sqlserver14-windows.v2.sql",
        "database.files.sqlserver13-windows.v1.sql", "database.files.sqlserver14-windows.v1.sql",
        "activity.sessions.sqlserver13-windows.v1.sql", "activity.sessions.sqlserver14-windows.v1.sql",
        "activity.requests.sqlserver13-windows.v1.sql", "activity.requests.sqlserver14-windows.v1.sql",
        "waits.server.sqlserver13-windows.v1.sql", "waits.server.sqlserver14-windows.v1.sql",
        "blocking.current.sqlserver13-windows.v1.sql", "blocking.current.sqlserver14-windows.v1.sql",
    ];
    private static readonly string[] CollectorIds =
        ["engine.core", "database.inventory", "database.files", "activity.sessions", "activity.requests", "waits.server", "blocking.current"];
    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServer2016AssetCatalog(IReadOnlyDictionary<string, string> assets) => this.assets = assets;
    public string BootstrapSql => assets["capability.connection.bootstrap-2016-compatible.v1.sql"];
    public string GetCapabilitySql(int major) => major switch
    {
        13 => assets["capability.connection.sqlserver13-windows.v4.sql"],
        14 => assets["capability.connection.sqlserver14-windows.v4.sql"],
        _ => throw new ArgumentOutOfRangeException(nameof(major)),
    };

    public static SqlServer2016AssetCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServer2016AssetCatalog).Assembly;
        string[] actual = assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = Names.Append(ChecksumName).Select(name => Prefix + name).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("SQL Server 2016/2017 resource inventory is invalid.");
        byte[] checksum = Read(assembly, ChecksumName);
        if (Digest(checksum) != ExpectedBundleChecksum) throw new InvalidDataException("SQL Server 2016/2017 bundle digest is not approved.");
        string[] entries = Decode(checksum).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length != Names.Length) throw new InvalidDataException("SQL Server 2016/2017 checksum inventory is invalid.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            byte[] bytes = Read(assembly, Names[i]);
            if (entries[i] != Digest(bytes) + "  " + Names[i]) throw new InvalidDataException("SQL Server 2016/2017 asset digest or order is invalid.");
            loaded.Add(Names[i], Decode(bytes));
        }
        using JsonDocument config = JsonDocument.Parse(loaded[Names[0]]);
        JsonElement root = config.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            !root.GetProperty("engineMajors").EnumerateArray().Select(item => item.GetInt32()).SequenceEqual([13, 14]) ||
            root.GetProperty("platform").GetString() != "Windows" || root.GetProperty("mode").GetString() != "passive" ||
            root.GetProperty("capabilityContractVersion").GetInt32() != 4 ||
            !root.GetProperty("collectors").EnumerateArray().Select(item => item.GetString()).SequenceEqual(CollectorIds))
            throw new InvalidDataException("SQL Server 2016/2017 compatibility contract is invalid.");
        return new SqlServer2016AssetCatalog(loaded);
    }

    public SqlServerCollectorAsset Extend(SqlServerCollectorAsset current)
    {
        ArgumentNullException.ThrowIfNull(current);
        CollectorManifest manifest = current.Manifest;
        if (!CollectorIds.Contains(manifest.Id.Value, StringComparer.Ordinal) ||
            manifest.SupportedVersions.MinimumMajor != 15 || manifest.SupportedVersions.MaximumMajor != 17)
            throw new InvalidDataException("Only verified core and activity collectors may receive SQL Server 2016/2017 variants.");
        int version = manifest.Id.Value == "database.inventory" ? manifest.ManifestVersion.Value : 1;
        var permissions = manifest.RequiredPermissions.Concat(manifest.RequiredPermissions
            .Where(item => item.ApplicableVersions.Contains(15))
            .Select(item => new CollectorPermissionRequirement(item.PermissionId, item.Scope, new SqlServerMajorVersionRange(13, 14)))).ToArray();
        var extended = new CollectorManifest(manifest.Id, manifest.DisplayName, manifest.ManifestVersion,
            manifest.RequiredCapabilities, permissions, new SqlServerMajorVersionRange(13, 17), manifest.SupportedPlatforms,
            manifest.Intervals, manifest.Limits, manifest.Fallback, manifest.OutputSchemaVersion, manifest.OperationalMode,
            manifest.SupportedEngineEditions, manifest.DependsOn, manifest.Resilience, manifest.OutputKind);
        JsonNode json = JsonNode.Parse(current.ManifestJson)!;
        string kind = CollectorIds.Take(3).Contains(manifest.Id.Value, StringComparer.Ordinal) ? "core" : "activity";
        json["$schema"] = $"collector-manifest.sqlserver2016.{kind}.v1.schema.json";
        json["supportedTargets"]!["minimumMajorVersion"] = 13;
        var queries = new Dictionary<int, string> { [15] = current.GetQuery(15), [16] = current.GetQuery(16), [17] = current.GetQuery(17) };
        foreach (int major in new[] { 13, 14 })
        {
            string resource = $"{manifest.Id.Value}.sqlserver{major}-windows.v{version}.sql";
            if (!assets.TryGetValue(resource, out string? query)) throw new InvalidDataException("SQL Server 2016/2017 has no exact query variant for this collector.");
            json["requiredPermissionsByMajor"]![major.ToString(System.Globalization.CultureInfo.InvariantCulture)] = json["requiredPermissionsByMajor"]!["15"]!.DeepClone();
            json["queryResources"]!["supportedByMajor"]![major.ToString(System.Globalization.CultureInfo.InvariantCulture)] = resource;
            queries.Add(major, query);
        }
        string manifestJson = json.ToJsonString() + "\n";
        return new SqlServerCollectorAsset(extended, manifestJson, Digest(Encoding.UTF8.GetBytes(manifestJson)), queries);
    }

    public static string ComposeBundleChecksum(string currentChecksum) => Digest(Encoding.ASCII.GetBytes(currentChecksum + "\n" + ExpectedBundleChecksum + "\n"));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException("SQL Server 2016/2017 assets require LF UTF-8 without BOM.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException("SQL Server 2016/2017 resource is missing.");
        if (stream.Length > 64 * 1024) throw new InvalidDataException("SQL Server 2016/2017 resource exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
