using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

public enum QueryPerformanceReadStatus { QueryStoreEmpty = 1, QueryStoreRows = 2, QueryStoreDisabled = 3, QueryStoreUnsupported = 4, QueryStorePermissionDenied = 5, QueryStoreReadFailure = 6, QueryStoreTimedOut = 7, PlanCacheRows = 8, PlanCacheEmpty = 9, PlanCachePermissionDenied = 10, PlanCacheReadFailure = 11, PlanCacheTimedOut = 12, OutputCapped = 13 }

public static class QueryPerformanceReadStatusExtensions
{
    public static bool IsPlanCacheDerived(this QueryPerformanceReadResult read)
        => read.FallbackAttempted || read.Status is QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty or QueryPerformanceReadStatus.PlanCachePermissionDenied or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheTimedOut;
}

/// <summary>Bounded target-level evidence for failures before a per-database source attempt exists.</summary>
public sealed record QueryPerformanceTargetStatus
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["inventory_failure"] = ["inventory_read_failure"],
        ["connection_failure"] = ["transient_target_failure", "permanent_target_failure", "required_permission_missing"],
        ["deadline_exceeded"] = ["deadline_exceeded"],
        ["circuit_open"] = ["circuit_currently_open"],
        ["unsupported"] = ["target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_profile_missing", "capability_profile_stale", "capability_missing"],
        ["output_invalid"] = ["output_validation_failed"],
        ["lease_lost"] = ["lease_ownership_lost"],
    };
    public QueryPerformanceTargetStatus(string status, string reason)
    {
        if (!Allowed.TryGetValue(status, out string[]? reasons) || !reasons.Contains(reason, StringComparer.Ordinal)) throw new ArgumentException("Target query performance status is not allowlisted.");
        Status = status; Reason = reason;
    }
    public string Status { get; }
    public string Reason { get; }
}

/// <summary>Stable inventory identity used by the catalog/database switcher; callers cannot provide SQL text.</summary>
public sealed record SqlServerDatabaseIdentity
{
    public SqlServerDatabaseIdentity(int databaseId, string name)
    {
        if (databaseId is <= 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(databaseId));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || name.Contains('\0')) throw new ArgumentException("Database identity is invalid.", nameof(name));
        DatabaseId = databaseId; Name = name;
    }
    public int DatabaseId { get; }
    public string Name { get; }
}

public sealed class QueryPerformanceReadResult
{
    public const int MaximumResponseBytes = 8 * 1024 * 1024;
    public QueryPerformanceReadResult(SqlServerDatabaseIdentity database, QueryPerformanceReadStatus status, IReadOnlyList<QueryPerformanceObservation> observations, string reason, bool fallbackAttempted, bool truncated, int sourceRowsRead, int responseBytes, QueryStoreState? sourceState = null, CollectorLossKind lossKind = CollectorLossKind.None, int minimumLostItems = 0, bool lossCountIsExact = true, int minimumLostBytes = 0) { Database = database ?? throw new ArgumentNullException(nameof(database)); if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status)); Observations = observations ?? throw new ArgumentNullException(nameof(observations)); Status = status; Reason = RequireReason(reason); FallbackAttempted = fallbackAttempted; Truncated = truncated; if (sourceRowsRead < 0 || responseBytes < 0 || responseBytes > MaximumResponseBytes) throw new ArgumentOutOfRangeException(nameof(responseBytes)); if (!Enum.IsDefined(lossKind)) throw new ArgumentOutOfRangeException(nameof(lossKind)); if (minimumLostItems < 0 || minimumLostBytes < 0 || minimumLostBytes > MaximumResponseBytes || lossKind == CollectorLossKind.None && (minimumLostItems != 0 || minimumLostBytes != 0 || !lossCountIsExact) || lossKind != CollectorLossKind.None && !truncated) throw new ArgumentOutOfRangeException(nameof(minimumLostItems)); SourceRowsRead = sourceRowsRead; ResponseBytes = responseBytes; SourceState = sourceState; LossKind = lossKind; MinimumLostItems = minimumLostItems; LossCountIsExact = lossCountIsExact; MinimumLostBytes = minimumLostBytes; ValidateObservations(); }
    public SqlServerDatabaseIdentity Database { get; }
    public QueryPerformanceReadStatus Status { get; }
    public IReadOnlyList<QueryPerformanceObservation> Observations { get; }
    public string Reason { get; }
    public bool FallbackAttempted { get; }
    public bool Truncated { get; }
    public int SourceRowsRead { get; }
    public int ResponseBytes { get; }
    public QueryStoreState? SourceState { get; }
    public CollectorLossKind LossKind { get; }
    public int MinimumLostItems { get; }
    public bool LossCountIsExact { get; }
    public int MinimumLostBytes { get; }
    private void ValidateObservations()
    {
        bool queryStoreRows = Status == QueryPerformanceReadStatus.QueryStoreRows;
        bool planCacheRows = Status == QueryPerformanceReadStatus.PlanCacheRows;
        bool outputCapped = Status == QueryPerformanceReadStatus.OutputCapped;
        bool rowStatus = queryStoreRows || planCacheRows;
        if (rowStatus && Observations.Count == 0) throw new ArgumentException("A row status must carry at least one observation.");
        if (!rowStatus && Observations.Count != 0) throw new ArgumentException("A non-row source status cannot carry observations.");
        if (planCacheRows && !FallbackAttempted) throw new ArgumentException("Plan-cache rows require an attempted fallback.");
        foreach (QueryPerformanceObservation item in Observations)
        {
            if (item.Query.DatabaseId != Database.DatabaseId) throw new ArgumentException("Source observations must match their database result.");
            if (queryStoreRows && (item.Source != QueryPerformanceSource.QueryStore || item.SourceState is not (QueryStoreState.ReadWrite or QueryStoreState.ReadOnly))) throw new ArgumentException("Query Store rows must be read/write or read-only Query Store observations.");
            if (planCacheRows && (item.Source != QueryPerformanceSource.PlanCache || item.Semantics == QueryMetricSemantics.QueryStoreInterval)) throw new ArgumentException("Plan-cache rows must be plan-cache observations.");
        }
        if (!rowStatus && !outputCapped && (LossKind is CollectorLossKind.SourceRowLimit or CollectorLossKind.ResponseByteLimit || Truncated && LossKind == CollectorLossKind.None)) throw new ArgumentException("Non-row source statuses cannot claim global row/byte loss.");
    }
    private static string RequireReason(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? throw new ArgumentException("A bounded source reason is required.", nameof(value)) : value;
}

public static class QueryPerformanceBounds
{
    public const int MaximumDatabases = 256;
    public const int MaximumCandidatesPerDatabase = 2_000;
    public const int MaximumObservationsPerDatabase = 20_000;
    public const int MaximumPlansPerQuery = 50;
    public const int ProbeRows = 2_001;
    public const int ResponseBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);
    public static IReadOnlyList<QueryPerformanceObservation> DedupeOverlap(IEnumerable<QueryPerformanceObservation> observations)
        => DedupeOverlapWithAccounting(observations).Observations;

    /// <summary>Deduplicates overlapping Query Store intervals while retaining exact bounded duplicate evidence.</summary>
    public static QueryPerformanceDeduplicationResult DedupeOverlapWithAccounting(IEnumerable<QueryPerformanceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var duplicateCounts = new Dictionary<int, int>();
        var duplicateBytes = new Dictionary<int, int>();
        int totalCount = 0;
        int totalBytes = 0;
        var selected = new List<QueryPerformanceObservation>();
        foreach (var group in observations.GroupBy(x => (x.Query.DatabaseId, x.Query.QueryFingerprint, Plan: x.Plan?.PlanFingerprint, x.IntervalStartUtc, x.IntervalEndUtc, x.Source)))
        {
            var ordered = group.OrderByDescending(x => x.ObservedAtUtc).ThenBy(x => x.Plan?.PlanFingerprint, StringComparer.Ordinal).ToArray();
            selected.Add(ordered[0]);
            int duplicates = ordered.Length - 1;
            if (duplicates > 0)
            {
                duplicateCounts[group.Key.DatabaseId] = duplicateCounts.GetValueOrDefault(group.Key.DatabaseId) + duplicates;
                int bytes = checked(duplicates * QueryPerformanceObservation.FixedEstimatedBytes);
                duplicateBytes[group.Key.DatabaseId] = duplicateBytes.GetValueOrDefault(group.Key.DatabaseId) + bytes;
                totalCount = checked(totalCount + duplicates);
                totalBytes = checked(totalBytes + bytes);
            }
        }
        return new QueryPerformanceDeduplicationResult(
            selected.OrderBy(x => x.Query.DatabaseId).ThenBy(x => x.IntervalEndUtc).ThenBy(x => x.Query.QueryFingerprint, StringComparer.Ordinal).ToArray(),
            duplicateCounts, duplicateBytes, totalCount, totalBytes);
    }
}

public sealed record QueryPerformanceDeduplicationResult(
    IReadOnlyList<QueryPerformanceObservation> Observations,
    IReadOnlyDictionary<int, int> DuplicateCountsByDatabase,
    IReadOnlyDictionary<int, int> DuplicateBytesByDatabase,
    int TotalDuplicateCount,
    int TotalDuplicateBytes)
{
    public bool CountIsExact => TotalDuplicateCount == DuplicateCountsByDatabase.Values.Sum();
}

public sealed class QueryPerformanceCacheBaseline
{
    private readonly Dictionary<(Guid Target, int Database, string Query, string Plan), CacheCounter> counters = new();
    public CacheCounter Observe(Guid targetId, int databaseId, string queryFingerprint, long cumulativeExecutions, long cumulativeCpuMilliseconds, DateTimeOffset observedAtUtc, string? planFingerprint = null)
    {
        if (targetId == Guid.Empty) throw new ArgumentException("Target is required.", nameof(targetId));
        QueryOpaqueIdentity.RequireDigest(queryFingerprint, nameof(queryFingerprint));
        string plan = planFingerprint is null ? "none" : QueryOpaqueIdentity.RequireDigest(planFingerprint, nameof(planFingerprint));
        if (cumulativeExecutions < 0 || cumulativeCpuMilliseconds < 0 || observedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cumulativeExecutions), "Cache counters must be non-negative UTC values.");
        var key = (targetId, databaseId, queryFingerprint.ToLowerInvariant(), plan);
        if (!counters.TryGetValue(key, out CacheCounter prior) || cumulativeExecutions < prior.CumulativeExecutions || cumulativeCpuMilliseconds < prior.CumulativeCpuMilliseconds)
        { var reset = new CacheCounter(cumulativeExecutions, cumulativeCpuMilliseconds, 0, 0, true, observedAtUtc); counters[key] = reset; return reset; }
        var delta = new CacheCounter(cumulativeExecutions, cumulativeCpuMilliseconds, cumulativeExecutions - prior.CumulativeExecutions, cumulativeCpuMilliseconds - prior.CumulativeCpuMilliseconds, false, observedAtUtc); counters[key] = delta; return delta;
    }
    public readonly record struct CacheCounter(long CumulativeExecutions, long CumulativeCpuMilliseconds, long DeltaExecutions, long DeltaCpuMilliseconds, bool ResetDetected, DateTimeOffset ObservedAtUtc);
}

/// <summary>Single-flight gate for target-wide plan-cache sampling.</summary>
public sealed class QueryPerformanceSingleFlight<T>
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<T>> factory;
    private Task<T>? task;
    public QueryPerformanceSingleFlight(Func<CancellationToken, Task<T>> factory) => this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    public Task<T> GetAsync(CancellationToken cancellationToken)
    {
        lock (gate) task ??= factory(cancellationToken);
        return task;
    }
}

public sealed record QueryPerformancePlanCacheDatabaseAccounting
{
    public QueryPerformancePlanCacheDatabaseAccounting(int sourceRowsRead, int emittedRows, int rejectedRows, int responseBytes, CollectorLossKind lossKind, bool truncated, int minimumLostBytes = 0, bool lossCountIsExact = true, int minimumLostItems = 0)
    {
        if (sourceRowsRead < 0 || sourceRowsRead > QueryPerformanceBounds.ProbeRows || emittedRows < 0 || rejectedRows < 0 || emittedRows + rejectedRows > sourceRowsRead || responseBytes < 0 || responseBytes > QueryPerformanceBounds.ResponseBytes || minimumLostBytes < 0 || minimumLostBytes > QueryPerformanceBounds.ResponseBytes || minimumLostItems < 0 || !Enum.IsDefined(lossKind) || lossKind != CollectorLossKind.None && !truncated || lossKind == CollectorLossKind.None && (minimumLostBytes != 0 || minimumLostItems != 0 || !lossCountIsExact)) throw new ArgumentOutOfRangeException(nameof(sourceRowsRead));
        SourceRowsRead = sourceRowsRead; EmittedRows = emittedRows; RejectedRows = rejectedRows; ResponseBytes = responseBytes; LossKind = lossKind; Truncated = truncated; MinimumLostBytes = minimumLostBytes; LossCountIsExact = lossCountIsExact; MinimumLostItems = minimumLostItems;
    }
    public int SourceRowsRead { get; }
    public int EmittedRows { get; }
    public int RejectedRows { get; }
    public int ResponseBytes { get; }
    public CollectorLossKind LossKind { get; }
    public bool Truncated { get; }
    public int MinimumLostBytes { get; }
    public bool LossCountIsExact { get; }
    public int MinimumLostItems { get; }
}

public sealed record QueryPerformancePlanCacheSample
{
    /// <summary>
    /// The target-wide sample is a partitioned accounting contract. Every
    /// accepted source row belongs to exactly one database bucket, an invalid
    /// row bucket, or an explicit global-only probe bucket; bytes follow the
    /// same partition. This prevents a fallback sample from being charged once
    /// per database.
    /// </summary>
    public QueryPerformancePlanCacheSample(IReadOnlyList<QueryPerformanceObservation> observations, int sourceRowsRead, int responseBytes, CollectorLossKind lossKind, bool truncated, IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting>? byDatabase = null, int invalidSourceRowsRead = 0, int invalidResponseBytes = 0, int globalOnlySourceRowsRead = 0, int globalOnlyResponseBytes = 0)
    {
        Observations = observations ?? throw new ArgumentNullException(nameof(observations));
        IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting> accounting = byDatabase ?? new Dictionary<int, QueryPerformancePlanCacheDatabaseAccounting>();
        if (observations.Count > QueryPerformanceObservationBatch.MaximumItems || sourceRowsRead < 0 || sourceRowsRead > QueryPerformanceBounds.ProbeRows || responseBytes < 0 || responseBytes > QueryPerformanceBounds.ResponseBytes || invalidSourceRowsRead < 0 || invalidResponseBytes < 0 || globalOnlySourceRowsRead < 0 || globalOnlyResponseBytes < 0 || !Enum.IsDefined(lossKind)) throw new ArgumentOutOfRangeException(nameof(responseBytes));
        if (accounting.Keys.Any(databaseId => databaseId is <= 0 or > 32767)) throw new ArgumentException("Plan-cache database accounting contains an invalid database identity.", nameof(byDatabase));
        long partitionedRows = accounting.Values.Sum(item => (long)item.SourceRowsRead) + invalidSourceRowsRead + globalOnlySourceRowsRead;
        long partitionedBytes = accounting.Values.Sum(item => (long)item.ResponseBytes) + invalidResponseBytes + globalOnlyResponseBytes;
        if (partitionedRows != sourceRowsRead || partitionedBytes != responseBytes) throw new ArgumentException("Plan-cache sample rows and bytes must be exactly partitioned by database, invalid, or global-only probe ownership.", nameof(sourceRowsRead));
        SourceRowsRead = sourceRowsRead; ResponseBytes = responseBytes; LossKind = lossKind; Truncated = truncated; ByDatabase = accounting; InvalidSourceRowsRead = invalidSourceRowsRead; InvalidResponseBytes = invalidResponseBytes; GlobalOnlySourceRowsRead = globalOnlySourceRowsRead; GlobalOnlyResponseBytes = globalOnlyResponseBytes;
    }
    public IReadOnlyList<QueryPerformanceObservation> Observations { get; }
    public int SourceRowsRead { get; }
    public int ResponseBytes { get; }
    public CollectorLossKind LossKind { get; }
    public bool Truncated { get; }
    public IReadOnlyDictionary<int, QueryPerformancePlanCacheDatabaseAccounting> ByDatabase { get; }
    public int InvalidSourceRowsRead { get; }
    public int InvalidResponseBytes { get; }
    public int GlobalOnlySourceRowsRead { get; }
    public int GlobalOnlyResponseBytes { get; }

    public QueryPerformancePlanCacheSample RejectForResponseBudget()
    {
        var rejected = ByDatabase.ToDictionary(
            pair => pair.Key,
            pair => new QueryPerformancePlanCacheDatabaseAccounting(
                pair.Value.SourceRowsRead,
                0,
                pair.Value.SourceRowsRead,
                0,
                CollectorLossKind.ResponseByteLimit,
                true,
                Math.Max(1, pair.Value.ResponseBytes),
                false,
                Math.Max(1, pair.Value.SourceRowsRead)));
        return new QueryPerformancePlanCacheSample(
            [],
            SourceRowsRead,
            0,
            CollectorLossKind.ResponseByteLimit,
            true,
            rejected,
            InvalidSourceRowsRead,
            0,
            GlobalOnlySourceRowsRead,
            0);
    }
}

/// <summary>Deterministic run-level source metadata derived from both emitted rows and per-database attempts.</summary>
public readonly record struct QueryPerformanceAggregateMetadata(QueryPerformanceSource Source, string SourceState)
{
    public static QueryPerformanceAggregateMetadata Create(IReadOnlyList<QueryPerformanceObservation> observations, IReadOnlyList<QueryPerformanceDatabaseStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(statuses);
        var sources = observations.Select(static item => item.Source).Concat(statuses.Select(StatusSource)).Distinct().ToArray();
        var states = observations.Select(static item => item.SourceState).Concat(statuses.Select(static item => item.SourceState ?? StatusState(item.Status))).Distinct().ToArray();
        return new QueryPerformanceAggregateMetadata(sources.Length == 1 ? sources[0] : sources.Length == 0 ? QueryPerformanceSource.QueryStore : QueryPerformanceSource.Mixed, states.Length == 1 ? StateName(states[0]) : states.Length == 0 ? "unsupported" : "mixed");
    }

    private static QueryPerformanceSource StatusSource(QueryPerformanceDatabaseStatus status) => status.Status == QueryPerformanceReadStatus.OutputCapped
        ? QueryPerformanceSource.Mixed
        : status.Status is QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty or QueryPerformanceReadStatus.PlanCachePermissionDenied or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheTimedOut
            ? QueryPerformanceSource.PlanCache
            : QueryPerformanceSource.QueryStore;
    private static QueryStoreState StatusState(QueryPerformanceReadStatus status) => status switch
    {
        QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty => QueryStoreState.ReadWrite,
        QueryPerformanceReadStatus.QueryStoreDisabled => QueryStoreState.Disabled,
        QueryPerformanceReadStatus.QueryStorePermissionDenied or QueryPerformanceReadStatus.PlanCachePermissionDenied => QueryStoreState.PermissionDenied,
        QueryPerformanceReadStatus.QueryStoreTimedOut or QueryPerformanceReadStatus.PlanCacheTimedOut => QueryStoreState.TimedOut,
        QueryPerformanceReadStatus.QueryStoreReadFailure or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty => QueryStoreState.ReadFailure,
        _ => QueryStoreState.Unsupported,
    };
    private static string StateName(QueryStoreState state) => state switch
    {
        QueryStoreState.ReadWrite => "read_write", QueryStoreState.ReadOnly => "read_only", QueryStoreState.Disabled => "disabled", QueryStoreState.PermissionDenied => "permission_denied", QueryStoreState.ReadFailure => "read_failure", QueryStoreState.TimedOut => "timed_out", _ => "unsupported",
    };
}
