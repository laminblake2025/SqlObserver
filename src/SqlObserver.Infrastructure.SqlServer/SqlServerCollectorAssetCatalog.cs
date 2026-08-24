using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>A checksum-pinned v2 manifest and its exact version-selected passive queries.</summary>
public sealed class SqlServerCollectorAsset
{
    private readonly ReadOnlyDictionary<int, string> _queriesByMajor;

    internal SqlServerCollectorAsset(
        CollectorManifest manifest,
        string manifestJson,
        string manifestChecksum,
        IReadOnlyDictionary<int, string> queriesByMajor)
    {
        Manifest = manifest;
        ManifestJson = manifestJson;
        ManifestChecksum = manifestChecksum;
        _queriesByMajor = new ReadOnlyDictionary<int, string>(
            new Dictionary<int, string>(queriesByMajor));
    }

    public CollectorManifest Manifest { get; }

    public string ManifestJson { get; }

    public string ManifestChecksum { get; }

    public string GetQuery(int majorVersion)
    {
        if (!Manifest.SupportedVersions.Contains(majorVersion) ||
            !_queriesByMajor.TryGetValue(majorVersion, out string? query))
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }

        return query;
    }
}

/// <summary>
/// Loads the generic M4 manifests and fixed SQL from verified embedded bytes. The parser rejects
/// unknown fields and never accepts a runtime path, SQL override, or unchecked manifest.
/// </summary>
public sealed class SqlServerCollectorAssetCatalog
{
    private const string ResourcePrefix = "SqlObserver.Infrastructure.SqlServer.M4CollectorAssets.";
    private const string ChecksumFileName = "m4-core-health.assets.sha256";
    private const int MaximumAssetBytes = 4 * 1024 * 1024;

    private static readonly string[] ExpectedAssetNames =
    [
        "collector-manifest.v2.schema.json",
        "engine.core.v1.json",
        "database.inventory.v1.json",
        "database.files.v1.json",
        "engine.core.sqlserver15-windows.v1.sql",
        "engine.core.sqlserver16-windows.v1.sql",
        "engine.core.sqlserver17-windows.v1.sql",
        "database.inventory.sqlserver15-windows.v1.sql",
        "database.inventory.sqlserver16-windows.v1.sql",
        "database.inventory.sqlserver17-windows.v1.sql",
        "database.files.sqlserver15-windows.v1.sql",
        "database.files.sqlserver16-windows.v1.sql",
        "database.files.sqlserver17-windows.v1.sql",
    ];

    private static readonly string[] ExpectedCollectorManifestNames =
    [
        "engine.core.v1.json",
        "database.inventory.v1.json",
        "database.files.v1.json",
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ReadOnlyCollection<SqlServerCollectorAsset> _collectors;
    private readonly ReadOnlyDictionary<string, SqlServerCollectorAsset> _byId;

    private SqlServerCollectorAssetCatalog(
        IReadOnlyList<SqlServerCollectorAsset> collectors,
        string bundleChecksum)
    {
        SqlServerCollectorAsset[] copy = collectors.ToArray();
        _collectors = Array.AsReadOnly(copy);
        _byId = new ReadOnlyDictionary<string, SqlServerCollectorAsset>(
            copy.ToDictionary(static asset => asset.Manifest.Id.Value, StringComparer.Ordinal));
        BundleChecksum = bundleChecksum;
    }

    public IReadOnlyList<SqlServerCollectorAsset> Collectors => _collectors;

    public string BundleChecksum { get; }

    public SqlServerCollectorAsset Get(CollectorId collectorId)
    {
        ArgumentNullException.ThrowIfNull(collectorId);
        return _byId.TryGetValue(collectorId.Value, out SqlServerCollectorAsset? asset)
            ? asset
            : throw new KeyNotFoundException("The collector is not in the verified SQL Server asset catalog.");
    }

    public static SqlServerCollectorAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerCollectorAssetCatalog).Assembly;
        byte[] checksumBytes = ReadResource(assembly, ChecksumFileName);
        string checksumText = DecodeExactLf(checksumBytes, ChecksumFileName);
        IReadOnlyList<ChecksumEntry> checksumEntries = ParseChecksums(checksumText);
        if (!checksumEntries.Select(static entry => entry.FileName).SequenceEqual(ExpectedAssetNames))
        {
            throw new InvalidDataException("The M4 collector checksum manifest has an unexpected asset order.");
        }

        var verifiedAssets = new Dictionary<string, string>(StringComparer.Ordinal);
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ChecksumEntry entry in checksumEntries)
        {
            byte[] bytes = ReadResource(assembly, entry.FileName);
            string actualChecksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualChecksum),
                    Encoding.ASCII.GetBytes(entry.Checksum)))
            {
                throw new InvalidDataException(
                    $"Embedded collector asset checksum verification failed for {entry.FileName}.");
            }

            verifiedAssets.Add(entry.FileName, DecodeExactLf(bytes, entry.FileName));
            checksums.Add(entry.FileName, actualChecksum);
        }

        var collectors = new List<SqlServerCollectorAsset>(ExpectedCollectorManifestNames.Length);
        foreach (string manifestFileName in ExpectedCollectorManifestNames)
        {
            collectors.Add(ParseCollector(
                verifiedAssets[manifestFileName],
                checksums[manifestFileName],
                verifiedAssets));
        }

        string[] actualOrder = collectors.Select(static item => item.Manifest.Id.Value).ToArray();
        string[] requiredOrder = ["engine.core", "database.inventory", "database.files"];
        if (!actualOrder.SequenceEqual(requiredOrder, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The M4 collector dependency order is invalid.");
        }

        return new SqlServerCollectorAssetCatalog(
            collectors,
            Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant());
    }

    private static SqlServerCollectorAsset ParseCollector(
        string json,
        string checksum,
        IReadOnlyDictionary<string, string> verifiedAssets)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        JsonElement root = document.RootElement;
        RequireObjectProperties(
            root,
            "$schema",
            "schemaVersion",
            "collectorId",
            "collectorVersion",
            "displayName",
            "operationalMode",
            "dependsOn",
            "supportedTargets",
            "requiredCapabilities",
            "requiredPermissionsByMajor",
            "cadence",
            "executionBounds",
            "resilience",
            "estimatedCostClass",
            "queryResources",
            "fallback",
            "outputSchemaVersion",
            "outputKind");
        RequireString(root, "$schema", "collector-manifest.v2.schema.json");
        RequireInt32(root, "schemaVersion", 2);

        var collectorId = new CollectorId(ReadString(root, "collectorId"));
        int collectorVersion = ReadPositiveInt32(root, "collectorVersion");
        var displayName = new CollectorDisplayName(ReadString(root, "displayName"));
        CollectorOperationalMode operationalMode = ReadString(root, "operationalMode") switch
        {
            "passive" => CollectorOperationalMode.Passive,
            "enhanced-prerequisite" => CollectorOperationalMode.EnhancedPrerequisite,
            _ => throw InvalidManifest("operationalMode"),
        };

        CollectorId[] dependencies = ReadStringArray(root, "dependsOn")
            .Select(static value => new CollectorId(value))
            .ToArray();
        JsonElement targets = ReadObject(root, "supportedTargets");
        RequireObjectProperties(
            targets,
            "product",
            "minimumMajorVersion",
            "maximumMajorVersion",
            "platforms",
            "engineEditions");
        RequireString(targets, "product", "Microsoft SQL Server");
        int minimumMajor = ReadPositiveInt32(targets, "minimumMajorVersion");
        int maximumMajor = ReadPositiveInt32(targets, "maximumMajorVersion");
        var supportedVersions = new SqlServerMajorVersionRange(minimumMajor, maximumMajor);
        SqlServerPlatform[] platforms = ReadStringArray(targets, "platforms")
            .Select(static value => value == "Windows"
                ? SqlServerPlatform.Windows
                : throw InvalidManifest("platforms"))
            .ToArray();
        SqlServerEngineEdition[] editions = ReadInt32Array(targets, "engineEditions")
            .Select(static value => value switch
            {
                2 => SqlServerEngineEdition.Standard,
                3 => SqlServerEngineEdition.Enterprise,
                4 => SqlServerEngineEdition.Express,
                _ => throw InvalidManifest("engineEditions"),
            })
            .ToArray();
        CapabilityId[] capabilities = ReadStringArray(root, "requiredCapabilities")
            .Select(static value => new CapabilityId(value))
            .ToArray();
        CollectorPermissionRequirement[] permissions = ReadPermissions(root, supportedVersions);

        JsonElement cadence = ReadObject(root, "cadence");
        RequireObjectProperties(
            cadence,
            "defaultIntervalSeconds",
            "minimumIntervalSeconds",
            "nonOverlappingPerTarget");
        if (!ReadBoolean(cadence, "nonOverlappingPerTarget"))
        {
            throw InvalidManifest("nonOverlappingPerTarget");
        }

        var intervals = new CollectorIntervalPolicy(
            TimeSpan.FromSeconds(ReadPositiveInt32(cadence, "defaultIntervalSeconds")),
            TimeSpan.FromSeconds(ReadPositiveInt32(cadence, "minimumIntervalSeconds")));
        JsonElement bounds = ReadObject(root, "executionBounds");
        RequireObjectProperties(
            bounds,
            "connectTimeoutSeconds",
            "commandTimeoutSeconds",
            "maximumRows",
            "maximumResponseBytes");
        CollectorEstimatedCost estimatedCost = ReadString(root, "estimatedCostClass") switch
        {
            "low" => CollectorEstimatedCost.Low,
            "moderate" => CollectorEstimatedCost.Moderate,
            "high" => CollectorEstimatedCost.High,
            _ => throw InvalidManifest("estimatedCostClass"),
        };
        var limits = new CollectorExecutionLimits(
            TimeSpan.FromSeconds(ReadPositiveInt32(bounds, "connectTimeoutSeconds")),
            TimeSpan.FromSeconds(ReadPositiveInt32(bounds, "commandTimeoutSeconds")),
            ReadPositiveInt32(bounds, "maximumRows"),
            ReadPositiveInt32(bounds, "maximumResponseBytes"),
            estimatedCost);
        JsonElement resilienceElement = ReadObject(root, "resilience");
        RequireObjectProperties(
            resilienceElement,
            "maximumAttempts",
            "transientRetryDelayMilliseconds",
            "circuitFailureThreshold",
            "circuitOpenSeconds");
        var resilience = new CollectorResiliencePolicy(
            ReadPositiveInt32(resilienceElement, "maximumAttempts"),
            TimeSpan.FromMilliseconds(ReadNonNegativeInt32(resilienceElement, "transientRetryDelayMilliseconds")),
            ReadPositiveInt32(resilienceElement, "circuitFailureThreshold"),
            TimeSpan.FromSeconds(ReadPositiveInt32(resilienceElement, "circuitOpenSeconds")));

        JsonElement fallbackElement = ReadObject(root, "fallback");
        string fallbackMode = ReadString(fallbackElement, "mode");
        CollectorFallbackPolicy fallback;
        if (fallbackMode == "unsupported")
        {
            RequireObjectProperties(fallbackElement, "mode");
            fallback = new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported);
        }
        else if (fallbackMode == "alternate_collector")
        {
            RequireObjectProperties(fallbackElement, "mode", "collectorId");
            fallback = new CollectorFallbackPolicy(
                CollectorFallbackMode.AlternateCollector,
                new CollectorId(ReadString(fallbackElement, "collectorId")));
        }
        else
        {
            throw InvalidManifest("fallback.mode");
        }

        CollectorOutputKind outputKind = ReadString(root, "outputKind") switch
        {
            "metrics" => CollectorOutputKind.Metrics,
            "database_inventory" => CollectorOutputKind.DatabaseInventory,
            "database_files" => CollectorOutputKind.DatabaseFiles,
            _ => throw InvalidManifest("outputKind"),
        };
        var manifest = new CollectorManifest(
            collectorId,
            displayName,
            new CollectorManifestVersion(collectorVersion),
            capabilities,
            permissions,
            supportedVersions,
            platforms,
            intervals,
            limits,
            fallback,
            new CollectorOutputSchemaVersion(ReadPositiveInt32(root, "outputSchemaVersion")),
            operationalMode,
            editions,
            dependencies,
            resilience,
            outputKind);

        IReadOnlyDictionary<int, string> queries = ReadQueries(root, supportedVersions, verifiedAssets);
        return new SqlServerCollectorAsset(manifest, json, checksum, queries);
    }

    private static CollectorPermissionRequirement[] ReadPermissions(
        JsonElement root,
        SqlServerMajorVersionRange supportedVersions)
    {
        JsonElement permissions = ReadObject(root, "requiredPermissionsByMajor");
        RequireObjectProperties(permissions, "15", "16", "17");
        var results = new List<CollectorPermissionRequirement>();
        for (int major = 15; major <= 17; major++)
        {
            foreach (string permission in ReadStringArray(permissions, major.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                if (major >= supportedVersions.MinimumMajor && major <= supportedVersions.MaximumMajor)
                {
                    results.Add(new CollectorPermissionRequirement(
                        new SqlServerPermissionId(permission),
                        PermissionEvidenceScope.Server,
                        new SqlServerMajorVersionRange(major, major)));
                }
            }
        }

        return results.ToArray();
    }

    private static Dictionary<int, string> ReadQueries(
        JsonElement root,
        SqlServerMajorVersionRange supportedVersions,
        IReadOnlyDictionary<string, string> verifiedAssets)
    {
        JsonElement queryResources = ReadObject(root, "queryResources");
        RequireObjectProperties(queryResources, "supportedByMajor");
        JsonElement byMajor = ReadObject(queryResources, "supportedByMajor");
        RequireObjectProperties(byMajor, "15", "16", "17");
        var result = new Dictionary<int, string>();
        for (int major = 15; major <= 17; major++)
        {
            string propertyName = major.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string resourceName = ReadString(byMajor, propertyName);
            if (!verifiedAssets.TryGetValue(resourceName, out string? query))
            {
                throw new InvalidDataException("A collector query resource is not checksum-pinned.");
            }

            ValidatePassiveQuery(query);
            if (supportedVersions.Contains(major))
            {
                result.Add(major, query);
            }
        }

        return result;
    }

    private static void ValidatePassiveQuery(string query)
    {
        if (!query.StartsWith("SET NOCOUNT ON;\n", StringComparison.Ordinal) ||
            !query.Contains("TOP (@maximum_rows)", StringComparison.Ordinal) ||
            !query.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A collector query is not deterministically bounded.");
        }

        string normalized = string.Concat(" ", query.ToUpperInvariant(), " ");
        string[] forbidden =
        [
            " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ",
            " DROP ", " TRUNCATE ", " EXEC ", " EXECUTE ", " DBCC ", " BACKUP ",
            " RESTORE ", " RECONFIGURE ", " KILL ", " PHYSICAL_NAME",
        ];
        if (forbidden.Any(token => normalized.Contains(token, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("A collector query contains a forbidden operation or physical path field.");
        }
    }

    private static List<ChecksumEntry> ParseChecksums(string manifest)
    {
        string[] lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<ChecksumEntry>(lines.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
            {
                throw new InvalidDataException("The M4 collector checksum manifest contains an invalid line.");
            }

            string checksum = line[..64];
            string fileName = line[66..];
            if (checksum.Any(static character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)) ||
                fileName.Length == 0 ||
                fileName.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            {
                throw new InvalidDataException("The M4 collector checksum manifest contains an invalid entry.");
            }

            if (!names.Add(fileName))
            {
                throw new InvalidDataException("The M4 collector checksum manifest contains a duplicate entry.");
            }

            entries.Add(new ChecksumEntry(checksum, fileName));
        }

        if (entries.Count != ExpectedAssetNames.Length)
        {
            throw new InvalidDataException("The M4 collector checksum manifest has the wrong entry count.");
        }

        return entries;
    }

    private static byte[] ReadResource(Assembly assembly, string fileName)
    {
        string resourceName = string.Concat(ResourcePrefix, fileName);
        using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidDataException($"Embedded collector resource {fileName} is missing.");
        if (stream.Length > MaximumAssetBytes)
        {
            throw new InvalidDataException($"Embedded collector resource {fileName} exceeds its byte limit.");
        }

        using var destination = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(destination);
        return destination.ToArray();
    }

    private static string DecodeExactLf(byte[] bytes, string fileName)
    {
        if ((bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ||
            bytes.AsSpan().Contains((byte)'\r'))
        {
            throw new InvalidDataException($"Embedded collector resource {fileName} must be UTF-8 without BOM and use LF endings.");
        }

        return StrictUtf8.GetString(bytes);
    }

    private static void RequireObjectProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest("object");
        }

        string[] actual = element.EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] orderedExpected = expected.Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("A collector manifest contains missing, duplicate, or unknown fields.");
        }
    }

    private static JsonElement ReadObject(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Object ? value : throw InvalidManifest(propertyName);
    }

    private static string ReadString(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } result
            ? result
            : throw InvalidManifest(propertyName);
    }

    private static void RequireString(JsonElement parent, string propertyName, string expected)
    {
        if (!ReadString(parent, propertyName).Equals(expected, StringComparison.Ordinal))
        {
            throw InvalidManifest(propertyName);
        }
    }

    private static int ReadPositiveInt32(JsonElement parent, string propertyName)
    {
        int value = ReadNonNegativeInt32(parent, propertyName);
        return value > 0 ? value : throw InvalidManifest(propertyName);
    }

    private static int ReadNonNegativeInt32(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) && result >= 0
            ? result
            : throw InvalidManifest(propertyName);
    }

    private static void RequireInt32(JsonElement parent, string propertyName, int expected)
    {
        if (ReadNonNegativeInt32(parent, propertyName) != expected)
        {
            throw InvalidManifest(propertyName);
        }
    }

    private static bool ReadBoolean(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidManifest(propertyName),
        };
    }

    private static string[] ReadStringArray(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest(propertyName);
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { } text
                ? text
                : throw InvalidManifest(propertyName))
            .ToArray();
    }

    private static int[] ReadInt32Array(JsonElement parent, string propertyName)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest(propertyName);
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)
                ? number
                : throw InvalidManifest(propertyName))
            .ToArray();
    }

    private static InvalidDataException InvalidManifest(string field) =>
        new($"The collector manifest contains an invalid {field} field.");

    private sealed record ChecksumEntry(string Checksum, string FileName);
}
