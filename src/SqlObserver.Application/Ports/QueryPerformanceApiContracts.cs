using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum QueryPerformanceMetric { CpuMilliseconds = 1, DurationMilliseconds = 2, Executions = 3, LogicalReads = 4, Writes = 5, Rows = 6 }
public static class QueryPerformanceAggregateState
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "read_write", "read_only", "disabled", "unsupported", "permission_denied", "read_failure", "timed_out", "mixed", "unavailable" };
    public static bool IsValid(string? value) => value is not null && Allowed.Contains(value);
    public static string Require(string value) => IsValid(value) ? value : throw new InvalidDataException("Query performance aggregate state was not allowlisted.");
    public static string RequireDatabase(string value) => IsValid(value) && !string.Equals(value, "mixed", StringComparison.Ordinal) && !string.Equals(value, "unavailable", StringComparison.Ordinal) ? value : throw new InvalidDataException("Per-database query performance state was not allowlisted.");
}
public static class QueryPerformanceDatabaseStatusCode
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "query_store_empty", "query_store_rows", "query_store_disabled", "query_store_unsupported", "query_store_permission_denied", "query_store_read_failure", "query_store_timed_out", "plan_cache_rows", "plan_cache_empty", "plan_cache_permission_denied", "plan_cache_read_failure", "plan_cache_timed_out", "output_capped" };
    public static string Require(string value) => Allowed.Contains(value) ? value : throw new InvalidDataException("Query performance database status was not allowlisted.");
}
public static class QueryPerformanceLossKind
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "duplicate_overlap" };
    public static string Require(string value) => Allowed.Contains(value) ? value : throw new InvalidDataException("Query performance loss kind was not allowlisted.");
}
public sealed record QueryPerformanceDatabaseCatalogDto
{
    public QueryPerformanceDatabaseCatalogDto(int databaseId, string databaseName)
    {
        if (databaseId is <= 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(databaseId));
        DatabaseId = databaseId;
        DatabaseName = new SqlServerObjectName(databaseName).Value;
    }
    public int DatabaseId { get; }
    public string DatabaseName { get; }
}
public sealed record QueryPerformanceDatabaseStatusDto
{
    public QueryPerformanceDatabaseStatusDto(int databaseId, string status, string sourceState, string reason, bool fallbackAttempted, bool truncated, string lossKind, int sourceRowsRead, int responseBytes, int minimumLostItems = 0, bool lossCountIsExact = true, int minimumLostBytes = 0)
    { if (databaseId is <= 0 or > 32767 || reason is null || reason.Length is 0 or > 128 || sourceRowsRead < 0 || sourceRowsRead > QueryPerformanceBounds.ProbeRows || responseBytes < 0 || responseBytes > QueryPerformanceBounds.ResponseBytes || minimumLostItems < 0 || minimumLostItems > QueryPerformanceBounds.MaximumObservationsPerDatabase || minimumLostBytes < 0 || minimumLostBytes > QueryPerformanceBounds.ResponseBytes || lossKind == "none" && (minimumLostItems != 0 || minimumLostBytes != 0 || !lossCountIsExact)) throw new ArgumentOutOfRangeException(nameof(databaseId)); DatabaseId = databaseId; Status = QueryPerformanceDatabaseStatusCode.Require(status); SourceState = QueryPerformanceAggregateState.RequireDatabase(sourceState); Reason = reason; FallbackAttempted = fallbackAttempted; Truncated = truncated; LossKind = QueryPerformanceLossKind.Require(lossKind); SourceRowsRead = sourceRowsRead; ResponseBytes = responseBytes; MinimumLostItems = minimumLostItems; LossCountIsExact = lossCountIsExact; MinimumLostBytes = minimumLostBytes; }
    public int DatabaseId { get; }
    public string Status { get; }
    public string SourceState { get; }
    public string Reason { get; }
    public bool FallbackAttempted { get; }
    public bool Truncated { get; }
    public string LossKind { get; }
    public int SourceRowsRead { get; }
    public int ResponseBytes { get; }
    public int MinimumLostItems { get; }
    public bool LossCountIsExact { get; }
    public int MinimumLostBytes { get; }
}
public sealed record QueryPerformanceStatusDto
{
    public QueryPerformanceStatusDto(MonitoredInstanceId targetId, DateTimeOffset snapshotUtc, QueryPerformanceSource? source, string sourceState, QueryCoverage coverage, bool fresh, bool truncated, bool contentAvailable, string? reason, IReadOnlyList<QueryPerformanceDatabaseStatusDto>? databaseStatuses = null, string? targetStatus = null, string? targetReason = null, IReadOnlyList<QueryPerformanceDatabaseCatalogDto>? databaseCatalog = null)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        SnapshotUtc = snapshotUtc; Source = source; SourceState = QueryPerformanceAggregateState.Require(sourceState); Coverage = coverage; Fresh = fresh; Truncated = truncated; ContentAvailable = contentAvailable; Reason = reason; DatabaseStatuses = databaseStatuses; DatabaseCatalog = RequireDatabaseCatalog(databaseCatalog); if (targetStatus is not null) { _ = new QueryPerformanceTargetStatus(targetStatus, targetReason ?? throw new InvalidDataException("Target failure reason is required.")); if (source is not QueryPerformanceSource.Unavailable || sourceState != "unavailable" || coverage != QueryCoverage.Unavailable || fresh) throw new InvalidDataException("Target failure evidence is inconsistent."); } TargetStatus = targetStatus; TargetReason = targetReason;
    }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public QueryPerformanceSource? Source { get; }
    public string SourceState { get; }
    public QueryCoverage Coverage { get; }
    public bool Fresh { get; }
    public bool Truncated { get; }
    public bool ContentAvailable { get; }
    public string? Reason { get; }
    public IReadOnlyList<QueryPerformanceDatabaseStatusDto>? DatabaseStatuses { get; }
    public IReadOnlyList<QueryPerformanceDatabaseCatalogDto> DatabaseCatalog { get; }
    public string? TargetStatus { get; }
    public string? TargetReason { get; }

    private static QueryPerformanceDatabaseCatalogDto[] RequireDatabaseCatalog(IReadOnlyList<QueryPerformanceDatabaseCatalogDto>? databaseCatalog)
    {
        if (databaseCatalog is null) return [];
        if (databaseCatalog.Count > QueryPerformanceBounds.MaximumDatabases) throw new ArgumentOutOfRangeException(nameof(databaseCatalog));
        var ids = new HashSet<int>();
        foreach (QueryPerformanceDatabaseCatalogDto item in databaseCatalog)
            if (item is null || !ids.Add(item.DatabaseId)) throw new InvalidDataException("Query performance database catalog contains a duplicate identity.");
        return databaseCatalog.ToArray();
    }
}
public sealed record TopQueryDto(MonitoredInstanceId TargetId, QueryOpaqueIdentity Query, PlanOpaqueIdentity? Plan, QueryPerformanceSource Source, QueryStoreState SourceState, QueryPerformanceMetric Metric, long? Value, QueryMetricSemantics Semantics, DateTimeOffset IntervalStartUtc, DateTimeOffset IntervalEndUtc, QueryCoverage Coverage, bool Fresh, bool Truncated, bool ContentAvailable, Guid? CollectionRunId = null, string? ObservationKey = null);
public sealed record QueryHistoryDto(MonitoredInstanceId TargetId, QueryOpaqueIdentity Query, QueryPerformanceSource Source, QueryStoreState SourceState, QueryPerformanceMetricSet Metrics, QueryMetricSemantics Semantics, DateTimeOffset IntervalStartUtc, DateTimeOffset IntervalEndUtc, bool ResetDetected, bool Fresh, bool Truncated, bool ContentAvailable, Guid? CollectionRunId = null, QueryCoverage Coverage = QueryCoverage.Complete, string? PlanFingerprint = null, string? ObservationKey = null);
public sealed record QueryPlanMetadataDto(MonitoredInstanceId TargetId, PlanOpaqueIdentity Plan, QueryPerformanceSource Source, DateTimeOffset ObservedAtUtc, QueryCoverage Coverage, bool ContentAvailable);
public sealed class QueryPerformanceCursorEnvelope
{
    public QueryPerformanceCursorEnvelope(MonitoredInstanceId targetId, int databaseId, DateTimeOffset fromUtc, DateTimeOffset toUtc, QueryPerformanceMetric metric, DateTimeOffset snapshotUtc, DateTimeOffset intervalEndUtc, string queryFingerprint, long? metricValue = null, Guid? collectionRunId = null, string? planFingerprint = null, string? observationKey = null) { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); if (databaseId is <= 0 or > 32767) throw new ArgumentOutOfRangeException(nameof(databaseId)); DatabaseId = databaseId; FromUtc = RequireUtc(fromUtc); ToUtc = RequireUtc(toUtc); if (ToUtc <= FromUtc || ToUtc-FromUtc > TimeSpan.FromDays(7)) throw new ArgumentException("Cursor window is invalid."); if (!Enum.IsDefined(metric)) throw new ArgumentOutOfRangeException(nameof(metric)); if (metricValue is < 0) throw new ArgumentOutOfRangeException(nameof(metricValue)); Metric = metric; MetricValue = metricValue; SnapshotUtc = RequireUtc(snapshotUtc); IntervalEndUtc = RequireUtc(intervalEndUtc); QueryFingerprint = QueryOpaqueIdentity.RequireDigest(queryFingerprint, nameof(queryFingerprint)); if (collectionRunId == Guid.Empty) throw new ArgumentException("Cursor run identity is invalid.", nameof(collectionRunId)); CollectionRunId = collectionRunId; PlanFingerprint = planFingerprint is null ? null : QueryOpaqueIdentity.RequireDigest(planFingerprint, nameof(planFingerprint)); ObservationKey = observationKey is null ? null : RequireObservationKey(observationKey); }
    public MonitoredInstanceId TargetId { get; }
    public int DatabaseId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public QueryPerformanceMetric Metric { get; }
    public long? MetricValue { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public DateTimeOffset IntervalEndUtc { get; }
    public string QueryFingerprint { get; }
    public Guid? CollectionRunId { get; }
    public string? PlanFingerprint { get; }
    public string? ObservationKey { get; }
    private static string RequireObservationKey(string value) => value.Length == 32 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : throw new ArgumentException("Observation key must be a 16-byte opaque token.", nameof(value));
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Cursor timestamp must be UTC.");
}
