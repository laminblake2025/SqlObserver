using System.Collections.ObjectModel;
using System.Security.Cryptography;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Bounded query performance evidence. Plaintext, XML and provider handles are never fields.</summary>
public enum QueryPerformanceSource { QueryStore = 1, PlanCache = 2, Mixed = 3, Unavailable = 4 }
public enum QueryStoreState { ReadWrite = 1, ReadOnly = 2, Disabled = 3, Unsupported = 4, PermissionDenied = 5, ReadFailure = 6, TimedOut = 7 }
public enum QueryMetricSemantics { QueryStoreInterval = 1, PlanCacheCumulative = 2, PlanCacheDelta = 3, PlanCacheBaseline = 4, Reset = 5 }
public enum QueryCoverage { Complete = 1, Truncated = 2, Unavailable = 3, NoActivity = 4 }

public sealed record QueryOpaqueIdentity
{
    public QueryOpaqueIdentity(int databaseId, string queryFingerprint)
    {
        if (databaseId is <= 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(databaseId));
        QueryFingerprint = RequireDigest(queryFingerprint, nameof(queryFingerprint)); DatabaseId = databaseId;
    }
    public int DatabaseId { get; }
    public string QueryFingerprint { get; }
    public static string RequireDigest(string value, string name) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new ArgumentException("Opaque identity must be a SHA-256 hex token.", name);
}

public sealed record PlanOpaqueIdentity
{
    public PlanOpaqueIdentity(QueryOpaqueIdentity query, string planFingerprint) { Query = query ?? throw new ArgumentNullException(nameof(query)); PlanFingerprint = QueryOpaqueIdentity.RequireDigest(planFingerprint, nameof(planFingerprint)); }
    public QueryOpaqueIdentity Query { get; }
    public string PlanFingerprint { get; }
}

public sealed record QueryPerformanceMetricSet
{
    public QueryPerformanceMetricSet(long? cpuMilliseconds, long? durationMilliseconds, long? executions, long? logicalReads, long? writes, long? rows)
    {
        CpuMilliseconds = NonNegative(cpuMilliseconds, nameof(cpuMilliseconds)); DurationMilliseconds = NonNegative(durationMilliseconds, nameof(durationMilliseconds)); Executions = NonNegative(executions, nameof(executions)); LogicalReads = NonNegative(logicalReads, nameof(logicalReads)); Writes = NonNegative(writes, nameof(writes)); Rows = NonNegative(rows, nameof(rows));
    }
    public long? CpuMilliseconds { get; }
    public long? DurationMilliseconds { get; }
    public long? Executions { get; }
    public long? LogicalReads { get; }
    public long? Writes { get; }
    public long? Rows { get; }
    private static long? NonNegative(long? value, string name) => value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(name);
}

/// <summary>Query Store wait-category totals for one sampled plan and run; these are not deltas.</summary>
public sealed record QueryWaitCategory
{
    public QueryWaitCategory(int category, long waitMilliseconds)
    {
        if (category is < 0 or > 31 || waitMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(category));
        Category = category;
        WaitMilliseconds = waitMilliseconds;
    }
    public int Category { get; }
    public long WaitMilliseconds { get; }
}

public sealed class QueryStoreWaitSnapshot
{
    public const int MaximumCategories = 32;
    public QueryStoreWaitSnapshot(IReadOnlyList<QueryWaitCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count > MaximumCategories || categories.Any(static item => item is null) ||
            categories.Select(static item => item.Category).Distinct().Count() != categories.Count)
            throw new ArgumentException("Query Store wait categories must be bounded and unique.", nameof(categories));
        Categories = Array.AsReadOnly(categories.OrderBy(static item => item.Category).ToArray());
    }
    public IReadOnlyList<QueryWaitCategory> Categories { get; }
}

public sealed class QueryPerformanceObservation : IIngestionRecord
{
    public const int FixedEstimatedBytes = 256;
    public QueryPerformanceObservation(MonitoredInstanceId targetId, ObservationTargetRevision revision, QueryOpaqueIdentity query, PlanOpaqueIdentity? plan, QueryPerformanceSource source, QueryStoreState sourceState, QueryMetricSemantics semantics, QueryPerformanceMetricSet metrics, DateTimeOffset intervalStartUtc, DateTimeOffset intervalEndUtc, DateTimeOffset observedAtUtc, QueryCoverage coverage, bool fresh, bool truncated, SensitivePayloadReference? contentReference = null, long? queryTextSourceId = null, ProtectedSensitivePayload? protectedContent = null, SensitivePayloadReference? planContentReference = null, ProtectedSensitivePayload? protectedPlanContent = null, long? planSourceId = null, QueryStoreWaitSnapshot? waitSnapshot = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); TargetRevision = revision ?? throw new ArgumentNullException(nameof(revision)); Query = query ?? throw new ArgumentNullException(nameof(query)); if (plan is not null && (plan.Query.DatabaseId != query.DatabaseId || !string.Equals(plan.Query.QueryFingerprint, query.QueryFingerprint, StringComparison.Ordinal))) throw new ArgumentException("Plan identity must belong to the observation query.", nameof(plan)); Plan = plan; Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        if (!Enum.IsDefined(source) || !Enum.IsDefined(sourceState) || !Enum.IsDefined(semantics) || !Enum.IsDefined(coverage)) throw new ArgumentOutOfRangeException(nameof(source));
        if (intervalStartUtc.Offset != TimeSpan.Zero || intervalEndUtc.Offset != TimeSpan.Zero || observedAtUtc.Offset != TimeSpan.Zero || intervalEndUtc <= intervalStartUtc || intervalEndUtc - intervalStartUtc > TimeSpan.FromDays(7)) throw new ArgumentException("Query intervals must be UTC, increasing, and at most seven days.");
        if (source == QueryPerformanceSource.QueryStore && semantics != QueryMetricSemantics.QueryStoreInterval || source == QueryPerformanceSource.PlanCache && semantics == QueryMetricSemantics.QueryStoreInterval) throw new ArgumentException("Metric semantics do not match source.");
        if (queryTextSourceId is <= 0 || queryTextSourceId is not null && source != QueryPerformanceSource.QueryStore) throw new ArgumentOutOfRangeException(nameof(queryTextSourceId));
        if (planSourceId is <= 0 || planSourceId is not null && (source != QueryPerformanceSource.QueryStore || plan is null)) throw new ArgumentOutOfRangeException(nameof(planSourceId));
        if (contentReference is not null && contentReference.Kind != SensitivePayloadKind.QueryText) throw new ArgumentException("Query text reference has the wrong payload kind.", nameof(contentReference));
        if (protectedContent is not null && (protectedContent.Kind != SensitivePayloadKind.QueryText || protectedContent.CiphertextLengthBytes > 16 * 1024 || contentReference is not null)) throw new ArgumentException("Protected query text cannot coexist with a stored reference or exceed its bound.", nameof(protectedContent));
        if (planContentReference is not null && (plan is null || planContentReference.Kind != SensitivePayloadKind.ExecutionPlan)) throw new ArgumentException("Plan reference must belong to a plan and have the execution-plan kind.", nameof(planContentReference));
        if (protectedPlanContent is not null && (plan is null || source != QueryPerformanceSource.QueryStore || protectedPlanContent.Kind != SensitivePayloadKind.ExecutionPlan || protectedPlanContent.CiphertextLengthBytes > ProtectedSensitivePayload.MaximumCiphertextBytes || planContentReference is not null)) throw new ArgumentException("Protected plan content must belong to a Query Store plan and cannot coexist with its stored reference.", nameof(protectedPlanContent));
        if (waitSnapshot is not null && (source != QueryPerformanceSource.QueryStore || plan is null))
            throw new ArgumentException("Query Store waits require a plan identity.", nameof(waitSnapshot));
        Source = source; SourceState = sourceState; Semantics = semantics; IntervalStartUtc = intervalStartUtc; IntervalEndUtc = intervalEndUtc; ObservedAtUtc = observedAtUtc; Coverage = coverage; Fresh = fresh; Truncated = truncated; ContentReference = contentReference; QueryTextSourceId = queryTextSourceId; ProtectedContent = protectedContent; PlanContentReference = planContentReference; ProtectedPlanContent = protectedPlanContent; PlanSourceId = planSourceId; WaitSnapshot = waitSnapshot; EstimatedSizeBytes = checked(FixedEstimatedBytes + (waitSnapshot?.Categories.Count ?? 0) * 24);
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public QueryOpaqueIdentity Query { get; }
    public PlanOpaqueIdentity? Plan { get; }
    public QueryPerformanceSource Source { get; }
    public QueryStoreState SourceState { get; }
    public QueryMetricSemantics Semantics { get; }
    public QueryPerformanceMetricSet Metrics { get; }
    public DateTimeOffset IntervalStartUtc { get; }
    public DateTimeOffset IntervalEndUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public QueryCoverage Coverage { get; }
    public bool Fresh { get; }
    public bool Truncated { get; }
    public SensitivePayloadReference? ContentReference { get; }
    /// <summary>Ephemeral Query Store lookup key; never included in repository metadata.</summary>
    public long? QueryTextSourceId { get; }
    /// <summary>Ephemeral Query Store plan lookup key; never included in repository metadata.</summary>
    public long? PlanSourceId { get; }
    /// <summary>Ephemeral ciphertext handed to the fenced scheduler writer; never serialized as metadata.</summary>
    public ProtectedSensitivePayload? ProtectedContent { get; }
    public SensitivePayloadReference? PlanContentReference { get; }
    /// <summary>Ephemeral encrypted plan XML handed to the fenced scheduler writer.</summary>
    public ProtectedSensitivePayload? ProtectedPlanContent { get; }
    /// <summary>Captured Query Store wait totals for this plan; null means not captured.</summary>
    public QueryStoreWaitSnapshot? WaitSnapshot { get; }
    public int EstimatedSizeBytes { get; }

    public QueryPerformanceObservation WithProtectedContent(ProtectedSensitivePayload? content) =>
        new(TargetId, TargetRevision, Query, Plan, Source, SourceState, Semantics, Metrics,
            IntervalStartUtc, IntervalEndUtc, ObservedAtUtc, Coverage, Fresh, Truncated,
            contentReference: null, queryTextSourceId: null, protectedContent: content,
            planContentReference: PlanContentReference, protectedPlanContent: ProtectedPlanContent,
            planSourceId: PlanSourceId, waitSnapshot: WaitSnapshot);

    public QueryPerformanceObservation WithContentReference(SensitivePayloadReference reference) =>
        new(TargetId, TargetRevision, Query, Plan, Source, SourceState, Semantics, Metrics,
            IntervalStartUtc, IntervalEndUtc, ObservedAtUtc, Coverage, Fresh, Truncated,
            contentReference: reference, planContentReference: PlanContentReference,
            protectedPlanContent: ProtectedPlanContent, planSourceId: PlanSourceId,
            waitSnapshot: WaitSnapshot);

    public QueryPerformanceObservation WithProtectedPlanContent(ProtectedSensitivePayload? content) =>
        new(TargetId, TargetRevision, Query, Plan, Source, SourceState, Semantics, Metrics,
            IntervalStartUtc, IntervalEndUtc, ObservedAtUtc, Coverage, Fresh, Truncated,
            contentReference: ContentReference, queryTextSourceId: QueryTextSourceId,
            protectedContent: ProtectedContent, protectedPlanContent: content,
            waitSnapshot: WaitSnapshot);

    public QueryPerformanceObservation WithPlanContentReference(SensitivePayloadReference reference) =>
        new(TargetId, TargetRevision, Query, Plan, Source, SourceState, Semantics, Metrics,
            IntervalStartUtc, IntervalEndUtc, ObservedAtUtc, Coverage, Fresh, Truncated,
            contentReference: ContentReference, queryTextSourceId: QueryTextSourceId,
            protectedContent: ProtectedContent, planContentReference: reference,
            waitSnapshot: WaitSnapshot);

    public QueryPerformanceObservation WithWaitSnapshot(QueryStoreWaitSnapshot snapshot) =>
        new(TargetId, TargetRevision, Query, Plan, Source, SourceState, Semantics, Metrics,
            IntervalStartUtc, IntervalEndUtc, ObservedAtUtc, Coverage, Fresh, Truncated,
            contentReference: ContentReference, queryTextSourceId: QueryTextSourceId,
            protectedContent: ProtectedContent, planContentReference: PlanContentReference,
            protectedPlanContent: ProtectedPlanContent, planSourceId: PlanSourceId,
            waitSnapshot: snapshot);
}

public sealed class QueryPerformanceObservationBatch : ObservationBatch<QueryPerformanceObservation>
{
    public const int MaximumItems = 20_000;
    public QueryPerformanceObservationBatch(IReadOnlyList<QueryPerformanceObservation> items) : base(items, MaximumItems, x => $"{x.Query.DatabaseId}:{x.Query.QueryFingerprint}:{x.Source}:{x.IntervalStartUtc:O}:{x.IntervalEndUtc:O}:{x.Plan?.PlanFingerprint ?? "none"}", "query-performance") { }
}

/// <summary>Bounded per-database source attempt evidence, including empty and unavailable databases.</summary>
public sealed record QueryPerformanceDatabaseStatus
{
    public QueryPerformanceDatabaseStatus(int databaseId, QueryPerformanceReadStatus status, string reason, bool fallbackAttempted, bool truncated, int sourceRowsRead, int responseBytes, QueryStoreState? sourceState = null, CollectorLossKind lossKind = CollectorLossKind.None, int minimumLostItems = 0, bool lossCountIsExact = true, int minimumLostBytes = 0)
    {
        if (databaseId is <= 0 or > 32767 || !Enum.IsDefined(status) || string.IsNullOrWhiteSpace(reason) || reason.Length > 128 || reason.Any(char.IsControl) || sourceRowsRead < 0 || sourceRowsRead > QueryPerformanceBounds.ProbeRows || responseBytes < 0 || responseBytes > QueryPerformanceBounds.ResponseBytes) throw new ArgumentException("Database source status is outside bounded M7 contract.");
        bool planCache = status is QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty or QueryPerformanceReadStatus.PlanCachePermissionDenied or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheTimedOut;
        if (status != QueryPerformanceReadStatus.OutputCapped && planCache != fallbackAttempted || (!planCache && status is (QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty) && fallbackAttempted)) throw new ArgumentException("Database source status fallback matrix is invalid.");
        if (!AllowedReason(status, reason)) throw new ArgumentException("Database source status reason is not in the reviewed allowlist.");
        if (!Enum.IsDefined(lossKind)) throw new ArgumentOutOfRangeException(nameof(lossKind));
        if (minimumLostItems < 0 || minimumLostBytes < 0 || minimumLostBytes > QueryPerformanceBounds.ResponseBytes) throw new ArgumentOutOfRangeException(nameof(minimumLostItems));
        if (lossKind == CollectorLossKind.None && (minimumLostItems != 0 || minimumLostBytes != 0 || !lossCountIsExact)) throw new ArgumentException("No-loss status cannot carry loss detail.");
        if (lossKind != CollectorLossKind.None && !truncated) throw new ArgumentException("A database loss kind requires truncation evidence.");
        bool rowStatus = status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.PlanCacheRows;
        if (status != QueryPerformanceReadStatus.OutputCapped && !rowStatus && (lossKind is CollectorLossKind.SourceRowLimit or CollectorLossKind.ResponseByteLimit || truncated && lossKind == CollectorLossKind.None)) throw new ArgumentException("Non-row database statuses cannot claim global row/byte loss.");
        if (status == QueryPerformanceReadStatus.PlanCacheRows && !fallbackAttempted) throw new ArgumentException("Plan-cache rows require an attempted fallback.");
        if ((status is QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty) && sourceState is not (QueryStoreState.ReadWrite or QueryStoreState.ReadOnly) || status == QueryPerformanceReadStatus.QueryStoreDisabled && sourceState != QueryStoreState.Disabled || status == QueryPerformanceReadStatus.QueryStorePermissionDenied && sourceState != QueryStoreState.PermissionDenied || status == QueryPerformanceReadStatus.QueryStoreTimedOut && sourceState != QueryStoreState.TimedOut) throw new ArgumentException("Database source status state is inconsistent.");
        DatabaseId=databaseId; Status=status; Reason=reason; FallbackAttempted=fallbackAttempted; Truncated=truncated; SourceRowsRead=sourceRowsRead; ResponseBytes=responseBytes; SourceState=sourceState; LossKind=lossKind; MinimumLostItems=minimumLostItems; LossCountIsExact=lossCountIsExact; MinimumLostBytes=minimumLostBytes;
    }
    public int DatabaseId { get; }
    public QueryPerformanceReadStatus Status { get; }
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
    private static bool AllowedReason(QueryPerformanceReadStatus status, string reason) => status switch
    {
        QueryPerformanceReadStatus.QueryStoreEmpty => reason == "query_store_empty",
        QueryPerformanceReadStatus.QueryStoreRows => reason is "query_store_read" or "query_store_empty",
        QueryPerformanceReadStatus.QueryStoreDisabled => reason == "query_store_disabled",
        QueryPerformanceReadStatus.QueryStoreUnsupported => reason is "query_store_unsupported" or "query_store_probe_missing",
        QueryPerformanceReadStatus.QueryStorePermissionDenied => reason == "query_store_permission_denied",
        QueryPerformanceReadStatus.QueryStoreReadFailure => reason == "query_store_read_failure",
        QueryPerformanceReadStatus.QueryStoreTimedOut => reason == "query_store_timeout",
        QueryPerformanceReadStatus.PlanCacheRows => reason == "plan_cache_fallback",
        QueryPerformanceReadStatus.PlanCacheEmpty => reason == "plan_cache_empty",
        QueryPerformanceReadStatus.PlanCachePermissionDenied => reason == "plan_cache_permission_denied",
        QueryPerformanceReadStatus.PlanCacheReadFailure => reason == "plan_cache_read_failure",
        QueryPerformanceReadStatus.PlanCacheTimedOut => reason == "plan_cache_timeout",
        QueryPerformanceReadStatus.OutputCapped => reason == "output_capped",
        _ => false
    };
}

public static class QueryPerformanceIdentity
{
    public static string Fingerprint(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
