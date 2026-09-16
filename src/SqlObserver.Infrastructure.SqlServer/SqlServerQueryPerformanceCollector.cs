using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Deterministic source choice; cancellation/fence loss is never converted to fallback.</summary>
public static class QueryPerformanceSourceSelector
{
    public static (QueryPerformanceSource Source, QueryStoreState State, string Reason) Choose(QueryStoreState state, bool canUsePlanCache, bool cancelled = false, bool fenceLost = false)
    {
        if (cancelled || fenceLost) throw new OperationCanceledException("Query performance source selection was cancelled or fenced.");
        if (state is QueryStoreState.ReadWrite or QueryStoreState.ReadOnly) return (QueryPerformanceSource.QueryStore, state, "query_store_preferred");
        if (canUsePlanCache) return (QueryPerformanceSource.PlanCache, state, "query_store_fallback");
        return (QueryPerformanceSource.QueryStore, state, "unavailable");
    }
}

/// <summary>Checksum-pinned M7 query assets. The manifest and SQL are immutable embedded resources.</summary>
public sealed class SqlServerQueryPerformanceCollectorAssetCatalog
{
    private const string Prefix = "SqlObserver.Infrastructure.SqlServer.M7QueryPerformanceCollectorAssets.";
    private static readonly string[] Names = ["collector-manifest.v4.schema.json", "queries.performance.v1.json", "queries.performance.sqlserver15-windows.v1.sql", "queries.performance.sqlserver16-windows.v1.sql", "queries.performance.sqlserver17-windows.v1.sql"];
    private readonly SqlServerCollectorAsset asset;
    private static readonly int[] ExpectedVersions = [15, 16, 17];
    private SqlServerQueryPerformanceCollectorAssetCatalog(SqlServerCollectorAsset asset, string checksum, string fallbackMode, IReadOnlyDictionary<int, IReadOnlyList<string>> fallbackPermissions) { this.asset = asset; BundleChecksum = checksum; FallbackMode = fallbackMode; FallbackPermissionsByMajor = fallbackPermissions; }
    public string BundleChecksum { get; }
    public string FallbackMode { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<string>> FallbackPermissionsByMajor { get; }
    public SqlServerCollectorAsset Asset => asset;
    public SqlServerCollectorAsset Get(CollectorId id) => id.Value == "queries.performance" ? asset : throw new KeyNotFoundException("The collector is not in the verified M7 bundle.");
    public static SqlServerQueryPerformanceCollectorAssetCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(SqlServerQueryPerformanceCollectorAssetCatalog).Assembly;
        byte[][] bytes = Names.Select(name => Read(assembly, name)).ToArray();
        string[] lines = Encoding.UTF8.GetString(Read(assembly, "m7-query-performance.assets.sha256")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != Names.Length) throw new InvalidDataException("M7 checksum manifest has an unexpected asset count.");
        for (int i = 0; i < Names.Length; i++) { string[] p = lines[i].Split("  ", StringSplitOptions.None); string actual = Convert.ToHexString(SHA256.HashData(bytes[i])).ToLowerInvariant(); if (p.Length != 2 || p[1] != Names[i] || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(p[0]))) throw new InvalidDataException("M7 asset checksum failed."); }
        var queries = new Dictionary<int, string> { [15] = Encoding.UTF8.GetString(bytes[2]), [16] = Encoding.UTF8.GetString(bytes[3]), [17] = Encoding.UTF8.GetString(bytes[4]) };
        ValidateManifestJson(Encoding.UTF8.GetString(bytes[1]), queries);
        (CollectorManifest manifest, string fallbackMode, IReadOnlyDictionary<int, IReadOnlyList<string>> fallbackPermissions) = ParseManifest(Encoding.UTF8.GetString(bytes[1]));
        return new SqlServerQueryPerformanceCollectorAssetCatalog(new SqlServerCollectorAsset(manifest, Encoding.UTF8.GetString(bytes[1]), Convert.ToHexString(SHA256.HashData(bytes[1])).ToLowerInvariant(), queries), Convert.ToHexString(SHA256.HashData(Read(assembly, "m7-query-performance.assets.sha256"))).ToLowerInvariant(), fallbackMode, fallbackPermissions);
    }
    private static (CollectorManifest Manifest, string FallbackMode, IReadOnlyDictionary<int, IReadOnlyList<string>> Permissions) ParseManifest(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement targets = root.GetProperty("supportedTargets");
        JsonElement bounds = root.GetProperty("executionBounds");
        JsonElement cadence = root.GetProperty("cadence");
        JsonElement resilience = root.GetProperty("resilience");
        JsonElement required = root.GetProperty("requiredPermissionsByMajor");
        var permissions = new List<CollectorPermissionRequirement>();
        foreach (string major in new[] { "15", "16", "17" }) permissions.Add(new CollectorPermissionRequirement(new SqlServerPermissionId(required.GetProperty(major)[0].GetString()!), PermissionEvidenceScope.Database, new SqlServerMajorVersionRange(int.Parse(major, CultureInfo.InvariantCulture), int.Parse(major, CultureInfo.InvariantCulture))));
        JsonElement fallback = root.GetProperty("fallback");
        var fallbackPermissions = new Dictionary<int, IReadOnlyList<string>>();
        foreach (string major in new[] { "15", "16", "17" }) fallbackPermissions[int.Parse(major, CultureInfo.InvariantCulture)] = fallback.GetProperty("permissionsByMajor").GetProperty(major).EnumerateArray().Select(item => item.GetString()!).ToArray();
        CollectorManifest manifest = new(new CollectorId(root.GetProperty("collectorId").GetString()!), new CollectorDisplayName(root.GetProperty("displayName").GetString()!), new CollectorManifestVersion(root.GetProperty("collectorVersion").GetInt32()), root.GetProperty("requiredCapabilities").EnumerateArray().Select(item => new CapabilityId(item.GetString()!)).ToArray(), permissions, new SqlServerMajorVersionRange(targets.GetProperty("minimumMajorVersion").GetInt32(), targets.GetProperty("maximumMajorVersion").GetInt32()), [SqlServerPlatform.Windows], new CollectorIntervalPolicy(TimeSpan.FromSeconds(cadence.GetProperty("defaultIntervalSeconds").GetInt32()), TimeSpan.FromSeconds(cadence.GetProperty("minimumIntervalSeconds").GetInt32())), new CollectorExecutionLimits(TimeSpan.FromSeconds(bounds.GetProperty("connectTimeoutSeconds").GetInt32()), TimeSpan.FromSeconds(bounds.GetProperty("commandTimeoutSeconds").GetInt32()), bounds.GetProperty("maximumRows").GetInt32(), bounds.GetProperty("maximumResponseBytes").GetInt32(), CollectorEstimatedCost.Moderate), new CollectorFallbackPolicy(CollectorFallbackMode.AlternateSource), new CollectorOutputSchemaVersion(root.GetProperty("outputSchemaVersion").GetInt32()), CollectorOperationalMode.Passive, [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise, SqlServerEngineEdition.Express], root.GetProperty("dependsOn").EnumerateArray().Select(item => new CollectorId(item.GetString()!)).ToArray(), new CollectorResiliencePolicy(resilience.GetProperty("maximumAttempts").GetInt32(), TimeSpan.FromMilliseconds(resilience.GetProperty("transientRetryDelayMilliseconds").GetInt32()), resilience.GetProperty("circuitFailureThreshold").GetInt32(), TimeSpan.FromSeconds(resilience.GetProperty("circuitOpenSeconds").GetInt32())), CollectorOutputKind.QueryPerformance);
        return (manifest, fallback.GetProperty("mode").GetString()!, fallbackPermissions);
    }
    internal static void ValidateManifestJson(string json, IReadOnlyDictionary<int, string> queries)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
        JsonElement root = doc.RootElement; string[] required = ["$schema", "schemaVersion", "collectorId", "collectorVersion", "displayName", "operationalMode", "dependsOn", "supportedTargets", "requiredCapabilities", "requiredPermissionsByMajor", "cadence", "executionBounds", "resilience", "estimatedCostClass", "fallback", "queryResources", "outputSchemaVersion", "outputKind"];
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new InvalidDataException("M7 manifest schema is not exact.");
        if (root.GetProperty("collectorId").GetString() != "queries.performance" || root.GetProperty("schemaVersion").GetInt32() != 4 || root.GetProperty("collectorVersion").GetInt32() != 1 || root.GetProperty("outputSchemaVersion").GetInt32() != 1 || root.GetProperty("outputKind").GetString() != "query-performance") throw new InvalidDataException("M7 manifest identity is invalid.");
        JsonElement targets = root.GetProperty("supportedTargets"); if (targets.GetProperty("minimumMajorVersion").GetInt32() != 15 || targets.GetProperty("maximumMajorVersion").GetInt32() != 17 || targets.GetProperty("product").GetString() != "Microsoft SQL Server" || targets.GetProperty("platforms").GetArrayLength() != 1 || targets.GetProperty("platforms")[0].GetString() != "Windows") throw new InvalidDataException("M7 target range is invalid.");
        JsonElement bounds = root.GetProperty("executionBounds"); if (bounds.GetProperty("maximumRows").GetInt32() != 2000 || bounds.GetProperty("maximumResponseBytes").GetInt32() != 8 * 1024 * 1024 || bounds.GetProperty("connectTimeoutSeconds").GetInt32() != 5 || bounds.GetProperty("commandTimeoutSeconds").GetInt32() != 15) throw new InvalidDataException("M7 execution bounds are invalid.");
        JsonElement cadence = root.GetProperty("cadence"); if (cadence.GetProperty("defaultIntervalSeconds").GetInt32() != 300 || cadence.GetProperty("minimumIntervalSeconds").GetInt32() != 60 || !cadence.GetProperty("nonOverlappingPerTarget").GetBoolean()) throw new InvalidDataException("M7 cadence is invalid.");
        JsonElement requiredPermissions = root.GetProperty("requiredPermissionsByMajor"); JsonElement fallback = root.GetProperty("fallback"); if (fallback.GetProperty("mode").GetString() != "plan-cache" || !fallback.GetProperty("singleAttempt").GetBoolean()) throw new InvalidDataException("M7 fallback contract is invalid.");
        foreach (string version in new[] { "15", "16", "17" }) { string expectedDatabase = version == "15" ? "database.view-state" : "database.view-performance-state"; string expectedServer = version == "15" ? "server.view-state" : "server.view-performance-state"; if (requiredPermissions.GetProperty(version).GetArrayLength() != 1 || requiredPermissions.GetProperty(version)[0].GetString() != expectedDatabase || fallback.GetProperty("permissionsByMajor").GetProperty(version).GetArrayLength() != 1 || fallback.GetProperty("permissionsByMajor").GetProperty(version)[0].GetString() != expectedServer) throw new InvalidDataException("M7 permission contract is invalid."); }
        JsonElement resources = root.GetProperty("queryResources").GetProperty("supportedByMajor"); if (resources.GetProperty("15").GetString() != "queries.performance.sqlserver15-windows.v1.sql" || resources.GetProperty("16").GetString() != "queries.performance.sqlserver16-windows.v1.sql" || resources.GetProperty("17").GetString() != "queries.performance.sqlserver17-windows.v1.sql") throw new InvalidDataException("M7 SQL resource contract is invalid.");
        if (queries.Count != 3 || queries.Keys.Order().SequenceEqual(ExpectedVersions) == false) throw new InvalidDataException("M7 query assets are incomplete.");
    }
    private static byte[] Read(Assembly assembly, string name) { Stream? stream = assembly.GetManifestResourceStream(Prefix + name) ?? (name == "collector-manifest.v4.schema.json" ? assembly.GetManifestResourceStream("SqlObserver.Infrastructure.SqlServer.M6DeadlockCollectorAssets.collector-manifest.v4.schema.json") : null); using (stream ?? throw new InvalidDataException("M7 asset unavailable.")) { if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("M7 asset exceeds size bound."); using var m = new MemoryStream(); stream.CopyTo(m); return m.ToArray(); } }
}

/// <summary>Reads bounded, metadata-only Query Store rows. No SQL text, handles or plan XML are selected.</summary>
internal interface ISqlServerQueryPerformanceExecutionPort
{
    ValueTask<IReadOnlyList<SqlServerDatabaseIdentity>> ReadInventoryAsync(CollectorExecutionRequest request, CancellationToken cancellationToken);
    ValueTask<QueryPerformanceExecutionBatch> ReadDatabasesAsync(IReadOnlyList<SqlServerDatabaseIdentity> databases, CollectorExecutionRequest request, SharedResponseBudget responseBudget, CancellationToken cancellationToken);
}

internal sealed record QueryPerformanceExecutionBatch(
    IReadOnlyList<QueryPerformanceReadResult> Reads,
    QueryPerformancePlanCacheSample? PlanCacheSample = null);

/// <summary>Composable bounded loss accounting for the target-wide query-performance result.</summary>
internal sealed class QueryPerformanceLossAccumulator
{
    private bool hasOverlappingComponents;
    public CollectorLossKind Kind { get; private set; } = CollectorLossKind.None;
    public int MinimumLostItems { get; private set; }
    public int MinimumLostBytes { get; private set; }
    public bool CountIsExact { get; private set; } = true;
    public bool HasLoss => Kind != CollectorLossKind.None;
    public void Add(CollectorLossKind kind, int minimumItems, int minimumBytes, bool exact, bool overlapsExisting = false)
    {
        if (kind == CollectorLossKind.None) return;
        if (hasOverlappingComponents || overlapsExisting)
        {
            MinimumLostItems = Math.Max(MinimumLostItems, Math.Max(0, minimumItems));
            MinimumLostBytes = Math.Max(MinimumLostBytes, Math.Max(0, minimumBytes));
        }
        else
        {
            MinimumLostItems = checked(MinimumLostItems + Math.Max(0, minimumItems));
            MinimumLostBytes = checked(MinimumLostBytes + Math.Max(0, minimumBytes));
        }
        CountIsExact &= exact && !overlapsExisting && !hasOverlappingComponents;
        hasOverlappingComponents |= !exact || overlapsExisting;
        Kind = Kind switch
        {
            CollectorLossKind.ResponseByteLimit => Kind,
            _ when kind == CollectorLossKind.ResponseByteLimit => kind,
            _ when Kind == CollectorLossKind.None => kind,
            _ => Kind,
        };
    }
}

/// <summary>Reads bounded, metadata-only Query Store rows. No SQL text, handles or plan XML are selected.</summary>
public sealed class SqlServerQueryPerformanceCollector : SqlServerActivityCollectorBase
{
    private const string InventorySql = "SELECT TOP (257) database_id,name FROM sys.databases WHERE state=0 AND user_access=0 AND database_id>4 ORDER BY database_id;";
    internal const string PlanCacheSql = "SELECT TOP (@probe_rows) CONVERT(int,pa.value),CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONCAT(CONVERT(varchar(20),s.query_hash),':cache:',CONVERT(varchar(20),pa.value)))),2),CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONCAT(CONVERT(varchar(20),s.query_hash),':plan:',CONVERT(varchar(30),s.plan_generation_num),':created:',CONVERT(varchar(33),s.creation_time,126)))),2),CONVERT(varchar(16),'plan_cache'),CONVERT(varchar(32),'read_failure'),CONVERT(bigint,SUM(s.total_worker_time)/1000),CONVERT(bigint,SUM(s.total_elapsed_time)/1000),CONVERT(bigint,SUM(s.execution_count)),CONVERT(bigint,SUM(s.total_logical_reads)),CONVERT(bigint,SUM(s.total_logical_writes)),CONVERT(bigint,NULL),@sample_start,@sample_end,@sample_end,CONVERT(bit,0),CONVERT(bit,0) FROM sys.dm_exec_query_stats s CROSS APPLY sys.dm_exec_plan_attributes(s.plan_handle) pa WHERE pa.attribute='dbid' AND CONVERT(int,pa.value) IN (SELECT database_id FROM sys.databases WHERE database_id>4 AND state=0 AND user_access=0) GROUP BY pa.value,s.query_hash,s.plan_generation_num,s.creation_time ORDER BY SUM(s.total_worker_time) DESC,s.query_hash,s.plan_generation_num,s.creation_time;";
    public static CollectorOutputContract OutputContract { get; } = new(new CollectorOutputSchemaVersion(1), [], 0, 0, 0, maxQueryPerformanceObservations: QueryPerformanceObservationBatch.MaximumItems);
    private readonly ISqlServerQueryPerformanceExecutionPort? executionPort;
    public SqlServerQueryPerformanceCollector() : this(SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded()) { }
    public SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog catalog) : this(catalog, null) { }
    internal SqlServerQueryPerformanceCollector(SqlServerQueryPerformanceCollectorAssetCatalog catalog, ISqlServerQueryPerformanceExecutionPort? executionPort) : base(catalog.Asset, new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName)) => this.executionPort = executionPort;
    public override async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null || request.CapabilityProfile.TargetId != request.TargetId || request.CapabilityProfile.TargetRevision != request.TargetRevision) return Invalid(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        int major = request.CapabilityProfile.ServerIdentity.Version.Major;
        if (!Manifest.SupportedVersions.Contains(major)) return Invalid(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetVersionUnsupported);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout);
        try
        {
            var databases = new List<SqlServerDatabaseIdentity>();
            bool inventoryTruncated = false;
            try
            {
                if (executionPort is not null)
                {
                    databases.AddRange(await executionPort.ReadInventoryAsync(request, deadline.Token).ConfigureAwait(false));
                    if (databases.Count > QueryPerformanceBounds.MaximumDatabases)
                    {
                        databases.RemoveRange(QueryPerformanceBounds.MaximumDatabases, databases.Count - QueryPerformanceBounds.MaximumDatabases);
                        inventoryTruncated = true;
                    }
                }
                else
                {
                    await using SqlConnection inventoryConnection = await ConnectionFactory.OpenConnectionAsync(request.ConnectionPolicy, deadline.Token).ConfigureAwait(false);
                    await using (var inventory = new SqlCommand(InventorySql, inventoryConnection) { CommandTimeout = Math.Max(1, (int)Manifest.Limits.CommandTimeout.TotalSeconds) })
                    await using (SqlDataReader rows = await inventory.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
                        while (await rows.ReadAsync(deadline.Token).ConfigureAwait(false))
                        {
                            if (databases.Count >= 256) { inventoryTruncated = true; break; }
                            databases.Add(new SqlServerDatabaseIdentity(rows.GetInt32(0), rows.GetString(1)));
                        }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Invalid(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded, new QueryPerformanceTargetStatus("deadline_exceeded", "deadline_exceeded")); }
            catch (TimeoutException) { return Invalid(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded, new QueryPerformanceTargetStatus("deadline_exceeded", "deadline_exceeded")); }
            catch (Exception) { return Invalid(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure, new QueryPerformanceTargetStatus("inventory_failure", "inventory_read_failure")); }
            var targetResponseBudget = new SharedResponseBudget(Manifest.Limits.MaxResponseBytes);
            var dbReader = new DatabaseReader(this, request, major, targetResponseBudget);
            IReadOnlyList<QueryPerformanceReadResult> reads;
            QueryPerformancePlanCacheSample? planCacheSample;
            try
            {
                if (executionPort is null)
                {
                    reads = await SqlServerQueryPerformanceDatabaseOrchestrator.ReadAsync(databases, dbReader, request.Timeout.Value, deadline.Token).ConfigureAwait(false);
                    planCacheSample = dbReader.CompletedPlanCacheSample;
                }
                else
                {
                    QueryPerformanceExecutionBatch execution = await executionPort.ReadDatabasesAsync(databases, request, targetResponseBudget, deadline.Token).ConfigureAwait(false);
                    reads = execution.Reads;
                    planCacheSample = execution.PlanCacheSample;
                    // Injected and production execution paths use the same
                    // target budget contract. The injected port returns raw
                    // accounting; this collector is the single owner that
                    // charges Query Store reads and the completed plan-cache
                    // sample. Plan-cache read statuses are views over the
                    // sample and are deliberately not charged again.
                    foreach (QueryPerformanceReadResult read in reads.Where(static item => !item.IsPlanCacheDerived()))
                        if (read.ResponseBytes > 0) _ = targetResponseBudget.TryAccept(read.ResponseBytes);
                    if (planCacheSample is not null && planCacheSample.ResponseBytes > 0)
                        if (!targetResponseBudget.TryAccept(planCacheSample.ResponseBytes))
                            planCacheSample = planCacheSample.RejectForResponseBudget();
                }
            }
            catch (TimeoutException) { return Invalid(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
            HashSet<int> inventoriedDatabaseIds = databases.Select(static database => database.DatabaseId).ToHashSet();
            // The target-wide sample can contain valid rows for a database
            // that was not returned by inventory. Include those rows only for
            // rejection accounting; known rows are already represented by
            // their per-database read and must not be double-counted.
            // The completed target-wide plan-cache sample is authoritative for
            // fallback observations. Per-database read results carry status
            // and attribution only; they must not add the same sample rows a
            // second time. Query Store rows remain sourced from their reads.
            HashSet<int> queryStoreDatabaseIds = reads.Where(static read => !read.IsPlanCacheDerived()).Select(static read => read.Database.DatabaseId).ToHashSet();
            IEnumerable<QueryPerformanceObservation> sourceObservations = reads.Where(static x => !x.IsPlanCacheDerived()).SelectMany(static x => x.Observations)
                .Concat(planCacheSample?.Observations.Where(item => !queryStoreDatabaseIds.Contains(item.Query.DatabaseId)) ?? reads.Where(static x => x.IsPlanCacheDerived()).SelectMany(static x => x.Observations));
            QueryPerformanceDeduplicationResult allDeduplication = QueryPerformanceBounds.DedupeOverlapWithAccounting(sourceObservations);
            QueryPerformanceObservation[] deduped = allDeduplication.Observations.Where(item => inventoriedDatabaseIds.Contains(item.Query.DatabaseId)).ToArray();
            // Dedupe the raw sequence exactly once.  Filtering out
            // uninventoried rows must not create a second dedupe pass (or turn
            // duplicate overlap into a row-limit loss).
            QueryPerformanceDeduplicationResult deduplication = new(
                deduped,
                allDeduplication.DuplicateCountsByDatabase
                    .Where(pair => inventoriedDatabaseIds.Contains(pair.Key))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value),
                allDeduplication.DuplicateBytesByDatabase
                    .Where(pair => inventoriedDatabaseIds.Contains(pair.Key))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value),
                allDeduplication.DuplicateCountsByDatabase
                    .Where(pair => inventoriedDatabaseIds.Contains(pair.Key))
                    .Sum(static pair => pair.Value),
                allDeduplication.DuplicateBytesByDatabase
                    .Where(pair => inventoriedDatabaseIds.Contains(pair.Key))
                    .Sum(static pair => pair.Value));
            int uninventoriedObservationCount = allDeduplication.Observations.Count - deduped.Length;
            int invalidPlanCacheRows = planCacheSample?.InvalidSourceRowsRead ?? 0;
            bool rejectedUninventoriedObservations = uninventoriedObservationCount > 0 || invalidPlanCacheRows > 0;
            var boundedByDatabase = deduped.GroupBy(static item => item.Query.DatabaseId).SelectMany(group => group.Take(QueryPerformanceBounds.MaximumCandidatesPerDatabase)).ToArray();
            var observations = new QueryPerformanceObservationBatch(boundedByDatabase.Take(QueryPerformanceObservationBatch.MaximumItems).ToArray());
            // Query Store accounting is per database.  The plan-cache reader is a
            // target-wide single flight, so charge its source rows/bytes exactly
            // once instead of multiplying them by the number of fallback DBs.
            long sourceRows = reads.Where(static read => !read.IsPlanCacheDerived()).Sum(static read => (long)read.SourceRowsRead);
            long responseBytes = targetResponseBudget.ResponseBytes;
            if (planCacheSample is not null) sourceRows = checked(sourceRows + planCacheSample.SourceRowsRead);
            bool globalResponseLimit = targetResponseBudget.ByteLimitReached;
            CollectorLossKind sampleLossKind = planCacheSample?.LossKind ?? CollectorLossKind.None;
            bool rowLimitLoss = inventoryTruncated || sampleLossKind == CollectorLossKind.SourceRowLimit || reads.Any(x => x.LossKind == CollectorLossKind.SourceRowLimit) || deduped.Length > boundedByDatabase.Length || boundedByDatabase.Length > observations.Items.Count;
            bool targetResponseLoss = globalResponseLimit || sampleLossKind == CollectorLossKind.ResponseByteLimit || reads.Any(x => x.LossKind == CollectorLossKind.ResponseByteLimit);
            CollectorLossKind otherLoss = rejectedUninventoriedObservations ? CollectorLossKind.OutputValidationFailure : reads.Select(x => x.LossKind).Append(sampleLossKind).Where(x => x is not CollectorLossKind.None and not CollectorLossKind.SourceRowLimit and not CollectorLossKind.ResponseByteLimit).DefaultIfEmpty(CollectorLossKind.None).First();
            var finalCounts = observations.Items.GroupBy(static item => item.Query.DatabaseId).ToDictionary(static group => group.Key, static group => group.Count());
            var dedupedCounts = deduped.GroupBy(static item => item.Query.DatabaseId).ToDictionary(static group => group.Key, static group => group.Count());
            var statuses = reads.Where(read => inventoriedDatabaseIds.Contains(read.Database.DatabaseId)).Select(read =>
            {
                QueryPerformancePlanCacheDatabaseAccounting? sampleAccounting = null;
                if (planCacheSample is not null && read.IsPlanCacheDerived()) planCacheSample.ByDatabase.TryGetValue(read.Database.DatabaseId, out sampleAccounting);
                int sourceRowsForStatus = sampleAccounting?.SourceRowsRead ?? read.SourceRowsRead;
                int responseBytesForStatus = sampleAccounting?.ResponseBytes ?? read.ResponseBytes;
                bool sourceTruncated = read.Truncated || sampleAccounting?.Truncated == true;
                CollectorLossKind sourceLoss = read.LossKind != CollectorLossKind.None ? read.LossKind : sampleAccounting?.LossKind ?? CollectorLossKind.None;
                int finalCount = finalCounts.GetValueOrDefault(read.Database.DatabaseId);
                bool globallyCapped = dedupedCounts.GetValueOrDefault(read.Database.DatabaseId) > finalCount;
                bool overlapReduced = deduplication.DuplicateCountsByDatabase.GetValueOrDefault(read.Database.DatabaseId) > 0;
                if (globallyCapped && finalCount == 0)
                {
                    return new QueryPerformanceDatabaseStatus(read.Database.DatabaseId, QueryPerformanceReadStatus.OutputCapped, "output_capped", read.FallbackAttempted, true, sourceRowsForStatus, responseBytesForStatus, read.SourceState, CollectorLossKind.SourceRowLimit, Math.Max(1, read.MinimumLostItems), false, Math.Max(1, read.MinimumLostBytes));
                }
                if (globallyCapped || overlapReduced || sourceTruncated || sourceLoss != CollectorLossKind.None)
                {
                    CollectorLossKind statusLoss = globallyCapped
                        ? CollectorLossKind.SourceRowLimit
                        : overlapReduced && sourceLoss == CollectorLossKind.None
                            ? CollectorLossKind.DuplicateOverlap
                            : sourceLoss != CollectorLossKind.None ? sourceLoss : CollectorLossKind.SourceRowLimit;
                    int duplicateCount = deduplication.DuplicateCountsByDatabase.GetValueOrDefault(read.Database.DatabaseId);
                    int duplicateBytes = deduplication.DuplicateBytesByDatabase.GetValueOrDefault(read.Database.DatabaseId);
                    // A duplicate-only reduction is exact.  Once a cap, source
                    // loss, or fallback read loss participates, the duplicate
                    // count is only one component of a composite loss and must
                    // not be advertised as an exact total.
                    bool exactDuplicateLoss = statusLoss == CollectorLossKind.DuplicateOverlap
                        && duplicateCount > 0
                        && !globallyCapped
                        && sourceLoss == CollectorLossKind.None;
                    int lostItems = sourceLoss != CollectorLossKind.None ? Math.Max(1, read.MinimumLostItems) : duplicateCount;
                    int lostBytes = sourceLoss != CollectorLossKind.None ? Math.Max(1, read.MinimumLostBytes) : duplicateBytes;
                    bool exactLoss = sourceLoss != CollectorLossKind.None ? read.LossCountIsExact && !globallyCapped : exactDuplicateLoss;
                    QueryPerformanceReadStatus statusValue = sourceLoss != CollectorLossKind.None && read.IsPlanCacheDerived() && finalCount == 0 ? QueryPerformanceReadStatus.OutputCapped : read.Status;
                    string reasonValue = statusValue == QueryPerformanceReadStatus.OutputCapped ? "output_capped" : read.Reason;
                    return new QueryPerformanceDatabaseStatus(read.Database.DatabaseId, statusValue, reasonValue, read.FallbackAttempted, true, sourceRowsForStatus, responseBytesForStatus, read.SourceState, statusLoss, lostItems, exactLoss, lostBytes);
                }
                return new QueryPerformanceDatabaseStatus(read.Database.DatabaseId, read.Status, read.Reason, read.FallbackAttempted, sourceTruncated, sourceRowsForStatus, responseBytesForStatus, read.SourceState, sourceLoss, sourceLoss == CollectorLossKind.None ? 0 : Math.Max(1, read.MinimumLostItems), sourceLoss == CollectorLossKind.None || read.LossCountIsExact, sourceLoss == CollectorLossKind.None ? 0 : Math.Max(1, read.MinimumLostBytes));
            }).ToArray();
            (CollectorPayload payload, int serializedOverflowBytes) = BoundSerializedPayload(observations.Items, statuses, cancellationToken);
            bool serializedPayloadCapped = serializedOverflowBytes > 0;
            targetResponseLoss |= serializedPayloadCapped;
            bool finalStatusLoss = payload.QueryPerformanceStatuses.Any(static status => status.Truncated || status.LossKind != CollectorLossKind.None);
            var lossAccumulator = new QueryPerformanceLossAccumulator();
            bool hasIndependentLoss = targetResponseLoss || rowLimitLoss || otherLoss != CollectorLossKind.None;
            if (deduplication.TotalDuplicateCount > 0)
                lossAccumulator.Add(CollectorLossKind.DuplicateOverlap, deduplication.TotalDuplicateCount, deduplication.TotalDuplicateBytes, !hasIndependentLoss);
            if (rowLimitLoss)
                lossAccumulator.Add(CollectorLossKind.SourceRowLimit, 1, payload.EstimatedSizeBytes, false, deduplication.TotalDuplicateCount > 0);
            if (targetResponseLoss)
                lossAccumulator.Add(CollectorLossKind.ResponseByteLimit, 1, serializedPayloadCapped ? serializedOverflowBytes : Math.Max(1, targetResponseBudget.RejectedResponseBytes), false, lossAccumulator.HasLoss);
            if (rejectedUninventoriedObservations)
            {
                int rejected = Math.Max(1, uninventoriedObservationCount + invalidPlanCacheRows);
                int rejectedBytes = checked(uninventoriedObservationCount * QueryPerformanceObservation.FixedEstimatedBytes + (planCacheSample?.InvalidResponseBytes ?? 0));
                lossAccumulator.Add(CollectorLossKind.OutputValidationFailure, rejected, Math.Max(1, rejectedBytes), false, lossAccumulator.HasLoss);
            }
            if (otherLoss != CollectorLossKind.None && otherLoss is not CollectorLossKind.ResponseByteLimit and not CollectorLossKind.SourceRowLimit)
                lossAccumulator.Add(otherLoss, 1, payload.EstimatedSizeBytes, false, lossAccumulator.HasLoss);
            if (finalStatusLoss && !lossAccumulator.HasLoss)
                lossAccumulator.Add(CollectorLossKind.OutputValidationFailure, 1, payload.EstimatedSizeBytes, false);
            CollectorLossKind lossKind = lossAccumulator.Kind;
            CollectorRunReason lossReason = lossKind switch { CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit, CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit, CollectorLossKind.DuplicateOverlap => CollectorRunReason.OverlapDeduplicated, CollectorLossKind.OutputValidationFailure or CollectorLossKind.IngestionRejection => CollectorRunReason.OutputValidationFailed, _ => CollectorRunReason.Completed };
            CollectorLossEvidence loss = lossAccumulator.HasLoss ? new CollectorLossEvidence(lossKind, lossAccumulator.MinimumLostItems, lossAccumulator.CountIsExact, lossAccumulator.MinimumLostBytes) : CollectorLossEvidence.None;
            CollectorRunOutcome outcome = loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded;
            CollectorRunReason reason = lossReason;
            if (!loss.HasLoss && statuses.Length > 0 && statuses.All(static status => !IsSuccessfulStatus(status.Status)))
            {
                (outcome, reason) = AggregateUnavailable(statuses);
            }
            return new CollectorExecutionResult(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, outcome, reason, payload, new CollectorRunAccounting((int)Math.Min(int.MaxValue, sourceRows), payload.ItemCount, (int)Math.Min(QueryPerformanceBounds.ResponseBytes, responseBytes), payload.EstimatedSizeBytes), loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Invalid(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException ex) when (ex.Number == -2) { return Invalid(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException ex) when (ex.Number is 229 or 297 or 300) { return Invalid(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException ex) when (ex.Number is 20 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40197 or 40501 or 40613) { return Invalid(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException) { return Invalid(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (Exception) { return Invalid(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
    }

    private CollectorExecutionResult Invalid(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason, QueryPerformanceTargetStatus? targetStatus = null)
    {
        targetStatus ??= reason switch
        {
            CollectorRunReason.DeadlineExceeded => new QueryPerformanceTargetStatus("deadline_exceeded", "deadline_exceeded"),
            CollectorRunReason.CircuitCurrentlyOpen => new QueryPerformanceTargetStatus("circuit_open", "circuit_currently_open"),
            CollectorRunReason.RequiredPermissionMissing or CollectorRunReason.TransientTargetFailure or CollectorRunReason.PermanentTargetFailure => new QueryPerformanceTargetStatus("connection_failure", reason switch { CollectorRunReason.RequiredPermissionMissing => "required_permission_missing", CollectorRunReason.TransientTargetFailure => "transient_target_failure", _ => "permanent_target_failure" }),
            CollectorRunReason.TargetVersionUnsupported or CollectorRunReason.TargetUnsupported or CollectorRunReason.TargetPlatformUnsupported or CollectorRunReason.TargetEditionUnsupported or CollectorRunReason.CapabilityMissing or CollectorRunReason.CapabilityProfileMissing or CollectorRunReason.CapabilityProfileStale => new QueryPerformanceTargetStatus("unsupported", reason switch { CollectorRunReason.TargetVersionUnsupported => "target_version_unsupported", CollectorRunReason.TargetPlatformUnsupported => "target_platform_unsupported", CollectorRunReason.TargetEditionUnsupported => "target_edition_unsupported", CollectorRunReason.CapabilityMissing => "capability_missing", CollectorRunReason.CapabilityProfileMissing => "capability_profile_missing", CollectorRunReason.CapabilityProfileStale => "capability_profile_stale", _ => "target_unsupported" }),
            CollectorRunReason.OutputValidationFailed => new QueryPerformanceTargetStatus("output_invalid", "output_validation_failed"),
            CollectorRunReason.LeaseOwnershipLost => new QueryPerformanceTargetStatus("lease_lost", "lease_ownership_lost"),
            _ => new QueryPerformanceTargetStatus("connection_failure", "permanent_target_failure"),
        };
        return new CollectorExecutionResult(request.TargetId, request.TargetRevision, Manifest.Id, Manifest.ManifestVersion.Value, Manifest.OutputSchemaVersion.Value, outcome, reason, new CollectorPayload(queryPerformanceTargetStatus: targetStatus), new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
    }

    private static bool IsSuccessfulStatus(QueryPerformanceReadStatus status) => status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty or QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty;
    internal static (CollectorPayload Payload, int OverflowBytes) BoundSerializedPayload(IReadOnlyList<QueryPerformanceObservation> sourceItems, IReadOnlyList<QueryPerformanceDatabaseStatus> sourceStatuses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(sourceStatuses);
        var originalItems = sourceItems.ToArray();
        var originalStatuses = sourceStatuses.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var originalCounts = sourceItems.GroupBy(static item => item.Query.DatabaseId).ToDictionary(static group => group.Key, static group => group.Count());
        byte[] serialized = QueryPerformancePersistencePayload.Serialize(originalItems, originalStatuses, null);
        int originalLength = serialized.Length;
        if (serialized.Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes)
            return (new CollectorPayload(queryPerformance: new QueryPerformanceObservationBatch(originalItems), queryPerformanceStatuses: originalStatuses), 0);

        // The payload is monotonic in retained observations except for the
        // bounded status transition when a database loses its final row. Use a
        // binary search for the largest candidate, then recheck a small bounded
        // neighborhood after status growth. This avoids the former O(nÂ²)
        // reserialization loop while retaining exact final-size validation.
        int low = 0;
        int high = originalItems.Length;
        (QueryPerformanceObservation[] Items, QueryPerformanceDatabaseStatus[] Statuses, byte[] Bytes) Candidate(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryPerformanceObservation[] items = originalItems.Take(count).ToArray();
            var remainingCounts = items.GroupBy(static item => item.Query.DatabaseId).ToDictionary(static group => group.Key, static group => group.Count());
            QueryPerformanceDatabaseStatus[] statuses = originalStatuses.Select(status =>
            {
                if (status.Status is not (QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.PlanCacheRows) || remainingCounts.GetValueOrDefault(status.DatabaseId) >= originalCounts.GetValueOrDefault(status.DatabaseId)) return status;
                bool empty = remainingCounts.GetValueOrDefault(status.DatabaseId) == 0;
                return new QueryPerformanceDatabaseStatus(status.DatabaseId, empty ? QueryPerformanceReadStatus.OutputCapped : status.Status, empty ? "output_capped" : status.Reason, status.FallbackAttempted, true, status.SourceRowsRead, status.ResponseBytes, status.SourceState, CollectorLossKind.ResponseByteLimit, 1, false, 1);
            }).ToArray();
            return (items, statuses, QueryPerformancePersistencePayload.Serialize(items, statuses, null));
        }

        while (low < high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int mid = low + ((high - low + 1) / 2);
            if (Candidate(mid).Bytes.Length <= QueryPerformancePersistencePayload.MaximumSerializedBytes) low = mid;
            else high = mid - 1;
        }
        var bounded = Candidate(low);
        for (int adjustment = 0; bounded.Bytes.Length > QueryPerformancePersistencePayload.MaximumSerializedBytes && low > 0 && adjustment < 64; adjustment++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            low--;
            bounded = Candidate(low);
        }
        if (bounded.Bytes.Length > QueryPerformancePersistencePayload.MaximumSerializedBytes)
            throw new InvalidDataException("Query performance status payload exceeds the accepted response bound.");
        return (new CollectorPayload(queryPerformance: new QueryPerformanceObservationBatch(bounded.Items), queryPerformanceStatuses: bounded.Statuses), Math.Max(0, originalLength - QueryPerformancePersistencePayload.MaximumSerializedBytes));
    }
    private static (CollectorRunOutcome Outcome, CollectorRunReason Reason) AggregateUnavailable(IReadOnlyList<QueryPerformanceDatabaseStatus> statuses)
    {
        if (statuses.Any(static status => status.Status is QueryPerformanceReadStatus.QueryStoreTimedOut or QueryPerformanceReadStatus.PlanCacheTimedOut)) return (CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded);
        if (statuses.Any(static status => status.Status is QueryPerformanceReadStatus.QueryStorePermissionDenied or QueryPerformanceReadStatus.PlanCachePermissionDenied)) return (CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        if (statuses.Any(static status => status.Status is QueryPerformanceReadStatus.QueryStoreReadFailure or QueryPerformanceReadStatus.PlanCacheReadFailure)) return (CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure);
        return (CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
    }

    private sealed class DatabaseReader : IQueryPerformanceDatabaseReader, IQueryPerformanceSourceReader
    {
        private readonly SqlServerQueryPerformanceCollector owner; private readonly CollectorExecutionRequest request; private readonly int major; private readonly SharedResponseBudget targetResponseBudget; private readonly QueryPerformanceSingleFlight<QueryPerformancePlanCacheSample> planCacheSample; private QueryPerformancePlanCacheSample? completedPlanCacheSample;
        public DatabaseReader(SqlServerQueryPerformanceCollector owner, CollectorExecutionRequest request, int major, SharedResponseBudget targetResponseBudget) { this.owner = owner; this.request = request; this.major = major; this.targetResponseBudget = targetResponseBudget ?? throw new ArgumentNullException(nameof(targetResponseBudget)); planCacheSample = new QueryPerformanceSingleFlight<QueryPerformancePlanCacheSample>(LoadPlanCacheAsync); }
        public QueryPerformancePlanCacheSample? CompletedPlanCacheSample => completedPlanCacheSample;
        public ValueTask<QueryPerformanceReadResult> ReadDatabaseAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken) => targetResponseBudget.IsExhausted ? ValueTask.FromResult(new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.OutputCapped, [], "output_capped", false, true, 0, 0, QueryStoreState.ReadFailure, CollectorLossKind.ResponseByteLimit)) : QueryPerformanceFallbackCoordinator.ReadAsync(database, this, CanUsePlanCache(), cancellationToken);
        public ValueTask<QueryPerformanceReadResult> ReadQueryStoreAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken) => QueryStore(database, cancellationToken);
        public ValueTask<QueryPerformanceReadResult> ReadPlanCacheAsync(SqlServerDatabaseIdentity database, CancellationToken cancellationToken) => PlanCache(database, cancellationToken);
        private async ValueTask<QueryPerformanceReadResult> QueryStore(SqlServerDatabaseIdentity database, CancellationToken token)
        {
            try
            {
                await using SqlConnection connection = await owner.ConnectionFactory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
                connection.ChangeDatabase(database.Name);
                await using var command = new SqlCommand(owner.Asset.GetQuery(major), connection) { CommandTimeout = Math.Max(1, (int)owner.Manifest.Limits.CommandTimeout.TotalSeconds) };
                command.Parameters.Add("probe_rows", System.Data.SqlDbType.Int).Value = QueryPerformanceBounds.ProbeRows;
                command.Parameters.Add("window_start", System.Data.SqlDbType.DateTime2).Value = DateTime.UtcNow.Add(-QueryPerformanceBounds.DefaultWindow - QueryPerformanceBounds.Overlap);
                command.Parameters.Add("window_end", System.Data.SqlDbType.DateTime2).Value = DateTime.UtcNow;
                await using SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreUnsupported, [], "query_store_probe_missing", false, false, 0, 0);
                string stateValue = reader.IsDBNull(0) ? "UNSUPPORTED" : reader.GetString(0);
                QueryStoreState state = ParseState(stateValue);
                QueryPerformanceReadStatus probeStatus = stateValue.ToUpperInvariant() switch { "READ_WRITE" or "READ_ONLY" => QueryPerformanceReadStatus.QueryStoreRows, "OFF" or "DISABLED" => QueryPerformanceReadStatus.QueryStoreDisabled, "PERMISSION_DENIED" => QueryPerformanceReadStatus.QueryStorePermissionDenied, _ => QueryPerformanceReadStatus.QueryStoreUnsupported };
                if (probeStatus is not QueryPerformanceReadStatus.QueryStoreRows) return new QueryPerformanceReadResult(database, probeStatus, [], probeStatus switch { QueryPerformanceReadStatus.QueryStoreDisabled => "query_store_disabled", QueryPerformanceReadStatus.QueryStorePermissionDenied => "query_store_permission_denied", _ => "query_store_unsupported" }, false, false, 0, 0, state);
                if (!await reader.NextResultAsync(token).ConfigureAwait(false)) return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreEmpty, [], "query_store_empty", false, false, 0, 0, state);
                ActivityCollectorReadResult parsed = await owner.ReadPayloadAsync(request, reader, targetResponseBudget, token).ConfigureAwait(false);
                QueryPerformanceReadStatus status = parsed.Payload.QueryPerformance.Items.Count == 0 ? (parsed.Loss.Kind == CollectorLossKind.ResponseByteLimit ? QueryPerformanceReadStatus.OutputCapped : QueryPerformanceReadStatus.QueryStoreEmpty) : QueryPerformanceReadStatus.QueryStoreRows;
                return new QueryPerformanceReadResult(database, status, parsed.Payload.QueryPerformance.Items, status == QueryPerformanceReadStatus.QueryStoreEmpty ? "query_store_empty" : "query_store_read", false, parsed.Loss.HasLoss, parsed.SourceRowsRead, parsed.ResponseBytes, state, parsed.Loss.Kind, parsed.Loss.MinimumLostItems, parsed.Loss.CountIsExact, parsed.Loss.MinimumLostBytes);
            }
            catch (OperationCanceledException) { throw; }
            catch (SqlException ex) when (ex.Number is 229 or 297 or 300) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStorePermissionDenied, [], "query_store_permission_denied", false, false, 0, 0, QueryStoreState.PermissionDenied); }
            catch (SqlException ex) when (ex.Number == -2) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreTimedOut, [], "query_store_timeout", false, false, 0, 0, QueryStoreState.TimedOut); }
            catch (TimeoutException) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreTimedOut, [], "query_store_timeout", false, false, 0, 0, QueryStoreState.TimedOut); }
            catch (Exception) { return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.QueryStoreReadFailure, [], "query_store_read_failure", false, false, 0, 0, QueryStoreState.ReadFailure); }
        }
        private async ValueTask<QueryPerformanceReadResult> PlanCache(SqlServerDatabaseIdentity database, CancellationToken token)
        {
            QueryPerformancePlanCacheSample sample = await planCacheSample.GetAsync(token).ConfigureAwait(false);
            return BuildPlanCacheReadResult(database, sample);
        }
        private async Task<QueryPerformancePlanCacheSample> LoadPlanCacheAsync(CancellationToken token)
        {
            if (targetResponseBudget.IsExhausted) return completedPlanCacheSample = new QueryPerformancePlanCacheSample([], 0, 0, CollectorLossKind.ResponseByteLimit, true);
            await using SqlConnection connection = await owner.ConnectionFactory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
            await using var command = new SqlCommand(PlanCacheSql, connection) { CommandTimeout = Math.Max(1, (int)owner.Manifest.Limits.CommandTimeout.TotalSeconds) };
            DateTime sampleEnd = DateTime.UtcNow; command.Parameters.Add("probe_rows", System.Data.SqlDbType.Int).Value = QueryPerformanceBounds.ProbeRows; command.Parameters.Add("sample_start", System.Data.SqlDbType.DateTime2).Value = sampleEnd.AddMinutes(-5); command.Parameters.Add("sample_end", System.Data.SqlDbType.DateTime2).Value = sampleEnd;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            QueryPerformancePlanCacheParseResult parsed = await owner.ReadPlanCachePayloadAsync(request, reader, targetResponseBudget, token).ConfigureAwait(false);
            QueryPerformancePlanCacheSample sample = new(parsed.Observations, parsed.SourceRowsRead, parsed.ResponseBytes, parsed.Loss.Kind, parsed.Loss.HasLoss, parsed.PerDatabase, parsed.InvalidRows, parsed.InvalidBytes);
            completedPlanCacheSample = sample;
            return sample;
        }
        private bool CanUsePlanCache()
        {
            string required = major == 15 ? "server.view-state" : "server.view-performance-state";
            return request.CapabilityProfile.Permissions.Any(x => x.Scope == PermissionEvidenceScope.Server && x.Outcome == PermissionEvidenceOutcome.Granted && x.PermissionId.Value == required);
        }
    }

    /// <summary>Builds one database view over the completed target-wide sample without copying global loss.</summary>
    internal static QueryPerformanceReadResult BuildPlanCacheReadResult(SqlServerDatabaseIdentity database, QueryPerformancePlanCacheSample sample)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(sample);
        QueryPerformanceObservation[] selected = sample.Observations.Where(x => x.Query.DatabaseId == database.DatabaseId).ToArray();
        sample.ByDatabase.TryGetValue(database.DatabaseId, out QueryPerformancePlanCacheDatabaseAccounting? accounting);
        // A target-wide probe/byte loss is not proof that this database lost
        // rows. Only the per-database bucket can mark a database capped;
        // ambiguous global loss remains target-level evidence.
        bool databaseAffected = accounting is not null && (accounting.Truncated || accounting.LossKind != CollectorLossKind.None);
        if (selected.Length == 0 && databaseAffected)
            return new QueryPerformanceReadResult(database, QueryPerformanceReadStatus.OutputCapped, [], "output_capped", true, true, accounting?.SourceRowsRead ?? 0, accounting?.ResponseBytes ?? 0, QueryStoreState.ReadFailure, accounting?.LossKind ?? CollectorLossKind.OutputValidationFailure, accounting?.MinimumLostItems ?? 1, accounting?.LossCountIsExact ?? false, accounting?.MinimumLostBytes ?? 1);
        QueryPerformanceReadStatus status = selected.Length == 0 ? QueryPerformanceReadStatus.PlanCacheEmpty : QueryPerformanceReadStatus.PlanCacheRows;
        bool databaseTruncated = accounting?.Truncated == true;
        return new QueryPerformanceReadResult(database, status, selected, selected.Length == 0 ? "plan_cache_empty" : "plan_cache_fallback", true, databaseTruncated, accounting?.SourceRowsRead ?? 0, accounting?.ResponseBytes ?? 0, null, databaseTruncated ? accounting?.LossKind ?? CollectorLossKind.OutputValidationFailure : CollectorLossKind.None, databaseTruncated ? accounting?.MinimumLostItems ?? 1 : 0, databaseTruncated ? accounting?.LossCountIsExact ?? false : true, databaseTruncated ? accounting?.MinimumLostBytes ?? 1 : 0);
    }
    private protected override async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(CollectorExecutionRequest request, SqlDataReader reader, CancellationToken cancellationToken)
        => (await ReadPayloadCoreAsync(request, reader, false, null, cancellationToken).ConfigureAwait(false)).Result;

    private async ValueTask<ActivityCollectorReadResult> ReadPayloadAsync(CollectorExecutionRequest request, SqlDataReader reader, SharedResponseBudget targetResponseBudget, CancellationToken cancellationToken)
        => (await ReadPayloadCoreAsync(request, reader, false, targetResponseBudget, cancellationToken).ConfigureAwait(false)).Result;

    internal async ValueTask<QueryPerformancePlanCacheParseResult> ReadPlanCachePayloadAsync(CollectorExecutionRequest request, DbDataReader reader, SharedResponseBudget targetResponseBudget, CancellationToken cancellationToken)
    {
        (ActivityCollectorReadResult result, IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting> perDatabase, int invalidRows, int invalidBytes) = await ReadPayloadCoreAsync(request, reader, true, targetResponseBudget, cancellationToken).ConfigureAwait(false);
        return new QueryPerformancePlanCacheParseResult(result.Payload.QueryPerformance.Items, result.SourceRowsRead, result.ResponseBytes, result.Loss, perDatabase, invalidRows, invalidBytes);
    }

    private async ValueTask<(ActivityCollectorReadResult Result, IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting> PerDatabase, int InvalidRows, int InvalidBytes)> ReadPayloadCoreAsync(CollectorExecutionRequest request, DbDataReader reader, bool collectPlanCacheAccounting, SharedResponseBudget? targetResponseBudget, CancellationToken cancellationToken)
    {
        var items = new List<QueryPerformanceObservation>(); var plansByQuery = new Dictionary<(int DatabaseId, string Query), int>(); var perDatabase = new Dictionary<int, PlanCacheAccountingBuilder>(); var budget = new BoundedCollectorReadBudget(QueryPerformanceBounds.ProbeRows, Manifest.Limits.MaxResponseBytes); int loss = 0; int invalidRows = 0; int invalidBytes = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!budget.TryBeginRow())
            {
                // The final row is a deliberate max+1 probe.  The bounded
                // reader must still attribute that probe to its database when
                // the first column is a safe database id, while keeping it out
                // of emitted observations and response-byte accounting.
                if (collectPlanCacheAccounting)
                {
                    try
                    {
                        int probeDatabaseId = reader.GetInt32(0);
                        if (probeDatabaseId is > 0 and <= 32767)
                        {
                            if (!perDatabase.TryGetValue(probeDatabaseId, out PlanCacheAccountingBuilder? probeAccounting)) perDatabase[probeDatabaseId] = probeAccounting = new PlanCacheAccountingBuilder();
                            probeAccounting.SourceRowsRead++;
                            probeAccounting.MarkLoss(CollectorLossKind.SourceRowLimit, 1, 1, false);
                        }
                        else invalidRows++;
                    }
                    catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or IndexOutOfRangeException)
                    {
                        invalidRows++;
                    }
                }
                break;
            }
            bool rowBytesSettled = false; bool stopReading = false; bool rejectionAlreadyCounted = false; int databaseId = 0; bool hasDatabase = false; PlanCacheAccountingBuilder? accounting = null;
            bool AcceptBytes(int bytes)
            {
                bool accepted = (targetResponseBudget is null || targetResponseBudget.TryAccept(bytes)) && budget.TryAcceptResponseBytes(bytes);
                if (accepted)
                {
                    if (accounting is not null) accounting.ResponseBytes += bytes; else invalidBytes += bytes;
                }
                else
                {
                    if (accounting is not null && !rejectionAlreadyCounted) accounting.MarkLoss(CollectorLossKind.ResponseByteLimit, 1, bytes, false);
                    stopReading = true;
                }
                rowBytesSettled = true;
                return accepted;
            }
            try
            {
                databaseId = reader.GetInt32(0); hasDatabase = databaseId is > 0 and <= 32767; if (collectPlanCacheAccounting && hasDatabase) { if (!perDatabase.TryGetValue(databaseId, out accounting)) perDatabase[databaseId] = accounting = new PlanCacheAccountingBuilder(); accounting.SourceRowsRead++; }
                string query = reader.IsDBNull(1) ? "" : reader.GetString(1).ToLowerInvariant(); string? planValue = reader.IsDBNull(2) ? null : reader.GetString(2).ToLowerInvariant();
                if (query.Length != 64 || !query.All(Uri.IsHexDigit)) { loss++; if (accounting is not null) { accounting.MarkLoss(CollectorLossKind.OutputValidationFailure, 1, 1, true); rejectionAlreadyCounted = true; } else invalidRows++; if (!AcceptBytes(1)) break; continue; }
                QueryOpaqueIdentity identity = new(databaseId, query); PlanOpaqueIdentity? plan = planValue is null ? null : new PlanOpaqueIdentity(identity, planValue);
                int planCount = plansByQuery.TryGetValue((databaseId, query), out int existingPlanCount) ? existingPlanCount : 0;
                if (planCount >= QueryPerformanceBounds.MaximumPlansPerQuery) { loss++; if (accounting is not null) { accounting.MarkLoss(CollectorLossKind.OutputValidationFailure, 1, 1, true); rejectionAlreadyCounted = true; } else invalidRows++; if (!AcceptBytes(1)) break; continue; }
                plansByQuery[(databaseId, query)] = planCount + 1;
                QueryPerformanceMetricSet metrics = new(ReadLong(reader, 5), ReadLong(reader, 6), ReadLong(reader, 7), ReadLong(reader, 8), ReadLong(reader, 9), ReadLong(reader, 10));
                DateTimeOffset start = ReadUtc(reader, 11), end = ReadUtc(reader, 12), observed = ReadUtc(reader, 13); QueryStoreState state = ParseState(reader.IsDBNull(4) ? "unsupported" : reader.GetString(4)); QueryPerformanceSource source = reader.IsDBNull(3) || reader.GetString(3) != "query_store" ? QueryPerformanceSource.PlanCache : QueryPerformanceSource.QueryStore;
                QueryPerformanceObservation observation = new(request.TargetId, request.TargetRevision, identity, plan, source, state, source == QueryPerformanceSource.QueryStore ? QueryMetricSemantics.QueryStoreInterval : QueryMetricSemantics.PlanCacheCumulative, metrics, start, end, observed, QueryCoverage.Complete, !reader.IsDBNull(14) && reader.GetBoolean(14), !reader.IsDBNull(15) && reader.GetBoolean(15));
                if (!AcceptBytes(QueryPerformanceObservation.FixedEstimatedBytes)) break;
                items.Add(observation); if (accounting is not null) accounting.EmittedRows++;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or ArgumentException or OverflowException) { loss++; if (accounting is not null) { accounting.MarkLoss(CollectorLossKind.OutputValidationFailure, 1, 1, true); rejectionAlreadyCounted = true; } else invalidRows++; }
            finally
            {
                if (!rowBytesSettled)
                {
                    try { AcceptBytes(1); } catch (InvalidOperationException) { }
                }
            }
            if (stopReading) break;
        }
        CollectorLossEvidence? parseLoss = loss > 0 ? new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, loss, false, loss) : null;
        if (targetResponseBudget?.ByteLimitReached == true) parseLoss = new CollectorLossEvidence(CollectorLossKind.ResponseByteLimit, 1, false, Math.Max(1, targetResponseBudget.RejectedResponseBytes));
        ActivityCollectorReadResult result = CompleteRead(new CollectorPayload(queryPerformance: new QueryPerformanceObservationBatch(items)), budget, parseLoss);
        var values = perDatabase.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToContract());
        return (result, values, invalidRows, invalidBytes);
    }
    private sealed class PlanCacheAccountingBuilder
    {
        public int SourceRowsRead;
        public int EmittedRows;
        public int RejectedRows;
        public int ResponseBytes;
        public int MinimumLostBytes;
        public int MinimumLostItems;
        public bool LossCountIsExact = true;
        public CollectorLossKind LossKind = CollectorLossKind.None;
        public bool Truncated;
        public void MarkLoss(CollectorLossKind kind, int minimumItems, int minimumBytes, bool exact)
        {
            RejectedRows = checked(RejectedRows + minimumItems);
            MinimumLostItems = checked(MinimumLostItems + minimumItems);
            MinimumLostBytes = checked(MinimumLostBytes + minimumBytes);
            LossCountIsExact &= exact;
            Truncated = true;
            LossKind = LossKind switch
            {
                CollectorLossKind.ResponseByteLimit => LossKind,
                _ when kind == CollectorLossKind.ResponseByteLimit => kind,
                _ when LossKind == CollectorLossKind.None => kind,
                _ => LossKind,
            };
        }
        public QueryPerformancePlanCacheDatabaseAccounting ToContract() => new(SourceRowsRead, EmittedRows, RejectedRows, ResponseBytes, LossKind, Truncated, MinimumLostBytes, LossCountIsExact, MinimumLostItems);
    }
    private static long? ReadLong(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static DateTimeOffset ReadUtc(DbDataReader r, int i) => QueryPerformanceRowParser.ReadUtc(r.GetValue(i));
    private static QueryStoreState ParseState(string value) => value.ToUpperInvariant() switch { "READ_WRITE" => QueryStoreState.ReadWrite, "READ_ONLY" => QueryStoreState.ReadOnly, "DISABLED" => QueryStoreState.Disabled, "PERMISSION_DENIED" => QueryStoreState.PermissionDenied, "TIMED_OUT" => QueryStoreState.TimedOut, "READ_FAILURE" or "FALLBACK" => QueryStoreState.ReadFailure, _ => QueryStoreState.Unsupported };
}

internal sealed record QueryPerformancePlanCacheParseResult(
    IReadOnlyList<QueryPerformanceObservation> Observations,
    int SourceRowsRead,
    int ResponseBytes,
    CollectorLossEvidence Loss,
    IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting> PerDatabase,
    int InvalidRows,
    int InvalidBytes);

/// <summary>Provider-shape tolerant UTC conversion for SQL datetime and datetimeoffset columns.</summary>
public static class QueryPerformanceRowParser
{
    public static DateTimeOffset ReadUtc(object value)
    {
        DateTimeOffset utc = value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => throw new InvalidCastException("Query performance timestamps must be datetimeoffset or datetime values."),
        };
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

internal sealed class SharedResponseBudget
{
    private readonly object gate = new();
    private readonly int maximumBytes;
    public SharedResponseBudget(int maximumBytes) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes); this.maximumBytes = maximumBytes; }
    public int ResponseBytes { get { lock (gate) return responseBytes; } }
    public bool ByteLimitReached { get { lock (gate) return byteLimitReached; } }
    public int RejectedResponseBytes { get { lock (gate) return rejectedResponseBytes; } }
    public bool IsExhausted { get { lock (gate) return byteLimitReached || responseBytes >= maximumBytes; } }
    private int responseBytes;
    private bool byteLimitReached;
    private int rejectedResponseBytes;
    public bool TryAccept(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (gate)
        {
            if (byteLimitReached || responseBytes > maximumBytes - bytes) { byteLimitReached = true; rejectedResponseBytes = SaturatingAdd(rejectedResponseBytes, bytes); return false; }
            responseBytes += bytes; return true;
        }
    }
    private static int SaturatingAdd(int left, int right) => left > int.MaxValue - right ? int.MaxValue : left + right;
}
