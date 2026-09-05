using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Dedicated checksum-pinned M6 passive system_health asset bundle.</summary>
public sealed class SqlServerDeadlockCollectorAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.M6DeadlockCollectorAssets.";
    private static readonly string[] Names = ["collector-manifest.v4.schema.json", "deadlocks.system-health.v1.json", "deadlocks.system-health.sqlserver15-windows.v1.sql", "deadlocks.system-health.sqlserver16-windows.v1.sql", "deadlocks.system-health.sqlserver17-windows.v1.sql"];
    private static readonly string[] ExpectedCapabilities = ["connection.tds", "authentication.windows-integrated", "transport.tls-validated", "privilege.non-sysadmin", "platform.windows"];
    private static readonly string[] ExpectedWindows = ["Windows"];
    private static readonly int[] ExpectedEditions = [2, 3, 4];
    private static readonly string[] ExpectedViewState = ["server.view-state"];
    private static readonly string[] ExpectedPerformanceState = ["server.view-performance-state"];
    private static readonly int[] ExpectedMajorVersions = [15, 16, 17];
    private readonly SqlServerCollectorAsset _asset;
    private SqlServerDeadlockCollectorAssetCatalog(SqlServerCollectorAsset asset, string bundleChecksum) { _asset = asset; BundleChecksum = bundleChecksum; }
    public string BundleChecksum { get; }
    public SqlServerCollectorAsset Asset => _asset;
    public SqlServerCollectorAsset Get(CollectorId id) => id.Value == "deadlocks.system-health" ? _asset : throw new KeyNotFoundException("The collector is not in the verified M6 bundle.");
    public static SqlServerDeadlockCollectorAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerDeadlockCollectorAssetCatalog).Assembly;
        byte[][] bytes = Names.Select(name => Read(assembly, name)).ToArray();
        byte[] checksumBytes = Read(assembly, "m6-deadlocks.assets.sha256");
        string[] checksumLines = Encoding.UTF8.GetString(checksumBytes).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (checksumLines.Length != Names.Length) throw new InvalidDataException("M6 checksum manifest has an unexpected asset count.");
        for (int index = 0; index < Names.Length; index++)
        {
            string[] parts = checksumLines[index].Split("  ", StringSplitOptions.None);
            string actual = Convert.ToHexString(SHA256.HashData(bytes[index])).ToLowerInvariant();
            if (parts.Length != 2 || parts[1] != Names[index] || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(parts[0]))) throw new InvalidDataException("M6 asset checksum failed.");
        }
        string bundle = Convert.ToHexString(SHA256.HashData(checksumBytes)).ToLowerInvariant();
        string manifestJson = Encoding.UTF8.GetString(bytes[1]);
        var queries = new Dictionary<int, string> { [15] = Encoding.UTF8.GetString(bytes[2]), [16] = Encoding.UTF8.GetString(bytes[3]), [17] = Encoding.UTF8.GetString(bytes[4]) };
        var manifest = ParseManifest(manifestJson, queries);
        return new SqlServerDeadlockCollectorAssetCatalog(new SqlServerCollectorAsset(manifest, manifestJson, Convert.ToHexString(SHA256.HashData(bytes[1])).ToLowerInvariant(), queries), bundle);
    }

    internal static CollectorManifest ParseManifestForTests(string json, IReadOnlyDictionary<int, string> queries) => ParseManifest(json, queries);

    private static CollectorManifest ParseManifest(string json, IReadOnlyDictionary<int, string> queries)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RequireProperties(root, "$schema", "schemaVersion", "collectorId", "collectorVersion", "displayName", "operationalMode", "dependsOn", "supportedTargets", "requiredCapabilities", "requiredPermissionsByMajor", "cadence", "executionBounds", "resilience", "estimatedCostClass", "queryResources", "fallback", "outputSchemaVersion", "outputKind");
        RequireString(root, "$schema", "collector-manifest.v4.schema.json");
        RequireInt(root, "schemaVersion", 4);
        RequireString(root, "collectorId", "deadlocks.system-health");
        RequireInt(root, "collectorVersion", 1);
        RequireString(root, "displayName", "SQL Server system_health deadlock evidence");
        string id = String(root, "collectorId");
        string display = String(root, "displayName");
        RequireString(root, "operationalMode", "passive");
        RequireString(root, "estimatedCostClass", "moderate");
        RequireString(root, "outputKind", "deadlocks");
        var dependencies = StringArray(root, "dependsOn");
        var expectedDependencies = new[] { "capability.connection", "engine.core", "activity.sessions", "activity.requests", "waits.server", "blocking.current" };
        RequireSequence(dependencies, expectedDependencies, "dependsOn");
        var capabilities = StringArray(root, "requiredCapabilities");
        RequireSequence(capabilities, ExpectedCapabilities, "requiredCapabilities");
        JsonElement targets = Object(root, "supportedTargets");
        RequireProperties(targets, "product", "minimumMajorVersion", "maximumMajorVersion", "platforms", "engineEditions");
        RequireString(targets, "product", "Microsoft SQL Server");
        int minimumMajor = PositiveInt(targets, "minimumMajorVersion");
        int maximumMajor = PositiveInt(targets, "maximumMajorVersion");
        if (minimumMajor != 15 || maximumMajor != 17) throw InvalidManifest("supportedTargets.versionRange");
        RequireSequence(StringArray(targets, "platforms"), ExpectedWindows, "supportedTargets.platforms");
        RequireSequence(IntArray(targets, "engineEditions"), ExpectedEditions, "supportedTargets.engineEditions");
        var supportedVersions = new SqlServerMajorVersionRange(minimumMajor, maximumMajor);
        JsonElement permissions = Object(root, "requiredPermissionsByMajor");
        RequireProperties(permissions, "15", "16", "17");
        string[] p15 = StringArray(permissions, "15");
        string[] p16 = StringArray(permissions, "16");
        string[] p17 = StringArray(permissions, "17");
        RequireSequence(p15, ExpectedViewState, "requiredPermissionsByMajor.15");
        RequireSequence(p16, ExpectedPerformanceState, "requiredPermissionsByMajor.16");
        RequireSequence(p17, ExpectedPerformanceState, "requiredPermissionsByMajor.17");
        var requiredPermissions = new[]
        {
            new CollectorPermissionRequirement(new SqlServerPermissionId(p15[0]), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(15, 15)),
            new CollectorPermissionRequirement(new SqlServerPermissionId(p16[0]), PermissionEvidenceScope.Server, new SqlServerMajorVersionRange(16, 17)),
        };
        JsonElement cadence = Object(root, "cadence");
        RequireProperties(cadence, "defaultIntervalSeconds", "minimumIntervalSeconds", "nonOverlappingPerTarget");
        if (!Boolean(cadence, "nonOverlappingPerTarget")) throw InvalidManifest("cadence.nonOverlappingPerTarget");
        RequireInt(cadence, "defaultIntervalSeconds", 30);
        RequireInt(cadence, "minimumIntervalSeconds", 10);
        var intervals = new CollectorIntervalPolicy(TimeSpan.FromSeconds(PositiveInt(cadence, "defaultIntervalSeconds")), TimeSpan.FromSeconds(PositiveInt(cadence, "minimumIntervalSeconds")));
        JsonElement bounds = Object(root, "executionBounds");
        RequireProperties(bounds, "connectTimeoutSeconds", "commandTimeoutSeconds", "maximumRows", "maximumResponseBytes");
        RequireInt(bounds, "connectTimeoutSeconds", 5);
        RequireInt(bounds, "commandTimeoutSeconds", 5);
        RequireInt(bounds, "maximumRows", 257);
        RequireInt(bounds, "maximumResponseBytes", 1048576);
        var limits = new CollectorExecutionLimits(TimeSpan.FromSeconds(PositiveInt(bounds, "connectTimeoutSeconds")), TimeSpan.FromSeconds(PositiveInt(bounds, "commandTimeoutSeconds")), PositiveInt(bounds, "maximumRows"), PositiveInt(bounds, "maximumResponseBytes"), CollectorEstimatedCost.Moderate);
        JsonElement resilience = Object(root, "resilience");
        RequireProperties(resilience, "maximumAttempts", "transientRetryDelayMilliseconds", "circuitFailureThreshold", "circuitOpenSeconds");
        RequireInt(resilience, "maximumAttempts", 2);
        RequireInt(resilience, "transientRetryDelayMilliseconds", 100);
        RequireInt(resilience, "circuitFailureThreshold", 3);
        RequireInt(resilience, "circuitOpenSeconds", 300);
        var retry = new CollectorResiliencePolicy(PositiveInt(resilience, "maximumAttempts"), TimeSpan.FromMilliseconds(NonNegativeInt(resilience, "transientRetryDelayMilliseconds")), PositiveInt(resilience, "circuitFailureThreshold"), TimeSpan.FromSeconds(PositiveInt(resilience, "circuitOpenSeconds")));
        JsonElement fallback = Object(root, "fallback");
        RequireProperties(fallback, "mode");
        RequireString(fallback, "mode", "unsupported");
        JsonElement queryResources = Object(root, "queryResources");
        RequireProperties(queryResources, "supportedByMajor");
        JsonElement supportedByMajor = Object(queryResources, "supportedByMajor");
        RequireProperties(supportedByMajor, "15", "16", "17");
        RequireString(supportedByMajor, "15", Names[2]);
        RequireString(supportedByMajor, "16", Names[3]);
        RequireString(supportedByMajor, "17", Names[4]);
        if (!queries.Keys.Order().SequenceEqual(ExpectedMajorVersions)) throw new InvalidDataException("M6 manifest query resources do not match the verified query set.");
        foreach (string query in queries.Values) ValidatePassiveQuery(query);
        RequireInt(root, "outputSchemaVersion", 1);
        return new CollectorManifest(new CollectorId(id), new CollectorDisplayName(display), new CollectorManifestVersion(1), capabilities.Select(static value => new CapabilityId(value)).ToArray(), requiredPermissions, supportedVersions, [SqlServerPlatform.Windows], intervals, limits, new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported), new CollectorOutputSchemaVersion(1), CollectorOperationalMode.Passive, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], dependencies.Select(static value => new CollectorId(value)).ToArray(), retry, CollectorOutputKind.Deadlocks);
    }

    private static void ValidatePassiveQuery(string query)
    {
        if (!query.StartsWith("SET NOCOUNT ON;\n", StringComparison.Ordinal) || !query.Contains("TOP (@maximum_rows)", StringComparison.Ordinal) || !query.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) || !query.Contains("fn_xe_file_target_read_file", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("M6 query is not bounded and passive.");
        string normalized = $" {query.ToUpperInvariant()} ";
        foreach (string token in new[] { " INSERT ", " UPDATE ", " DELETE ", " MERGE ", " CREATE ", " ALTER ", " DROP ", " TRUNCATE ", " EXEC ", " EXECUTE ", " START EVENT SESSION ", " STOP EVENT SESSION ", " PHYSICAL_NAME" }) if (normalized.Contains(token, StringComparison.Ordinal)) throw new InvalidDataException("M6 query contains a forbidden operation.");
    }

    private static JsonElement Object(JsonElement parent, string property) => parent.GetProperty(property).ValueKind == JsonValueKind.Object ? parent.GetProperty(property) : throw InvalidManifest(property);
    private static string String(JsonElement parent, string property) => parent.GetProperty(property).ValueKind == JsonValueKind.String && parent.GetProperty(property).GetString() is { } value ? value : throw InvalidManifest(property);
    private static void RequireString(JsonElement parent, string property, string expected) { if (!String(parent, property).Equals(expected, StringComparison.Ordinal)) throw InvalidManifest(property); }
    private static int PositiveInt(JsonElement parent, string property) { int value = NonNegativeInt(parent, property); return value > 0 ? value : throw InvalidManifest(property); }
    private static int NonNegativeInt(JsonElement parent, string property) => parent.GetProperty(property).ValueKind == JsonValueKind.Number && parent.GetProperty(property).TryGetInt32(out int value) && value >= 0 ? value : throw InvalidManifest(property);
    private static void RequireInt(JsonElement parent, string property, int expected) { if (NonNegativeInt(parent, property) != expected) throw InvalidManifest(property); }
    private static bool Boolean(JsonElement parent, string property) => parent.GetProperty(property).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw InvalidManifest(property) };
    private static string[] StringArray(JsonElement parent, string property) => parent.GetProperty(property).ValueKind == JsonValueKind.Array ? parent.GetProperty(property).EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { } value ? value : throw InvalidManifest(property)).ToArray() : throw InvalidManifest(property);
    private static int[] IntArray(JsonElement parent, string property) => parent.GetProperty(property).ValueKind == JsonValueKind.Array ? parent.GetProperty(property).EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int value) ? value : throw InvalidManifest(property)).ToArray() : throw InvalidManifest(property);
    private static void RequireSequence<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected, string property) { if (!actual.SequenceEqual(expected)) throw InvalidManifest(property); }
    private static void RequireProperties(JsonElement element, params string[] expected) { if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(static item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new InvalidDataException("M6 manifest contains missing or unknown fields."); }
    private static InvalidDataException InvalidManifest(string field) => new($"M6 manifest contains an invalid {field} field.");
    private static byte[] Read(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidDataException("M6 asset is unavailable.");
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("M6 asset exceeds the bounded size.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
}

/// <summary>Reads only the existing system_health event-file target; it never mutates XE state.</summary>
public sealed class SqlServerDeadlockCollector : SqlServerActivityCollectorBase
{
    internal readonly record struct BoundedXmlRead(string Text, int Utf8Bytes, bool Oversized);
    private static readonly CollectorOutputContract RegisteredOutputContract = new(new CollectorOutputSchemaVersion(1), [], 0, 0, 0, maxDeadlockObservations: DeadlockObservationBatch.MaximumItems);
    public static CollectorOutputContract OutputContract => RegisteredOutputContract;
    public SqlServerDeadlockCollector() : this(SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded(), new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)) { }
    public SqlServerDeadlockCollector(SqlServerDeadlockCollectorAssetCatalog catalog) : this(catalog, new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)) { }
    internal SqlServerDeadlockCollector(SqlServerDeadlockCollectorAssetCatalog catalog, ISqlServerConnectionFactory factory) : base(catalog.Asset, factory) { }
    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(CollectorExecutionRequest request, Microsoft.Data.SqlClient.SqlDataReader reader, CancellationToken cancellationToken)
    {
        var items = new List<DeadlockObservation>(); var budget = new BoundedCollectorReadBudget(Manifest.Limits.MaxRows, Manifest.Limits.MaxResponseBytes); int parseLoss = 0;
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow()) break;
                // SequentialAccess requires consuming the timestamp and XML before the state column.
                DateTime? occurredValue = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
                BoundedXmlRead? bounded = null;
                if (!reader.IsDBNull(1))
                {
                    using TextReader textReader = reader.GetTextReader(1);
                    bounded = await ReadBoundedXmlAsync(textReader, cancellationToken).ConfigureAwait(false);
                }
                if (bounded is { Oversized: true }) { parseLoss++; break; }
                string sourceState = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetString(2) : "ready";
                if (sourceState == "oversized") { parseLoss++; _ = budget.TryAcceptResponseBytes(1); break; }
                if (sourceState is not ("ready" or "empty" or "missing")) { parseLoss++; _ = budget.TryAcceptResponseBytes(1); continue; }
                if (occurredValue is null) { if (!budget.TryAcceptResponseBytes(1)) break; if (sourceState == "missing") parseLoss++; continue; }
                if (bounded is null) { parseLoss++; _ = budget.TryAcceptResponseBytes(1); continue; }
                if (!budget.TryAcceptResponseBytes(bounded.Value.Utf8Bytes)) break;
                DateTimeOffset occurred = new(DateTime.SpecifyKind(occurredValue.Value, DateTimeKind.Utc));
                string xml = bounded.Value.Text;
                try
                {
                    DeadlockObservation observation = DeadlockXmlParser.Parse(request.TargetId, request.TargetRevision, Encoding.UTF8.GetBytes(xml), occurred, cancellationToken);
                    items.Add(observation);
                    if (observation.ParseTruncated) parseLoss++;
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException) { parseLoss++; /* malformed events are explicit loss, not provider text */ }
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or ArgumentException) { throw new CollectorReadValidationException(budget, items.Count, budget.ResponseBytes, ex); }
        if (budget.SourceRowsRead == 0) parseLoss++;
        // Source sentinels are aggregate evidence: each proves at least one
        // unavailable/bad row, not the complete count of rows lost upstream.
        CollectorLossEvidence? loss = parseLoss > 0 ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, parseLoss, false, parseLoss) : null;
        return CompleteRead(new CollectorPayload(deadlocks: new DeadlockObservationBatch(items)), budget, loss);
    }

    internal static async ValueTask<BoundedXmlRead> ReadBoundedXmlAsync(TextReader source, CancellationToken cancellationToken)
    {
        var buffer = new char[8192]; var text = new StringBuilder(); int utf8Bytes = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            int chunkBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (utf8Bytes > DeadlockXmlParser.MaximumBytes - chunkBytes)
                return new BoundedXmlRead(string.Empty, DeadlockXmlParser.MaximumBytes, true);
            utf8Bytes += chunkBytes;
            text.Append(buffer, 0, read);
        }
        return new BoundedXmlRead(text.ToString(), utf8Bytes, false);
    }
}
