using System.Text.Json;
using System.Text;
using System.Text.Encodings.Web;

namespace SqlObserver.Domain.Collection;

/// <summary>Canonical bounded JSON shape used both by the collector cap and PostgreSQL jsonb persistence.</summary>
public static class QueryPerformancePersistencePayload
{
    public const int MaximumSerializedBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonbStringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] Serialize(
        IReadOnlyList<QueryPerformanceObservation> observations,
        IReadOnlyList<QueryPerformanceDatabaseStatus> statuses,
        QueryPerformanceTargetStatus? targetStatus,
        bool contentLinksCommittedWithRun = false)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(statuses);
        // The JSON payload is metadata-only. The repository may opt in only
        // when it commits the protected-content links in the same transaction.
        if (!contentLinksCommittedWithRun && observations.Any(static item =>
                item.ContentReference is not null || item.PlanContentReference is not null))
            throw new InvalidDataException("Query content references require a protected-content commit contract.");
        var observationJson = observations.Select(static x => new
        {
            databaseId = x.Query.DatabaseId,
            queryFingerprint = x.Query.QueryFingerprint,
            planFingerprint = x.Plan?.PlanFingerprint,
            source = Source(x.Source),
            sourceState = State(x.SourceState),
            semantics = Semantics(x.Semantics),
            intervalStartUtc = x.IntervalStartUtc,
            intervalEndUtc = x.IntervalEndUtc,
            observedAtUtc = x.ObservedAtUtc,
            cpuMs = x.Metrics.CpuMilliseconds,
            durationMs = x.Metrics.DurationMilliseconds,
            executions = x.Metrics.Executions,
            logicalReads = x.Metrics.LogicalReads,
            writes = x.Metrics.Writes,
            rows = x.Metrics.Rows,
            coverage = Coverage(x.Coverage),
            fresh = x.Fresh,
            truncated = x.Truncated,
        }).ToArray();
        var statusJson = statuses.Select(static x => new
        {
            databaseId = x.DatabaseId,
            status = ReadStatus(x.Status),
            sourceState = State(x.SourceState ?? StatusState(x.Status)),
            reason = x.Reason,
            fallbackAttempted = x.FallbackAttempted,
            truncated = x.Truncated,
            lossKind = Loss(x.LossKind),
            sourceRowsRead = x.SourceRowsRead,
            responseBytes = x.ResponseBytes,
            minimumLostItems = x.MinimumLostItems,
            lossCountIsExact = x.LossCountIsExact,
            minimumLostBytes = x.MinimumLostBytes,
        }).ToArray();
        object payload = targetStatus is null
            ? new { observations = observationJson, databaseStatuses = statusJson }
            : new { observations = observationJson, databaseStatuses = statusJson, targetStatus = new { status = targetStatus.Status, reason = targetStatus.Reason } };
        return SerializeCanonicalJsonb(payload);
    }

    /// <summary>Matches PostgreSQL jsonb textual output: sorted object keys and comma/colon spaces.</summary>
    public static byte[] SerializeCanonicalJsonb(object payload)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonbStringOptions));
        var text = new StringBuilder();
        WriteCanonical(document.RootElement, text);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static void WriteCanonical(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                bool firstProperty = true;
                // PostgreSQL jsonb's canonical object comparator orders keys by
                // UTF-8 byte length, then by binary key order (equal lengths).
                // The payload keys are ASCII, so ordinal comparison is the
                // exact binary tie-breaker used by the server representation.
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(static item => Encoding.UTF8.GetByteCount(item.Name)).ThenBy(static item => item.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) text.Append(", ");
                    firstProperty = false;
                    text.Append(JsonSerializer.Serialize(property.Name, JsonbStringOptions));
                    text.Append(": ");
                    WriteCanonical(property.Value, text);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in value.EnumerateArray())
                {
                    if (!firstItem) text.Append(", ");
                    firstItem = false;
                    WriteCanonical(item, text);
                }
                text.Append(']');
                break;
            case JsonValueKind.String:
                text.Append(JsonSerializer.Serialize(value.GetString(), JsonbStringOptions));
                break;
            default:
                text.Append(value.GetRawText());
                break;
        }
    }

    private static string Source(QueryPerformanceSource value) => value switch { QueryPerformanceSource.QueryStore => "query_store", QueryPerformanceSource.PlanCache => "plan_cache", _ => throw new InvalidDataException("Only concrete observation sources can be persisted.") };
    private static string State(QueryStoreState value) => value switch { QueryStoreState.ReadWrite => "read_write", QueryStoreState.ReadOnly => "read_only", QueryStoreState.Disabled => "disabled", QueryStoreState.Unsupported => "unsupported", QueryStoreState.PermissionDenied => "permission_denied", QueryStoreState.ReadFailure => "read_failure", QueryStoreState.TimedOut => "timed_out", _ => throw new InvalidDataException("Unknown query performance state.") };
    private static string Semantics(QueryMetricSemantics value) => value switch { QueryMetricSemantics.QueryStoreInterval => "query_store_interval", QueryMetricSemantics.PlanCacheCumulative => "plan_cache_cumulative", QueryMetricSemantics.PlanCacheDelta => "plan_cache_delta", QueryMetricSemantics.PlanCacheBaseline => "plan_cache_baseline", QueryMetricSemantics.Reset => "reset", _ => throw new InvalidDataException("Unknown query performance semantics.") };
    private static string Coverage(QueryCoverage value) => value switch { QueryCoverage.Complete => "complete", QueryCoverage.Truncated => "truncated", QueryCoverage.Unavailable => "unavailable", QueryCoverage.NoActivity => "no_activity", _ => throw new InvalidDataException("Unknown query performance coverage.") };
    private static string Loss(CollectorLossKind value) => value switch { CollectorLossKind.None => "none", CollectorLossKind.SourceRowLimit => "source_row_limit", CollectorLossKind.ResponseByteLimit => "response_byte_limit", CollectorLossKind.OutputValidationFailure => "output_validation_failure", CollectorLossKind.IngestionRejection => "ingestion_rejection", CollectorLossKind.BlockingGraphLimit => "blocking_graph_limit", CollectorLossKind.DuplicateOverlap => "duplicate_overlap", _ => throw new InvalidDataException("Unknown query performance loss.") };
    private static string ReadStatus(QueryPerformanceReadStatus value) => value switch { QueryPerformanceReadStatus.QueryStoreEmpty => "query_store_empty", QueryPerformanceReadStatus.QueryStoreRows => "query_store_rows", QueryPerformanceReadStatus.QueryStoreDisabled => "query_store_disabled", QueryPerformanceReadStatus.QueryStoreUnsupported => "query_store_unsupported", QueryPerformanceReadStatus.QueryStorePermissionDenied => "query_store_permission_denied", QueryPerformanceReadStatus.QueryStoreReadFailure => "query_store_read_failure", QueryPerformanceReadStatus.QueryStoreTimedOut => "query_store_timed_out", QueryPerformanceReadStatus.PlanCacheRows => "plan_cache_rows", QueryPerformanceReadStatus.PlanCacheEmpty => "plan_cache_empty", QueryPerformanceReadStatus.PlanCachePermissionDenied => "plan_cache_permission_denied", QueryPerformanceReadStatus.PlanCacheReadFailure => "plan_cache_read_failure", QueryPerformanceReadStatus.PlanCacheTimedOut => "plan_cache_timed_out", QueryPerformanceReadStatus.OutputCapped => "output_capped", _ => throw new InvalidDataException("Unknown query performance status.") };
    private static QueryStoreState StatusState(QueryPerformanceReadStatus value) => value switch { QueryPerformanceReadStatus.QueryStoreRows or QueryPerformanceReadStatus.QueryStoreEmpty => QueryStoreState.ReadWrite, QueryPerformanceReadStatus.QueryStoreDisabled => QueryStoreState.Disabled, QueryPerformanceReadStatus.QueryStorePermissionDenied or QueryPerformanceReadStatus.PlanCachePermissionDenied => QueryStoreState.PermissionDenied, QueryPerformanceReadStatus.QueryStoreTimedOut or QueryPerformanceReadStatus.PlanCacheTimedOut => QueryStoreState.TimedOut, QueryPerformanceReadStatus.QueryStoreReadFailure or QueryPerformanceReadStatus.PlanCacheReadFailure or QueryPerformanceReadStatus.PlanCacheRows or QueryPerformanceReadStatus.PlanCacheEmpty => QueryStoreState.ReadFailure, _ => QueryStoreState.Unsupported };
}
