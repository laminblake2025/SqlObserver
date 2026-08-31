using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

/// <summary>Application authorization and invariant boundary for MCP metric-series reads.</summary>
public sealed class MetricSeriesQueryService(IMetricSeriesProjectionRepositoryPort repository) : IMetricSeriesQueryService
{
    private readonly IMetricSeriesProjectionRepositoryPort _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<MetricSeriesPage> GetAsync(MetricSeriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Authorize(query.Authorization, query.TargetId);
        ValidateWindow(query.FromUtc, query.ToUtc, TimeSpan.FromDays(31));
        ValidateMetric(query.MetricKey);
        if (query.Timeout is null || query.Limit is < 1 or > 1000 || query.SnapshotUtc is { } snapshot && snapshot.Offset != TimeSpan.Zero || query.TargetRevision is { Value: < 1 }) throw new ArgumentException("Metric-series bounds are invalid.", nameof(query));
        if (query.Cursor is not null && (query.Cursor.TargetId != query.TargetId || query.Cursor.MetricKey != query.MetricKey || query.Cursor.RunId == Guid.Empty || query.Cursor.TargetRevision.Value < 1 || !McpQueryValidation.IsCanonicalDimensionsKey(query.Cursor.DimensionsKey) || query.Cursor.ObservedAtUtc < query.FromUtc || query.Cursor.ObservedAtUtc >= query.ToUtc || query.Cursor.SnapshotUtc != (query.SnapshotUtc ?? query.Cursor.SnapshotUtc) || query.TargetRevision is { } requestedRevision && requestedRevision != query.Cursor.TargetRevision)) throw new ArgumentException("Metric-series cursor does not match the query.", nameof(query));
        // A continuation is a frozen read: never re-resolve the current
        // target revision or repository snapshot between pages.
        MetricSeriesQuery effectiveQuery = query.Cursor is { } frozen
            ? query with { TargetRevision = frozen.TargetRevision, SnapshotUtc = query.SnapshotUtc ?? frozen.SnapshotUtc }
            : query;
        MetricSeriesPage result = await _repository.ReadMetricSeriesAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
        ValidateResult(result, effectiveQuery);
        return result;
    }

    private static void ValidateResult(MetricSeriesPage result, MetricSeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(result);
        MetricCatalogEntry metric = MetricCatalogV1.Get(query.MetricKey);
        if (result.TargetId != query.TargetId || result.MetricKey != query.MetricKey || result.FromUtc != query.FromUtc || result.ToUtc != query.ToUtc || query.TargetRevision is { } requested && result.TargetRevision != requested || result.TargetRevision.Value < 1 || result.SnapshotUtc.Offset != TimeSpan.Zero || !McpQueryValidation.IsState(result.State)) throw new InvalidDataException("Metric-series projection exceeded its contract.");
        IReadOnlyList<MetricSeriesItem> items = result.Items ?? throw new InvalidDataException("Metric-series projection omitted items.");
        if (items.Count > query.Limit || result.State == "no_data" && items.Count != 0 || result.State != "no_data" && items.Count == 0 || items.Any(item => item is null || item.ObservedAtUtc < query.FromUtc || item.ObservedAtUtc >= query.ToUtc || item.ObservedAtUtc > result.SnapshotUtc || item.ObservedAtUtc.Offset != TimeSpan.Zero || !double.IsFinite(item.Value) || item.Dimensions is null || item.Dimensions.Count > 32 || !metric.AllowsDimensions(item.Dimensions) || item.Dimensions.Any(pair => pair.Key is null or { Length: 0 or > 64 } || pair.Value is null or { Length: > 256 }))) throw new InvalidDataException("Metric-series projection exceeded its contract.");
        if (items.Zip(items.Skip(1)).Any(pair => pair.First.ObservedAtUtc > pair.Second.ObservedAtUtc)) throw new InvalidDataException("Metric-series projection is not ordered.");
        if (result.HasMore != (result.NextCursor is not null) || result.HasMore && items.Count != query.Limit || result.NextCursor is not null && (result.NextCursor.TargetId != query.TargetId || result.NextCursor.MetricKey != query.MetricKey || result.NextCursor.RunId == Guid.Empty || result.NextCursor.TargetRevision != result.TargetRevision || !McpQueryValidation.IsCanonicalDimensionsKey(result.NextCursor.DimensionsKey) || result.NextCursor.SnapshotUtc != result.SnapshotUtc || result.NextCursor.ObservedAtUtc < query.FromUtc || result.NextCursor.ObservedAtUtc >= query.ToUtc || result.NextCursor.ObservedAtUtc != items[^1].ObservedAtUtc)) throw new InvalidDataException("Metric-series continuation is invalid.");
        McpQueryValidation.EnsureResponseSize(result);
    }

    private static void ValidateMetric(string metric) { if (!MetricCatalogV1.TryGet(metric, out MetricCatalogEntry? entry) || !entry!.Enabled) throw new ArgumentException("Metric is not in the enabled catalog.", nameof(metric)); }
    private static void ValidateWindow(DateTimeOffset from, DateTimeOffset to, TimeSpan max) { if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from || to - from > max) throw new ArgumentException("The UTC query window is invalid."); }
    internal static void Authorize(AuthorizationContext authorization, MonitoredInstanceId targetId) { ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(targetId); if (!authorization.IsActive || !(authorization.CanAccess(ApplicationRole.Viewer, targetId) || authorization.CanAccess(ApplicationRole.Operator, targetId) || authorization.CanAccess(ApplicationRole.TargetAdministrator, targetId))) throw new UnauthorizedAccessException("The caller is not authorized for this projection."); }
}

public interface IMetricSeriesQueryService
{
    ValueTask<MetricSeriesPage> GetAsync(MetricSeriesQuery query, CancellationToken cancellationToken);
}

public sealed class StorageForecastQueryService(IStorageForecastProjectionRepositoryPort repository) : IStorageForecastQueryService
{
    private readonly IStorageForecastProjectionRepositoryPort _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<StorageForecastPage> GetAsync(StorageForecastQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        MetricSeriesQueryService.Authorize(query.Authorization, query.TargetId);
        if (!MetricCatalogV1.TryGet(query.MetricKey, out MetricCatalogEntry? metric) || !metric!.Enabled) throw new ArgumentException("Metric is not in the enabled catalog.", nameof(query));
        if (query.Timeout is null || query.Horizon < TimeSpan.FromHours(1) || query.Horizon > TimeSpan.FromDays(366) || query.Limit is < 1 or > 200 || query.SnapshotUtc is { } snapshot && snapshot.Offset != TimeSpan.Zero || query.TargetRevision is { Value: < 1 }) throw new ArgumentException("Forecast bounds are invalid.", nameof(query));
        if (query.Cursor is { } cursor && (cursor.TargetId != query.TargetId || cursor.MetricKey != query.MetricKey || cursor.Horizon != query.Horizon || query.TargetRevision is { } requested && requested != cursor.TargetRevision || query.SnapshotUtc is { } requestedSnapshot && requestedSnapshot != cursor.SnapshotUtc)) throw new ArgumentException("Forecast cursor does not match the query.", nameof(query));
        IReadOnlyDictionary<string, string> dimensions = CanonicalDimensions.Normalize(query.Dimensions);
        if (!metric.AllowsDimensions(dimensions)) throw new ArgumentException("Forecast dimensions are not allowlisted for this metric.", nameof(query));
        string dimensionsSha256 = CanonicalDimensions.Sha256(dimensions);
        if (query.Cursor is { } dimensionsCursor && !string.Equals(dimensionsCursor.DimensionsSha256, dimensionsSha256, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Forecast cursor dimensions do not match the query.", nameof(query));
        StorageForecastQuery effectiveQuery = query.Cursor is { } frozen ? query with { TargetRevision = frozen.TargetRevision, SnapshotUtc = query.SnapshotUtc ?? frozen.SnapshotUtc, Dimensions = dimensions } : query with { Dimensions = dimensions };
        StorageForecastPage result = await _repository.ReadStorageForecastAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
        ValidateResult(result, effectiveQuery, dimensions);
        return result;
    }

    private static void ValidateResult(StorageForecastPage result, StorageForecastQuery query, IReadOnlyDictionary<string, string> dimensions)
    {
        MetricCatalogEntry metric = MetricCatalogV1.Get(query.MetricKey);
        if (result.TargetId != query.TargetId || result.MetricKey != query.MetricKey || result.Horizon != query.Horizon || query.TargetRevision is { } requested && result.TargetRevision != requested || result.TargetRevision.Value < 1 || result.SnapshotUtc.Offset != TimeSpan.Zero || !McpQueryValidation.IsState(result.State)) throw new InvalidDataException("Storage forecast projection exceeded its contract.");
        IReadOnlyList<StorageForecastItem> items = result.Items ?? throw new InvalidDataException("Storage forecast projection omitted items.");
        if (items.Count > query.Limit || result.State == "no_data" && items.Count != 0 || result.State != "no_data" && items.Count == 0 || items.Any(item => item is null || item.MetricKey != query.MetricKey || item.HorizonEndUtc <= item.HorizonStartUtc || item.HorizonEndUtc - item.HorizonStartUtc > query.Horizon || item.HorizonStartUtc.Offset != TimeSpan.Zero || item.HorizonEndUtc.Offset != TimeSpan.Zero || !Finite(item.Estimate) || !Finite(item.LowerBound) || !Finite(item.UpperBound) || item.Estimate is { } estimate && item.LowerBound is { } lower && lower > estimate || item.Estimate is { } estimate2 && item.UpperBound is { } upper && upper < estimate2 || item.LowerBound is { } lower2 && item.UpperBound is { } upper2 && lower2 > upper2 || !double.IsFinite(item.Confidence) || item.Confidence is < 0 or > 1 || !double.IsFinite(item.Residual) || item.SourceGeneration < 1 || !McpQueryValidation.IsVisibility(item.VisibilityState) || !McpQueryValidation.IsSha256(item.DimensionsSha256) || item.Dimensions is null || !metric.AllowsDimensions(item.Dimensions) || !string.Equals(CanonicalDimensions.Sha256(item.Dimensions), item.DimensionsSha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(CanonicalDimensions.Sha256(dimensions), item.DimensionsSha256, StringComparison.OrdinalIgnoreCase) || item.Dimensions.Count > 32 || item.Model is null or { Length: 0 or > 64 })) throw new InvalidDataException("Storage forecast projection exceeded its contract.");
        if (result.HasMore && items.Count != query.Limit || items.Count < query.Limit && result.HasMore || result.HasMore != (result.NextCursor is not null) || result.NextCursor is { } next && (next.TargetId != query.TargetId || next.TargetRevision != result.TargetRevision || next.MetricKey != query.MetricKey || next.Horizon != query.Horizon || !string.Equals(next.DimensionsSha256, CanonicalDimensions.Sha256(dimensions), StringComparison.OrdinalIgnoreCase) || next.SnapshotUtc != result.SnapshotUtc || next.HorizonStartUtc != items[^1].HorizonStartUtc || next.ForecastId != items[^1].ForecastId)) throw new InvalidDataException("Storage forecast pagination invariant failed.");
        McpQueryValidation.EnsureResponseSize(result);
    }
    private static bool Finite(double? value) => value is null || double.IsFinite(value.Value);
    private static bool IsVisibility(string value) => McpQueryValidation.IsVisibility(value);
    private static bool IsSha256(string value) => McpQueryValidation.IsSha256(value);
}

public interface IStorageForecastQueryService
{
    ValueTask<StorageForecastPage> GetAsync(StorageForecastQuery query, CancellationToken cancellationToken);
}

public sealed class DiagnosticEventQueryService(IDiagnosticEventProjectionRepositoryPort repository) : IDiagnosticEventQueryService
{
    private readonly IDiagnosticEventProjectionRepositoryPort _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async ValueTask<DiagnosticEventSearchPage> SearchAsync(DiagnosticEventSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        MetricSeriesQueryService.Authorize(query.Authorization, query.TargetId);
        if (query.Timeout is null || query.Limit is < 1 or > 100 || query.SnapshotUtc is { } snapshot && snapshot.Offset != TimeSpan.Zero || query.TargetRevision is { Value: < 1 }) throw new ArgumentException("Diagnostic search bounds are invalid.", nameof(query));
        ValidateWindow(query.FromUtc, query.ToUtc, TimeSpan.FromDays(7));
        if (query.Cursor is { } cursor && (cursor.TargetId != query.TargetId || cursor.FromUtc != query.FromUtc || cursor.ToUtc != query.ToUtc || cursor.OccurredAtUtc < query.FromUtc || cursor.OccurredAtUtc >= query.ToUtc || cursor.SnapshotUtc != (query.SnapshotUtc ?? cursor.SnapshotUtc) || query.TargetRevision is { } requestedRevision && requestedRevision != cursor.TargetRevision)) throw new ArgumentException("Diagnostic cursor does not match the query.", nameof(query));
        DiagnosticEventSearchQuery effectiveQuery = query.Cursor is { } frozen ? query with { TargetRevision = frozen.TargetRevision, SnapshotUtc = query.SnapshotUtc ?? frozen.SnapshotUtc } : query;
        DiagnosticEventSearchPage result = await _repository.SearchDiagnosticEventsAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
        if (result.TargetId != effectiveQuery.TargetId || effectiveQuery.TargetRevision is { } requested && result.TargetRevision != requested || result.TargetRevision.Value < 1 || result.FromUtc != effectiveQuery.FromUtc || result.ToUtc != effectiveQuery.ToUtc || result.SnapshotUtc.Offset != TimeSpan.Zero || result.Items is null || result.Items.Count > effectiveQuery.Limit || result.HasMore != (result.NextCursor is not null) || result.HasMore && result.Items.Count != effectiveQuery.Limit || result.Items.Any(item => item is null || item.SafeMetadata is null || item.EventId == Guid.Empty || item.OccurredAtUtc.Offset != TimeSpan.Zero || item.CollectedAtUtc.Offset != TimeSpan.Zero || item.OccurredAtUtc < effectiveQuery.FromUtc || item.OccurredAtUtc >= effectiveQuery.ToUtc || item.EventKind is null or { Length: 0 or > 64 } || item.Severity is < 0 or > 100)) throw new InvalidDataException("Diagnostic event projection exceeded its contract.");
        if (result.Items.Zip(result.Items.Skip(1)).Any(pair => (pair.First.OccurredAtUtc, pair.First.EventId).CompareTo((pair.Second.OccurredAtUtc, pair.Second.EventId)) >= 0)) throw new InvalidDataException("Diagnostic event projection is not ordered.");
        if (result.NextCursor is not null && (result.NextCursor.TargetId != effectiveQuery.TargetId || result.NextCursor.TargetRevision != result.TargetRevision || result.NextCursor.FromUtc != effectiveQuery.FromUtc || result.NextCursor.ToUtc != effectiveQuery.ToUtc || result.NextCursor.SnapshotUtc != result.SnapshotUtc || result.Items.Count == 0 || result.NextCursor.OccurredAtUtc != result.Items[^1].OccurredAtUtc || result.NextCursor.EventId != result.Items[^1].EventId)) throw new InvalidDataException("Diagnostic continuation is invalid.");
        McpQueryValidation.EnsureResponseSize(result);
        return result;
    }
    private static void ValidateWindow(DateTimeOffset from, DateTimeOffset to, TimeSpan max) { if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || to <= from || to - from > max) throw new ArgumentException("The UTC query window is invalid."); }
}

public interface IDiagnosticEventQueryService
{
    ValueTask<DiagnosticEventSearchPage> SearchAsync(DiagnosticEventSearchQuery query, CancellationToken cancellationToken);
}

public sealed class IncidentEvidenceQueryService(IIncidentEvidenceProjectionRepositoryPort repository) : IIncidentEvidenceQueryService
{
    private readonly IIncidentEvidenceProjectionRepositoryPort _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async ValueTask<IncidentEvidencePage?> GetAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        MetricSeriesQueryService.Authorize(query.Authorization, query.TargetId);
        if (query.Timeout is null || query.ThreadId == Guid.Empty || query.Limit is < 1 or > 256 || query.SnapshotUtc is { } snapshot && snapshot.Offset != TimeSpan.Zero || query.TargetRevision is { Value: < 1 }) throw new ArgumentException("Incident evidence bounds are invalid.", nameof(query));
        if (query.Cursor is { } cursor && (cursor.TargetId != query.TargetId || cursor.ThreadId != query.ThreadId || cursor.TargetRevision.Value < 1 || query.TargetRevision is { } requestedRevision && requestedRevision != cursor.TargetRevision || query.SnapshotUtc is { } requestedSnapshot && requestedSnapshot != cursor.SnapshotUtc)) throw new ArgumentException("Incident evidence cursor does not match the query.", nameof(query));
        IncidentEvidenceQuery effectiveQuery = query.Cursor is { } frozen ? query with { TargetRevision = frozen.TargetRevision, SnapshotUtc = query.SnapshotUtc ?? frozen.SnapshotUtc } : query;
        IncidentEvidencePage? result = await _repository.GetIncidentEvidenceAsync(effectiveQuery, cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        if (result.TargetId != effectiveQuery.TargetId || result.ThreadId != effectiveQuery.ThreadId || effectiveQuery.TargetRevision is { } requested && result.TargetRevision != requested || result.TargetRevision.Value < 1 || result.SnapshotUtc.Offset != TimeSpan.Zero || result.Items is null || result.Generations is null || result.Items.Count > effectiveQuery.Limit || result.Generations.Count > effectiveQuery.Limit || result.Items.Any(item => item is null || item.PacketId == Guid.Empty || item.SourceRunId == Guid.Empty || item.OccurredAtUtc.Offset != TimeSpan.Zero || item.SourceCutoffUtc is { } cutoff && cutoff.Offset != TimeSpan.Zero || !double.IsFinite(item.Confidence) || item.Confidence is < 0 or > 1 || !IsSha256(item.SourceDigest) || !IsSha256(item.IdentityDigest) || !IsSha256(item.SourceCutoffDigest) || !IsVisibility(item.VisibilityState) || item.EvidenceKind is null or { Length: 0 or > 64 }) || result.Generations.Any(item => item is null || item.ThreadId != effectiveQuery.ThreadId || item.Generation is < 1 or > 1_000_000 || item.ObservedAtUtc.Offset != TimeSpan.Zero || !IsSha256(item.CorrelationSha256)) || result.Generations.Select(item => item.Generation).Distinct().Count() != result.Generations.Count || result.Generations.Zip(result.Generations.Skip(1)).Any(pair => pair.First.Generation >= pair.Second.Generation)) throw new InvalidDataException("Incident evidence projection exceeded its contract.");
        if (result.Items.Any(item => item.OccurredAtUtc > result.SnapshotUtc || item.SourceCutoffUtc > result.SnapshotUtc) || result.Generations.Any(item => item.ObservedAtUtc > result.SnapshotUtc)) throw new InvalidDataException("Incident evidence is newer than its frozen snapshot.");
        if (result.Items.Zip(result.Items.Skip(1)).Any(pair => (pair.First.OccurredAtUtc, pair.First.PacketId).CompareTo((pair.Second.OccurredAtUtc, pair.Second.PacketId)) >= 0)) throw new InvalidDataException("Incident evidence projection is not ordered.");
        if (result.HasMore != (result.NextCursor is not null) || result.HasMore && result.Items.Count == 0 && result.Generations.Count == 0) throw new InvalidDataException("Incident evidence pagination invariant failed.");
        if (result.NextCursor is { } next && (next.TargetId != effectiveQuery.TargetId || next.TargetRevision != result.TargetRevision || next.ThreadId != effectiveQuery.ThreadId || next.SnapshotUtc != result.SnapshotUtc || !CursorEvidenceMatches(next, result, effectiveQuery) || !CursorGenerationMatches(next, result, effectiveQuery))) throw new InvalidDataException("Incident evidence continuation is invalid.");
        McpQueryValidation.EnsureResponseSize(result);
        return result;
    }
    private static bool CursorEvidenceMatches(IncidentEvidenceCursor cursor, IncidentEvidencePage page, IncidentEvidenceQuery query) => cursor.EvidenceOccurredAtUtc is null
        ? cursor.EvidencePacketId is null
        : page.Items.Count > 0
            ? cursor.EvidenceOccurredAtUtc == page.Items[^1].OccurredAtUtc && cursor.EvidencePacketId == page.Items[^1].PacketId
            : query.Cursor?.EvidenceOccurredAtUtc == cursor.EvidenceOccurredAtUtc && query.Cursor.EvidencePacketId == cursor.EvidencePacketId;
    private static bool CursorGenerationMatches(IncidentEvidenceCursor cursor, IncidentEvidencePage page, IncidentEvidenceQuery query) => cursor.Generation is null
        ? true
        : page.Generations.Count > 0
            ? cursor.Generation == page.Generations[^1].Generation
            : query.Cursor?.Generation == cursor.Generation;
    private static bool IsVisibility(string value) => value is "complete" or "partial" or "unavailable" or "unsupported";
    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public interface IIncidentEvidenceQueryService
{
    ValueTask<IncidentEvidencePage?> GetAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken);
}

internal static class McpQueryValidation
{
    public static bool IsState(string value) => value is "no_data" or "complete" or "partial" or "unavailable" or "unsupported";
    public static bool IsVisibility(string value) => value is "complete" or "partial" or "unavailable" or "unsupported";
    public static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool IsCanonicalDimensionsKey(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || !dimensions.TryAdd(property.Name, property.Value.GetString()!)) return false;
            }
            return CanonicalDimensions.Json(CanonicalDimensions.Normalize(dimensions)) == value;
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static void EnsureResponseSize<T>(T result)
    {
        try
        {
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > McpQueryBounds.MaximumResponseBytes) throw new McpApplicationResponseOversizeException();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Projection could not be serialized safely.", exception);
        }
    }
}

/// <summary>Raised when a validated application projection cannot fit the MCP response budget.</summary>
public sealed class McpApplicationResponseOversizeException : Exception
{
    public McpApplicationResponseOversizeException() : base("The application projection exceeds the MCP response byte limit.") { }
}
