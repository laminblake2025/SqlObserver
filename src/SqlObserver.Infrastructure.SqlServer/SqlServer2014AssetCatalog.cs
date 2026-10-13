using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>An additive, pinned compatibility bundle. Historical bundles remain independently verified.</summary>
public sealed class SqlServer2014AssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.SqlServer2014Assets.";
    private const string ChecksumName = "sqlserver2014.compatibility.assets-v1.sha256";
    public const string ExpectedBundleChecksum = "5b4f62db7aefa15687bfc17c459c8a6cb2f44774c240627ff1a1bc99fd76cb83";
    private static readonly string[] Names =
    [
        "sqlserver2014.compatibility.v1.json",
        "collector-manifest.sqlserver2014.core.v1.schema.json",
        "collector-manifest.sqlserver2014.activity.v1.schema.json",
        "capability.connection.bootstrap-compatible.v1.sql",
        "capability.connection.sqlserver12-windows.v1.sql",
        "engine.core.sqlserver12-windows.v1.sql",
        "database.inventory.sqlserver12-windows.v1.sql",
        "database.inventory.sqlserver12-windows.v2.sql",
        "database.files.sqlserver12-windows.v1.sql",
        "activity.sessions.sqlserver12-windows.v1.sql",
        "activity.requests.sqlserver12-windows.v1.sql",
        "waits.server.sqlserver12-windows.v1.sql",
        "blocking.current.sqlserver12-windows.v1.sql",
    ];
    private static readonly string[] CollectorIds =
        ["engine.core", "database.inventory", "database.files", "activity.sessions", "activity.requests", "waits.server", "blocking.current"];
    private readonly IReadOnlyDictionary<string, string> assets;
    private SqlServer2014AssetCatalog(IReadOnlyDictionary<string, string> assets) => this.assets = assets;
    public string BootstrapSql => assets["capability.connection.bootstrap-compatible.v1.sql"];
    public string CapabilitySql => assets["capability.connection.sqlserver12-windows.v1.sql"];

    public static SqlServer2014AssetCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(SqlServer2014AssetCatalog).Assembly;
        string[] actual = assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = Names.Append(ChecksumName).Select(name => Prefix + name).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal)) throw new InvalidDataException("SQL Server 2014 resource inventory is invalid.");
        byte[] checksum = Read(assembly, ChecksumName);
        if (!Digest(checksum).Equals(ExpectedBundleChecksum, StringComparison.Ordinal)) throw new InvalidDataException("SQL Server 2014 bundle digest is not approved.");
        string[] entries = Decode(checksum).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length != Names.Length) throw new InvalidDataException("SQL Server 2014 checksum inventory is invalid.");
        var loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < Names.Length; i++)
        {
            byte[] bytes = Read(assembly, Names[i]);
            if (entries[i] != Digest(bytes) + "  " + Names[i]) throw new InvalidDataException("SQL Server 2014 asset digest or order is invalid.");
            loaded.Add(Names[i], Decode(bytes));
        }
        using JsonDocument config = JsonDocument.Parse(loaded[Names[0]]);
        JsonElement root = config.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("engineMajor").GetInt32() != 12 ||
            root.GetProperty("platform").GetString() != "Windows" || root.GetProperty("mode").GetString() != "passive" ||
            root.GetProperty("capabilityContractVersion").GetInt32() != 4 ||
            !root.GetProperty("collectors").EnumerateArray().Select(item => item.GetString()).SequenceEqual(CollectorIds))
            throw new InvalidDataException("SQL Server 2014 compatibility contract is invalid.");
        return new SqlServer2014AssetCatalog(loaded);
    }

    public SqlServerCollectorAsset Extend(SqlServerCollectorAsset current)
    {
        ArgumentNullException.ThrowIfNull(current);
        CollectorManifest manifest = current.Manifest;
        if (!CollectorIds.Contains(manifest.Id.Value, StringComparer.Ordinal) || manifest.SupportedVersions.MinimumMajor != 15 || manifest.SupportedVersions.MaximumMajor != 17)
            throw new InvalidDataException("Only verified core and activity collectors may receive SQL Server 2014 variants.");
        int version = manifest.Id.Value == "database.inventory" ? manifest.ManifestVersion.Value : 1;
        string resource = $"{manifest.Id.Value}.sqlserver12-windows.v{version}.sql";
        if (!assets.TryGetValue(resource, out string? query)) throw new InvalidDataException("SQL Server 2014 has no exact query variant for this collector.");
        var permissions = manifest.RequiredPermissions.Concat(
            manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(15))
                .Select(item => new CollectorPermissionRequirement(item.PermissionId, item.Scope, new SqlServerMajorVersionRange(12, 12)))).ToArray();
        var extended = new CollectorManifest(manifest.Id, manifest.DisplayName, manifest.ManifestVersion,
            manifest.RequiredCapabilities, permissions, new SqlServerMajorVersionRange(12, 17), manifest.SupportedPlatforms,
            manifest.Intervals, manifest.Limits, manifest.Fallback, manifest.OutputSchemaVersion, manifest.OperationalMode,
            manifest.SupportedEngineEditions, manifest.DependsOn, manifest.Resilience, manifest.OutputKind);
        JsonNode json = JsonNode.Parse(current.ManifestJson)!;
        string kind = CollectorIds.Take(3).Contains(manifest.Id.Value, StringComparer.Ordinal) ? "core" : "activity";
        json["$schema"] = $"collector-manifest.sqlserver2014.{kind}.v1.schema.json";
        json["supportedTargets"]!["minimumMajorVersion"] = 12;
        json["requiredPermissionsByMajor"]!["12"] = json["requiredPermissionsByMajor"]!["15"]!.DeepClone();
        json["queryResources"]!["supportedByMajor"]!["12"] = resource;
        string manifestJson = json.ToJsonString() + "\n";
        var queries = new Dictionary<int, string> { [12] = query, [15] = current.GetQuery(15), [16] = current.GetQuery(16), [17] = current.GetQuery(17) };
        return new SqlServerCollectorAsset(extended, manifestJson, Digest(Encoding.UTF8.GetBytes(manifestJson)), queries);
    }

    public static string ComposeBundleChecksum(string currentChecksum) => Digest(Encoding.ASCII.GetBytes(currentChecksum + "\n" + ExpectedBundleChecksum + "\n"));
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)'\r') || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) throw new InvalidDataException("SQL Server 2014 assets require LF UTF-8 without BOM.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException("SQL Server 2014 resource is missing.");
        if (stream.Length > 64 * 1024) throw new InvalidDataException("SQL Server 2014 resource exceeds its bound.");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
