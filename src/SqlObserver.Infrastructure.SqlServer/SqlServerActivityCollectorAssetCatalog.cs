using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Loads only the checksum-pinned M5 activity bundle from embedded resources.</summary>
public sealed class SqlServerActivityCollectorAssetCatalog
{
    private const string ResourcePrefix = "SqlObserver.Infrastructure.SqlServer.M5ActivityCollectorAssets.";
    private const string ChecksumFileName = "m5-activity.assets.sha256";
    private const int MaximumAssetBytes = 4 * 1024 * 1024;

    private static readonly string[] ExpectedAssetNames =
    [
        "collector-manifest.v3.schema.json",
        "activity.sessions.v1.json",
        "activity.requests.v1.json",
        "waits.server.v1.json",
        "blocking.current.v1.json",
        "activity.sessions.sqlserver15-windows.v1.sql",
        "activity.sessions.sqlserver16-windows.v1.sql",
        "activity.sessions.sqlserver17-windows.v1.sql",
        "activity.requests.sqlserver15-windows.v1.sql",
        "activity.requests.sqlserver16-windows.v1.sql",
        "activity.requests.sqlserver17-windows.v1.sql",
        "waits.server.sqlserver15-windows.v1.sql",
        "waits.server.sqlserver16-windows.v1.sql",
        "waits.server.sqlserver17-windows.v1.sql",
        "blocking.current.sqlserver15-windows.v1.sql",
        "blocking.current.sqlserver16-windows.v1.sql",
        "blocking.current.sqlserver17-windows.v1.sql",
    ];

    private static readonly string[] ExpectedManifestNames =
    [
        "activity.sessions.v1.json",
        "activity.requests.v1.json",
        "waits.server.v1.json",
        "blocking.current.v1.json",
    ];

    private static readonly string[] RequiredCollectorOrder =
    [
        "activity.sessions",
        "activity.requests",
        "waits.server",
        "blocking.current",
    ];

    private static readonly Dictionary<string, string[]> RequiredDependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["activity.sessions"] = ["capability.connection", "engine.core"],
            ["activity.requests"] = ["capability.connection", "engine.core", "activity.sessions"],
            ["waits.server"] =
                ["capability.connection", "engine.core", "activity.sessions", "activity.requests"],
            ["blocking.current"] =
                ["capability.connection", "engine.core", "activity.sessions", "activity.requests", "waits.server"],
        };

    private static readonly IReadOnlyDictionary<string, string> RequiredDmvByCollector =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["activity.sessions"] = "sys.dm_exec_sessions",
            ["activity.requests"] = "sys.dm_exec_requests",
            ["waits.server"] = "sys.dm_os_wait_stats",
            ["blocking.current"] = "sys.dm_os_waiting_tasks",
        };

    private static readonly Dictionary<string, string[]> RequiredOwnSessionPredicates =
        new(StringComparer.Ordinal)
        {
            ["activity.sessions"] = ["sessions.session_id <> @@SPID"],
            ["activity.requests"] = ["requests.session_id <> @@SPID"],
            ["waits.server"] = [],
            ["blocking.current"] =
                ["waiting.session_id <> @@SPID", "waiting.blocking_session_id <> @@SPID"],
        };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlyCollection<SqlServerCollectorAsset> _collectors;
    private readonly ReadOnlyDictionary<string, SqlServerCollectorAsset> _byId;

    private SqlServerActivityCollectorAssetCatalog(
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
            : throw new KeyNotFoundException("The collector is not in the verified M5 activity bundle.");
    }

    public static SqlServerActivityCollectorAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerActivityCollectorAssetCatalog).Assembly;
        byte[] checksumBytes = ReadResource(assembly, ChecksumFileName);
        List<ChecksumEntry> entries = ParseChecksums(DecodeExactLf(checksumBytes, ChecksumFileName));
        if (!entries.Select(static entry => entry.FileName).SequenceEqual(ExpectedAssetNames, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The M5 activity checksum manifest has an unexpected asset order.");
        }

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ChecksumEntry entry in entries)
        {
            byte[] bytes = ReadResource(assembly, entry.FileName);
            string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual),
                    Encoding.ASCII.GetBytes(entry.Checksum)))
            {
                throw new InvalidDataException($"Embedded M5 activity checksum failed for {entry.FileName}.");
            }

            assets.Add(entry.FileName, DecodeExactLf(bytes, entry.FileName));
            checksums.Add(entry.FileName, actual);
        }

        var collectors = new List<SqlServerCollectorAsset>(ExpectedManifestNames.Length);
        foreach (string manifestName in ExpectedManifestNames)
        {
            collectors.Add(ParseCollector(assets[manifestName], checksums[manifestName], assets));
        }

        if (!collectors.Select(static asset => asset.Manifest.Id.Value)
                .SequenceEqual(RequiredCollectorOrder, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The M5 activity collector order is invalid.");
        }

        foreach (SqlServerCollectorAsset collector in collectors)
        {
            string id = collector.Manifest.Id.Value;
            if (!collector.Manifest.DependsOn.Select(static dependency => dependency.Value)
                    .SequenceEqual(RequiredDependencies[id], StringComparer.Ordinal))
            {
                throw new InvalidDataException($"The dependency order for {id} is invalid.");
            }
        }

        return new SqlServerActivityCollectorAssetCatalog(
            collectors,
            Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant());
    }

    private static SqlServerCollectorAsset ParseCollector(
        string json,
        string checksum,
        IReadOnlyDictionary<string, string> verifiedAssets)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
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
        RequireString(root, "$schema", "collector-manifest.v3.schema.json");
        RequireInt32(root, "schemaVersion", 3);

        var id = new CollectorId(ReadString(root, "collectorId"));
        if (!RequiredCollectorOrder.Contains(id.Value, StringComparer.Ordinal))
        {
            throw InvalidManifest("collectorId");
        }

        RequireString(root, "operationalMode", "passive");
        JsonElement targets = ReadObject(root, "supportedTargets");
        RequireObjectProperties(
            targets,
            "product",
            "minimumMajorVersion",
            "maximumMajorVersion",
            "platforms",
            "engineEditions");
        RequireString(targets, "product", "Microsoft SQL Server");
        RequireInt32(targets, "minimumMajorVersion", 15);
        RequireInt32(targets, "maximumMajorVersion", 17);
        string[] platforms = ReadStringArray(targets, "platforms");
        if (!platforms.SequenceEqual(["Windows"], StringComparer.Ordinal))
        {
            throw InvalidManifest("platforms");
        }

        SqlServerEngineEdition[] editions = ReadInt32Array(targets, "engineEditions")
            .Select(static value => value switch
            {
                2 => SqlServerEngineEdition.Standard,
                3 => SqlServerEngineEdition.Enterprise,
                4 => SqlServerEngineEdition.Express,
                _ => throw InvalidManifest("engineEditions"),
            })
            .ToArray();

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

        JsonElement bounds = ReadObject(root, "executionBounds");
        RequireObjectProperties(
            bounds,
            "connectTimeoutSeconds",
            "commandTimeoutSeconds",
            "maximumRows",
            "maximumResponseBytes");
        RequireInt32(bounds, "connectTimeoutSeconds", 5);
        RequireInt32(bounds, "commandTimeoutSeconds", 5);

        JsonElement resilience = ReadObject(root, "resilience");
        RequireObjectProperties(
            resilience,
            "maximumAttempts",
            "transientRetryDelayMilliseconds",
            "circuitFailureThreshold",
            "circuitOpenSeconds");
        RequireInt32(resilience, "maximumAttempts", 2);
        RequireInt32(resilience, "circuitFailureThreshold", 3);
        RequireInt32(resilience, "circuitOpenSeconds", 300);

        JsonElement fallback = ReadObject(root, "fallback");
        RequireObjectProperties(fallback, "mode");
        RequireString(fallback, "mode", "unsupported");
        CollectorOutputKind outputKind = ReadString(root, "outputKind") switch
        {
            "activity_sessions" => CollectorOutputKind.ActivitySessions,
            "activity_requests" => CollectorOutputKind.ActivityRequests,
            "server_waits" => CollectorOutputKind.ServerWaits,
            "current_blocking" => CollectorOutputKind.CurrentBlocking,
            _ => throw InvalidManifest("outputKind"),
        };

        CollectorEstimatedCost cost = ReadString(root, "estimatedCostClass") switch
        {
            "low" => CollectorEstimatedCost.Low,
            "moderate" => CollectorEstimatedCost.Moderate,
            _ => throw InvalidManifest("estimatedCostClass"),
        };
        var supported = new SqlServerMajorVersionRange(15, 17);
        var manifest = new CollectorManifest(
            id,
            new CollectorDisplayName(ReadString(root, "displayName")),
            new CollectorManifestVersion(ReadPositiveInt32(root, "collectorVersion")),
            ReadStringArray(root, "requiredCapabilities").Select(static value => new CapabilityId(value)).ToArray(),
            ReadPermissions(root, supported),
            supported,
            [SqlServerPlatform.Windows],
            new CollectorIntervalPolicy(
                TimeSpan.FromSeconds(ReadPositiveInt32(cadence, "defaultIntervalSeconds")),
                TimeSpan.FromSeconds(ReadPositiveInt32(cadence, "minimumIntervalSeconds"))),
            new CollectorExecutionLimits(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5),
                ReadPositiveInt32(bounds, "maximumRows"),
                ReadPositiveInt32(bounds, "maximumResponseBytes"),
                cost),
            new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(ReadPositiveInt32(root, "outputSchemaVersion")),
            CollectorOperationalMode.Passive,
            editions,
            ReadStringArray(root, "dependsOn").Select(static value => new CollectorId(value)).ToArray(),
            new CollectorResiliencePolicy(
                2,
                TimeSpan.FromMilliseconds(ReadNonNegativeInt32(resilience, "transientRetryDelayMilliseconds")),
                3,
                TimeSpan.FromMinutes(5)),
            outputKind);

        Dictionary<int, string> queries = ReadQueries(root, id.Value, verifiedAssets);
        return new SqlServerCollectorAsset(manifest, json, checksum, queries);
    }

    private static CollectorPermissionRequirement[] ReadPermissions(
        JsonElement root,
        SqlServerMajorVersionRange supported)
    {
        JsonElement permissions = ReadObject(root, "requiredPermissionsByMajor");
        RequireObjectProperties(permissions, "15", "16", "17");
        var result = new List<CollectorPermissionRequirement>();
        for (int major = 15; major <= 17; major++)
        {
            string[] actual = ReadStringArray(permissions, major.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string expected = major == 15 ? "server.view-state" : "server.view-performance-state";
            if (!actual.SequenceEqual([expected], StringComparer.Ordinal))
            {
                throw InvalidManifest("requiredPermissionsByMajor");
            }

            result.Add(new CollectorPermissionRequirement(
                new SqlServerPermissionId(expected),
                PermissionEvidenceScope.Server,
                new SqlServerMajorVersionRange(major, major)));
        }

        return result.ToArray();
    }

    private static Dictionary<int, string> ReadQueries(
        JsonElement root,
        string collectorId,
        IReadOnlyDictionary<string, string> assets)
    {
        JsonElement resources = ReadObject(root, "queryResources");
        RequireObjectProperties(resources, "supportedByMajor");
        JsonElement byMajor = ReadObject(resources, "supportedByMajor");
        RequireObjectProperties(byMajor, "15", "16", "17");
        var result = new Dictionary<int, string>();
        for (int major = 15; major <= 17; major++)
        {
            string resourceName = ReadString(byMajor, major.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!assets.TryGetValue(resourceName, out string? query))
            {
                throw new InvalidDataException("An M5 activity query is not checksum-pinned.");
            }

            ValidatePassiveQuery(query, collectorId, RequiredDmvByCollector[collectorId]);
            result.Add(major, query);
        }

        return result;
    }

    private static void ValidatePassiveQuery(string query, string collectorId, string requiredDmv)
    {
        if (!query.StartsWith("SET NOCOUNT ON;\n", StringComparison.Ordinal) ||
            !query.Contains("TOP (@maximum_rows)", StringComparison.Ordinal) ||
            !query.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) ||
            !query.Contains(requiredDmv, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An M5 activity query is not deterministically bounded.");
        }

        string normalized = string.Concat(" ", query.ToUpperInvariant(), " ");
        string[] forbidden =
        [
            " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ",
            " DROP ", " TRUNCATE ", " EXEC ", " EXECUTE ", " DBCC ", " BACKUP ",
            " RESTORE ", " RECONFIGURE ", " KILL ", "QUERY_TEXT", "SQL_TEXT",
            "SQL_HANDLE", "PLAN_HANDLE", "QUERY_PLAN", "LOGIN_NAME", "HOST_NAME",
            "PROGRAM_NAME", "CLIENT_INTERFACE_NAME", "CLIENT_NET_ADDRESS", "RESOURCE_DESCRIPTION",
            "LOCAL_NET_ADDRESS", "REMOTE_NET_ADDRESS", "TASK_ADDRESS", "WORKER_ADDRESS",
            "WAITING_TASK_ADDRESS", "PHYSICAL_NAME",
        ];
        if (forbidden.Any(token => normalized.Contains(token, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("An M5 activity query contains a forbidden operation or sensitive field.");
        }

        foreach (string dmv in RequiredDmvByCollector.Values)
        {
            if (!dmv.Equals(requiredDmv, StringComparison.Ordinal) && query.Contains(dmv, StringComparison.Ordinal))
            {
                throw new InvalidDataException("An M5 activity query references an unapproved DMV for its contract.");
            }
        }

        int selectEnd = query.IndexOf("\nFROM ", StringComparison.Ordinal);
        if (selectEnd < 0 || query[..selectEnd].Contains("@@SPID", StringComparison.Ordinal))
        {
            throw new InvalidDataException("An M5 activity query projects the collector session identity.");
        }

        string[] ownSessionPredicates = RequiredOwnSessionPredicates[collectorId];
        if (CountOccurrences(query, "@@SPID") != ownSessionPredicates.Length ||
            ownSessionPredicates.Any(predicate => CountOccurrences(query, predicate) != 1))
        {
            throw new InvalidDataException("An M5 activity query has an invalid own-session exclusion.");
        }
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(token, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += token.Length;
        }

        return count;
    }

    private static List<ChecksumEntry> ParseChecksums(string manifest)
    {
        string[] lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != ExpectedAssetNames.Length)
        {
            throw new InvalidDataException("The M5 activity checksum manifest has the wrong entry count.");
        }

        var result = new List<ChecksumEntry>(lines.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
            {
                throw new InvalidDataException("The M5 activity checksum manifest has an invalid line.");
            }

            string checksum = line[..64];
            string fileName = line[66..];
            if (checksum.Any(static character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)) ||
                fileName.Length == 0 ||
                fileName.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-'))
            {
                throw new InvalidDataException("The M5 activity checksum manifest has an invalid entry.");
            }

            if (!names.Add(fileName))
            {
                throw new InvalidDataException("The M5 activity checksum manifest has a duplicate entry.");
            }

            result.Add(new ChecksumEntry(checksum, fileName));
        }

        return result;
    }

    private static byte[] ReadResource(Assembly assembly, string fileName)
    {
        using Stream stream = assembly.GetManifestResourceStream(string.Concat(ResourcePrefix, fileName)) ??
            throw new InvalidDataException($"Embedded M5 activity resource {fileName} is missing.");
        if (stream.Length > MaximumAssetBytes)
        {
            throw new InvalidDataException($"Embedded M5 activity resource {fileName} exceeds its byte limit.");
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
            throw new InvalidDataException($"Embedded M5 activity resource {fileName} must use exact LF UTF-8.");
        }

        return StrictUtf8.GetString(bytes);
    }

    private static void RequireObjectProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("An M5 activity manifest contains missing, duplicate, or unknown fields.");
        }
    }

    private static JsonElement ReadObject(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Object ? value : throw InvalidManifest(name);
    }

    private static string ReadString(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text
            : throw InvalidManifest(name);
    }

    private static void RequireString(JsonElement parent, string name, string expected)
    {
        if (!ReadString(parent, name).Equals(expected, StringComparison.Ordinal))
        {
            throw InvalidManifest(name);
        }
    }

    private static int ReadNonNegativeInt32(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= 0
            ? number
            : throw InvalidManifest(name);
    }

    private static int ReadPositiveInt32(JsonElement parent, string name)
    {
        int value = ReadNonNegativeInt32(parent, name);
        return value > 0 ? value : throw InvalidManifest(name);
    }

    private static void RequireInt32(JsonElement parent, string name, int expected)
    {
        if (ReadNonNegativeInt32(parent, name) != expected)
        {
            throw InvalidManifest(name);
        }
    }

    private static bool ReadBoolean(JsonElement parent, string name)
    {
        return parent.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidManifest(name),
        };
    }

    private static string[] ReadStringArray(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest(name);
        }

        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String && item.GetString() is { } text
                ? text
                : throw InvalidManifest(name)).ToArray();
    }

    private static int[] ReadInt32Array(JsonElement parent, string name)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest(name);
        }

        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)
                ? number
                : throw InvalidManifest(name)).ToArray();
    }

    private static InvalidDataException InvalidManifest(string field) =>
        new($"The M5 activity manifest contains an invalid {field} field.");

    private sealed record ChecksumEntry(string Checksum, string FileName);
}
