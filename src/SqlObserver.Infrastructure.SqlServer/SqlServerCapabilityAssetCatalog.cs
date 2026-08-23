using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Loads only exact, checksum-pinned capability.connection assets from this assembly.</summary>
internal sealed class SqlServerCapabilityAssetCatalog
{
    internal const int ConnectTimeoutSeconds = 5;
    internal const int CommandTimeoutSeconds = 5;
    internal const int MaximumRows = 1;
    internal const int MaximumResponseBytes = 16_384;

    private const int MaximumAssetBytes = 64 * 1024;
    private const int MaximumChecksumBytes = 4 * 1024;
    private const string ResourcePrefix = "SqlObserver.Infrastructure.SqlServer.CollectorAssets.";
    private const string ChecksumFileName = "capability.connection.assets.sha256";

    private static readonly string[] ExpectedFileNames =
    [
        "collector-manifest.schema.json",
        "capability.connection.v1.json",
        "capability.connection.bootstrap.v1.sql",
        "capability.connection.sqlserver15-windows.v1.sql",
        "capability.connection.sqlserver16-windows.v1.sql",
        "capability.connection.sqlserver17-windows.v1.sql",
        "capability.connection.fallback.v1.sql",
    ];

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IReadOnlyDictionary<string, string> _assets;

    private SqlServerCapabilityAssetCatalog(
        IReadOnlyDictionary<string, string> assets,
        CollectorManifest manifest)
    {
        _assets = assets;
        Manifest = manifest;
    }

    internal string BootstrapSql => _assets["capability.connection.bootstrap.v1.sql"];

    internal string PermissionFallbackSql => _assets["capability.connection.fallback.v1.sql"];

    internal string ManifestJson => _assets["capability.connection.v1.json"];

    internal CollectorManifest Manifest { get; }

    internal static SqlServerCapabilityAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerCapabilityAssetCatalog).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();
        EnsureExactResourceSet(resourceNames);

        byte[] checksumBytes = ReadBoundedResource(
            assembly,
            ResourcePrefix + ChecksumFileName,
            MaximumChecksumBytes);
        string checksumText = DecodeExactLfUtf8(checksumBytes, ChecksumFileName);
        List<ChecksumEntry> entries = ParseChecksums(checksumText);

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ChecksumEntry entry in entries)
        {
            byte[] bytes = ReadBoundedResource(
                assembly,
                ResourcePrefix + entry.FileName,
                MaximumAssetBytes);
            string text = DecodeExactLfUtf8(bytes, entry.FileName);
            byte[] actualHash = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, entry.Checksum))
            {
                throw new InvalidDataException(
                    $"Embedded collector asset checksum verification failed for {entry.FileName}.");
            }

            assets.Add(entry.FileName, text);
        }

        ValidateCapabilityManifest(assets["capability.connection.v1.json"]);
        return new SqlServerCapabilityAssetCatalog(assets, CreateManifestContract());
    }

    internal string GetSupportedQuery(int majorVersion)
    {
        string fileName = majorVersion switch
        {
            15 => "capability.connection.sqlserver15-windows.v1.sql",
            16 => "capability.connection.sqlserver16-windows.v1.sql",
            17 => "capability.connection.sqlserver17-windows.v1.sql",
            _ => throw new ArgumentOutOfRangeException(nameof(majorVersion)),
        };

        return _assets[fileName];
    }

    private static void EnsureExactResourceSet(IReadOnlyCollection<string> resourceNames)
    {
        string[] collectorResources = resourceNames
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        string[] expectedResources = ExpectedFileNames
            .Append(ChecksumFileName)
            .Select(static fileName => ResourcePrefix + fileName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (!collectorResources.SequenceEqual(expectedResources, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The embedded capability.connection resources do not match the pinned asset set.");
        }
    }

    private static List<ChecksumEntry> ParseChecksums(string text)
    {
        string[] lines = text.Split('\n');
        int lineCount = lines.Length > 0 && lines[^1].Length == 0
            ? lines.Length - 1
            : lines.Length;
        if (lineCount != ExpectedFileNames.Length)
        {
            throw new InvalidDataException("The collector checksum manifest has the wrong entry count.");
        }

        var entries = new List<ChecksumEntry>(lineCount);
        for (int index = 0; index < lineCount; index++)
        {
            string line = lines[index];
            string expectedFileName = ExpectedFileNames[index];
            if (line.Length != 66 + expectedFileName.Length ||
                line[64] != ' ' ||
                line[65] != ' ' ||
                !line.AsSpan(66).SequenceEqual(expectedFileName))
            {
                throw new InvalidDataException(
                    "The collector checksum manifest has an invalid or reordered entry.");
            }

            ReadOnlySpan<char> checksumHex = line.AsSpan(0, 64);
            if (!checksumHex.ToString().All(static character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new InvalidDataException(
                    "The collector checksum manifest must use lowercase SHA-256 hexadecimal.");
            }

            entries.Add(new ChecksumEntry(expectedFileName, Convert.FromHexString(checksumHex)));
        }

        return entries;
    }

    private static byte[] ReadBoundedResource(
        Assembly assembly,
        string resourceName,
        int maximumBytes)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidDataException($"Embedded collector resource {resourceName} is missing.");
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Embedded collector resource {resourceName} exceeds its byte limit.");
        }

        using var destination = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(destination);
        return destination.ToArray();
    }

    private static string DecodeExactLfUtf8(byte[] bytes, string fileName)
    {
        if (bytes.AsSpan().StartsWith(Utf8ByteOrderMark))
        {
            throw new InvalidDataException($"Collector asset {fileName} must not contain a UTF-8 byte-order mark.");
        }

        if (bytes.AsSpan().Contains((byte)'\r'))
        {
            throw new InvalidDataException($"Collector asset {fileName} must use exact LF line endings.");
        }

        return StrictUtf8.GetString(bytes);
    }

    private static void ValidateCapabilityManifest(string json)
    {
        using JsonDocument document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        RejectDuplicateProperties(document.RootElement);

        JsonElement root = document.RootElement;
        RequireProperties(
            root,
            "$schema",
            "schemaVersion",
            "collectorId",
            "collectorVersion",
            "displayName",
            "operationalMode",
            "supportedTargets",
            "requiredCapabilities",
            "requiredPermissionsByMajor",
            "cadence",
            "executionBounds",
            "estimatedCostClass",
            "queryResources",
            "fallback",
            "outputSchemaVersion");
        RequireString(root, "$schema", "collector-manifest.schema.json");
        RequireNumber(root, "schemaVersion", 1);
        RequireString(root, "collectorId", "capability.connection");
        RequireNumber(root, "collectorVersion", 1);
        RequireString(root, "operationalMode", "passive");
        RequireString(root, "estimatedCostClass", "low");
        RequireNumber(root, "outputSchemaVersion", 1);

        JsonElement targets = root.GetProperty("supportedTargets");
        RequireProperties(
            targets,
            "product",
            "minimumMajorVersion",
            "maximumMajorVersion",
            "platforms",
            "engineEditions");
        RequireString(targets, "product", "Microsoft SQL Server");
        RequireNumber(targets, "minimumMajorVersion", 15);
        RequireNumber(targets, "maximumMajorVersion", 17);
        RequireArray(targets, "platforms", "Windows");
        RequireNumberArray(targets, "engineEditions", 2, 3, 4);

        RequireArray(
            root,
            "requiredCapabilities",
            "connection.tds",
            "authentication.windows-integrated",
            "transport.tls-validated",
            "privilege.non-sysadmin",
            "platform.windows");

        JsonElement permissions = root.GetProperty("requiredPermissionsByMajor");
        RequireProperties(permissions, "15", "16", "17");
        RequireArray(permissions, "15", "server.view-state");
        RequireArray(permissions, "16", "server.view-performance-state");
        RequireArray(permissions, "17", "server.view-performance-state");

        JsonElement cadence = root.GetProperty("cadence");
        RequireProperties(cadence, "defaultIntervalSeconds", "minimumIntervalSeconds", "nonOverlappingPerTarget");
        RequireNumber(cadence, "defaultIntervalSeconds", 300);
        RequireNumber(cadence, "minimumIntervalSeconds", 60);
        if (cadence.GetProperty("nonOverlappingPerTarget").ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("The collector manifest must require non-overlapping execution.");
        }

        JsonElement bounds = root.GetProperty("executionBounds");
        RequireProperties(bounds, "connectTimeoutSeconds", "commandTimeoutSeconds", "maximumRows", "maximumResponseBytes");
        RequireNumber(bounds, "connectTimeoutSeconds", ConnectTimeoutSeconds);
        RequireNumber(bounds, "commandTimeoutSeconds", CommandTimeoutSeconds);
        RequireNumber(bounds, "maximumRows", MaximumRows);
        RequireNumber(bounds, "maximumResponseBytes", MaximumResponseBytes);

        JsonElement queries = root.GetProperty("queryResources");
        RequireProperties(queries, "bootstrap", "supportedByMajor", "permissionFallback");
        RequireString(queries, "bootstrap", "capability.connection.bootstrap.v1.sql");
        RequireString(queries, "permissionFallback", "capability.connection.fallback.v1.sql");
        JsonElement supportedQueries = queries.GetProperty("supportedByMajor");
        RequireProperties(supportedQueries, "15", "16", "17");
        RequireString(supportedQueries, "15", "capability.connection.sqlserver15-windows.v1.sql");
        RequireString(supportedQueries, "16", "capability.connection.sqlserver16-windows.v1.sql");
        RequireString(supportedQueries, "17", "capability.connection.sqlserver17-windows.v1.sql");

        JsonElement fallback = root.GetProperty("fallback");
        RequireProperties(fallback, "mode");
        RequireString(fallback, "mode", "unsupported");
    }

    private static CollectorManifest CreateManifestContract()
    {
        return new CollectorManifest(
            new CollectorId("capability.connection"),
            new CollectorDisplayName("SQL Server capability and connection"),
            new CollectorManifestVersion(1),
            [
                new CapabilityId("connection.tds"),
                new CapabilityId("authentication.windows-integrated"),
                new CapabilityId("transport.tls-validated"),
                new CapabilityId("privilege.non-sysadmin"),
                new CapabilityId("platform.windows"),
            ],
            [
                new CollectorPermissionRequirement(
                    new SqlServerPermissionId("server.view-state"),
                    PermissionEvidenceScope.Server,
                    new SqlServerMajorVersionRange(15, 15)),
                new CollectorPermissionRequirement(
                    new SqlServerPermissionId("server.view-performance-state"),
                    PermissionEvidenceScope.Server,
                    new SqlServerMajorVersionRange(16, 17)),
            ],
            new SqlServerMajorVersionRange(15, 17),
            [SqlServerPlatform.Windows],
            new CollectorIntervalPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
            new CollectorExecutionLimits(
                TimeSpan.FromSeconds(CommandTimeoutSeconds),
                MaximumRows,
                MaximumResponseBytes,
                CollectorEstimatedCost.Low),
            new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(1),
            CollectorOperationalMode.Passive);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Collector JSON must not contain duplicate property names.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void RequireProperties(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Collector manifest property must be an object.");
        }

        string[] actualNames = element.EnumerateObject().Select(static property => property.Name).ToArray();
        if (!actualNames.OrderBy(static name => name, StringComparer.Ordinal).SequenceEqual(
                propertyNames.OrderBy(static name => name, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("Collector manifest object has an unexpected property set.");
        }
    }

    private static void RequireString(JsonElement parent, string propertyName, string expected)
    {
        if (parent.GetProperty(propertyName).GetString() is not string actual ||
            !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Collector manifest property {propertyName} is invalid.");
        }
    }

    private static void RequireNumber(JsonElement parent, string propertyName, int expected)
    {
        if (!parent.GetProperty(propertyName).TryGetInt32(out int actual) || actual != expected)
        {
            throw new InvalidDataException($"Collector manifest property {propertyName} is invalid.");
        }
    }

    private static void RequireArray(JsonElement parent, string propertyName, params string[] expected)
    {
        JsonElement array = parent.GetProperty(propertyName);
        string[] actual = array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray()
            : [];
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Collector manifest array {propertyName} is invalid.");
        }
    }

    private static void RequireNumberArray(JsonElement parent, string propertyName, params int[] expected)
    {
        JsonElement array = parent.GetProperty(propertyName);
        int[] actual = array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(static item => item.GetInt32()).ToArray()
            : [];
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"Collector manifest array {propertyName} is invalid.");
        }
    }

    private sealed record ChecksumEntry(string FileName, byte[] Checksum);
}
