using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using System.Text.Json.Serialization;

namespace SqlObserver.Application.Ports;

public static class McpQueryBounds
{
    public const int MaximumResponseBytes = 1_048_576;
}

/// <summary>One bounded raw metric-series query. The repository owns the snapshot.</summary>
public sealed record MetricSeriesQuery(
    AuthorizationContext Authorization,
    MonitoredInstanceId TargetId,
    string MetricKey,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Limit,
    RepositoryCallTimeout Timeout,
    ObservationTargetRevision? TargetRevision = null,
    DateTimeOffset? SnapshotUtc = null,
    MetricSeriesCursor? Cursor = null);

public sealed record MetricSeriesCursor
{
    public MetricSeriesCursor(MonitoredInstanceId targetId, string metricKey, DateTimeOffset observedAtUtc, DateTimeOffset snapshotUtc)
        : this(targetId, metricKey, observedAtUtc, Guid.Empty, "{}", snapshotUtc, new ObservationTargetRevision(1)) { }

    public MetricSeriesCursor(MonitoredInstanceId targetId, string metricKey, DateTimeOffset observedAtUtc, Guid runId, string dimensionsKey, DateTimeOffset snapshotUtc)
        : this(targetId, metricKey, observedAtUtc, runId, dimensionsKey, snapshotUtc, new ObservationTargetRevision(1)) { }

    [JsonConstructor]
    public MetricSeriesCursor(MonitoredInstanceId targetId, string metricKey, DateTimeOffset observedAtUtc, Guid runId, string dimensionsKey, DateTimeOffset snapshotUtc, ObservationTargetRevision targetRevision)
    { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); MetricKey = RequireMetric(metricKey); ObservedAtUtc = RequireUtc(observedAtUtc); RunId = runId; DimensionsKey = string.IsNullOrWhiteSpace(dimensionsKey) || dimensionsKey.Length > 16384 ? throw new ArgumentException("Metric-series tie key is invalid.", nameof(dimensionsKey)) : dimensionsKey; SnapshotUtc = RequireUtc(snapshotUtc); TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision)); }

    public MonitoredInstanceId TargetId { get; }
    public string MetricKey { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public Guid RunId { get; }
    /// <summary>Canonical JSONB text for the final primary-key tie component.</summary>
    public string DimensionsKey { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public ObservationTargetRevision TargetRevision { get; }

    private static string RequireMetric(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 128 ? throw new ArgumentException("Metric key is invalid.", nameof(value)) : value;
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
}

public sealed record MetricSeriesItem(DateTimeOffset ObservedAtUtc, double Value, IReadOnlyDictionary<string, string> Dimensions);
public sealed record MetricSeriesPage(
    MonitoredInstanceId TargetId,
    string MetricKey,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<MetricSeriesItem> Items,
    string State,
    ObservationTargetRevision TargetRevision,
    DateTimeOffset SnapshotUtc,
    MetricSeriesCursor? NextCursor = null)
{
    public bool HasMore { get; init; }
}

public sealed record StorageForecastQuery(
    AuthorizationContext Authorization,
    MonitoredInstanceId TargetId,
    string MetricKey,
    TimeSpan Horizon,
    int Limit,
    RepositoryCallTimeout Timeout,
    ObservationTargetRevision? TargetRevision = null,
    DateTimeOffset? SnapshotUtc = null,
    IReadOnlyDictionary<string, string>? Dimensions = null,
    StorageForecastCursor? Cursor = null);

public sealed class StorageForecastCursor
{
    [JsonConstructor]
    public StorageForecastCursor(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, string metricKey, string dimensionsSha256, TimeSpan horizon, DateTimeOffset snapshotUtc, DateTimeOffset horizonStartUtc, Guid forecastId)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        MetricKey = string.IsNullOrWhiteSpace(metricKey) || metricKey.Length > 128 ? throw new ArgumentException("Forecast metric is invalid.", nameof(metricKey)) : metricKey;
        DimensionsSha256 = dimensionsSha256 is { Length: 64 } && dimensionsSha256.All(Uri.IsHexDigit) ? dimensionsSha256.ToLowerInvariant() : throw new ArgumentException("Forecast dimensions digest is invalid.", nameof(dimensionsSha256));
        Horizon = horizon >= TimeSpan.FromHours(1) && horizon <= TimeSpan.FromDays(366) ? horizon : throw new ArgumentOutOfRangeException(nameof(horizon));
        SnapshotUtc = RequireUtc(snapshotUtc); HorizonStartUtc = RequireUtc(horizonStartUtc);
        ForecastId = forecastId == Guid.Empty ? throw new ArgumentException("Forecast tie identifier is required.", nameof(forecastId)) : forecastId;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string MetricKey { get; }
    public string DimensionsSha256 { get; }
    public TimeSpan Horizon { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public DateTimeOffset HorizonStartUtc { get; }
    public Guid ForecastId { get; }
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
}

public sealed record StorageForecastItem(
    Guid? ForecastId,
    string MetricKey,
    DateTimeOffset HorizonStartUtc,
    DateTimeOffset HorizonEndUtc,
    double? Estimate,
    double? LowerBound,
    double? UpperBound,
    double? SlopePerDay,
    double Confidence,
    double Residual,
    string Model,
    long SourceGeneration,
    string VisibilityState,
    IReadOnlyDictionary<string, string> Dimensions,
    string DimensionsSha256);
public sealed record StorageForecastPage(
    MonitoredInstanceId TargetId,
    string MetricKey,
    TimeSpan Horizon,
    IReadOnlyList<StorageForecastItem> Items,
    string State,
    ObservationTargetRevision TargetRevision,
    DateTimeOffset SnapshotUtc,
    StorageForecastCursor? NextCursor = null)
{
    public bool HasMore { get; init; }
}

public sealed record DiagnosticEventSearchQuery(
    AuthorizationContext Authorization,
    MonitoredInstanceId TargetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Limit,
    RepositoryCallTimeout Timeout,
    ObservationTargetRevision? TargetRevision = null,
    DateTimeOffset? SnapshotUtc = null,
    DiagnosticEventCursor? Cursor = null);

public sealed record DiagnosticEventCursor
{
    public DiagnosticEventCursor(MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset occurredAtUtc, Guid eventId, DateTimeOffset snapshotUtc)
        : this(targetId, fromUtc, toUtc, occurredAtUtc, eventId, snapshotUtc, new ObservationTargetRevision(1)) { }
    [JsonConstructor]
    public DiagnosticEventCursor(MonitoredInstanceId targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset occurredAtUtc, Guid eventId, DateTimeOffset snapshotUtc, ObservationTargetRevision targetRevision)
    { TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); FromUtc = RequireUtc(fromUtc); ToUtc = RequireUtc(toUtc); OccurredAtUtc = RequireUtc(occurredAtUtc); EventId = RequireId(eventId); SnapshotUtc = RequireUtc(snapshotUtc); TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision)); if (ToUtc <= FromUtc || ToUtc - FromUtc > TimeSpan.FromDays(7)) throw new ArgumentException("Diagnostic cursor window is invalid."); }
    public MonitoredInstanceId TargetId { get; }
    public DateTimeOffset FromUtc { get; }
    public DateTimeOffset ToUtc { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public Guid EventId { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public ObservationTargetRevision TargetRevision { get; }
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
    private static Guid RequireId(Guid value) => value == Guid.Empty ? throw new ArgumentException("Event id is required.", nameof(value)) : value;
}

public sealed record DiagnosticEventSafeMetadata(string? MetricKey, int? ParticipantCount, int? RelationCount, bool? ParseTruncated);
public sealed record DiagnosticEventItem(
    DateTimeOffset OccurredAtUtc,
    Guid EventId,
    string EventKind,
    int Severity,
    DiagnosticEventSafeMetadata SafeMetadata,
    DateTimeOffset CollectedAtUtc,
    ObservationTargetRevision TargetRevision);
public sealed record DiagnosticEventSearchPage(
    MonitoredInstanceId TargetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<DiagnosticEventItem> Items,
    bool HasMore,
    DiagnosticEventCursor? NextCursor,
    ObservationTargetRevision TargetRevision,
    DateTimeOffset SnapshotUtc);

public sealed record IncidentEvidenceQuery(
    AuthorizationContext Authorization,
    MonitoredInstanceId TargetId,
    Guid ThreadId,
    int Limit,
    RepositoryCallTimeout Timeout,
    ObservationTargetRevision? TargetRevision = null,
    DateTimeOffset? SnapshotUtc = null,
    IncidentEvidenceCursor? Cursor = null);

/// <summary>
/// Immutable continuation for incident evidence. Evidence is ordered by its
/// complete (occurred_at, packet_id) key; generations are ordered by their
/// primary-key generation number. Either stream may be exhausted before the
/// other, so each stream's tie key is nullable and carried independently.
/// </summary>
public sealed record IncidentEvidenceCursor
{
    [JsonConstructor]
    public IncidentEvidenceCursor(
        MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision,
        Guid threadId,
        DateTimeOffset snapshotUtc,
        DateTimeOffset? evidenceOccurredAtUtc,
        Guid? evidencePacketId,
        long? generation)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        ThreadId = threadId == Guid.Empty ? throw new ArgumentException("Incident thread id is required.", nameof(threadId)) : threadId;
        SnapshotUtc = RequireUtc(snapshotUtc);
        if (evidenceOccurredAtUtc.HasValue != evidencePacketId.HasValue || evidenceOccurredAtUtc is { } evidenceAt && evidenceAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Incident evidence tie key is incomplete.", nameof(evidencePacketId));
        if (evidencePacketId is Guid id && id == Guid.Empty) throw new ArgumentException("Incident evidence packet id is required.", nameof(evidencePacketId));
        if (generation is < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        EvidenceOccurredAtUtc = evidenceOccurredAtUtc;
        EvidencePacketId = evidencePacketId;
        Generation = generation;
        if (EvidenceOccurredAtUtc is null && Generation is null) throw new ArgumentException("Incident continuation has no tie key.");
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public Guid ThreadId { get; }
    public DateTimeOffset SnapshotUtc { get; }
    public DateTimeOffset? EvidenceOccurredAtUtc { get; }
    public Guid? EvidencePacketId { get; }
    public long? Generation { get; }
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Timestamp must be UTC.");
}

public sealed record IncidentEvidenceItem(
    DateTimeOffset OccurredAtUtc,
    Guid PacketId,
    string EvidenceKind,
    Guid? SourceRunId,
    string SourceDigest,
    string IdentityDigest,
    string SourceCutoffDigest,
    DateTimeOffset? SourceCutoffUtc,
    double Confidence,
    string VisibilityState);
public sealed record IncidentGenerationItem(Guid ThreadId, long Generation, DateTimeOffset ObservedAtUtc, string CorrelationSha256, bool SupersedesPrevious, Guid? EvidencePacketId);
public sealed record IncidentEvidencePage(
    MonitoredInstanceId TargetId,
    Guid ThreadId,
    IReadOnlyList<IncidentEvidenceItem> Items,
    IReadOnlyList<IncidentGenerationItem> Generations,
    ObservationTargetRevision TargetRevision,
    DateTimeOffset SnapshotUtc,
    IncidentEvidenceCursor? NextCursor = null)
{
    public bool HasMore { get; init; }
}

public interface IMetricSeriesProjectionRepositoryPort
{
    ValueTask<MetricSeriesPage> ReadMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken);
}

public interface IStorageForecastProjectionRepositoryPort
{
    ValueTask<StorageForecastPage> ReadStorageForecastAsync(StorageForecastQuery query, CancellationToken cancellationToken);
}

public interface IDiagnosticEventProjectionRepositoryPort
{
    ValueTask<DiagnosticEventSearchPage> SearchDiagnosticEventsAsync(DiagnosticEventSearchQuery query, CancellationToken cancellationToken);
}

public interface IIncidentEvidenceProjectionRepositoryPort
{
    ValueTask<IncidentEvidencePage?> GetIncidentEvidenceAsync(IncidentEvidenceQuery query, CancellationToken cancellationToken);
}
