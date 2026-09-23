using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Retention;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Targets;
using System.Text.Json;

namespace SqlObserver.Application.Ports;

public sealed record AnalyticsQueryRequest(MonitoredInstanceId TargetId, string MetricKey, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Limit, RepositoryCallTimeout Timeout, ObservationTargetRevision? TargetRevision = null, DateTimeOffset? SnapshotUtc = null, string? DimensionsSha256 = null, IReadOnlyDictionary<string, string>? Dimensions = null);
public sealed record AnalyticsRollupPage(
    IReadOnlyList<RollupResult> Items,
    bool HasMore,
    string? NextCursor,
    long TargetRevision,
    long Generation,
    DateTimeOffset SnapshotUtc,
    DateTimeOffset SourceCutoffUtc);

public sealed record AnalyticsJobRequest(Guid JobId, MonitoredInstanceId? TargetId, string WorkKey, WorkerLeaseIdentity Lease, RepositoryCallTimeout Timeout, ObservationTargetRevision? TargetRevision = null)
{
    public static void Validate(AnalyticsJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.JobId == Guid.Empty) throw new ArgumentException("A job id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.WorkKey) || request.WorkKey.Length > 256) throw new ArgumentException("A bounded work key is required.", nameof(request));
    }
}

public interface IAnalyticsRepositoryPort
{
    ValueTask<IReadOnlyList<MetricPoint>> ReadMetricPointsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<RollupResult>> ReadRollupsAsync(AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken);
    ValueTask<AnalyticsRollupPage> ReadRollupPageAsync(AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<BaselineResult>> ReadBaselinesAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ForecastResult>> ReadForecastsAsync(AnalyticsQueryRequest request, TimeSpan horizon, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<IncidentThread>> ReadIncidentsAsync(AnalyticsQueryRequest request, CancellationToken cancellationToken);
    ValueTask StoreRollupsAsync(AnalyticsJobRequest request, IReadOnlyList<RollupResult> rollups, CancellationToken cancellationToken);
    ValueTask StoreBaselineAsync(AnalyticsJobRequest request, IReadOnlyList<BaselineResult> baselines, CancellationToken cancellationToken);
    ValueTask StoreForecastAsync(AnalyticsJobRequest request, ForecastResult forecast, CancellationToken cancellationToken);
    ValueTask StoreEvidenceAsync(AnalyticsJobRequest request, EvidencePacket packet, CancellationToken cancellationToken);
    ValueTask StoreIncidentAsync(AnalyticsJobRequest request, IncidentThread thread, CancellationToken cancellationToken);
    ValueTask StoreIncidentGenerationAsync(AnalyticsJobRequest request, IncidentGeneration generation, CancellationToken cancellationToken) => throw new NotSupportedException("Incident-generation persistence is not supported by this adapter.");
}

/// <summary>One bounded repository-owned analytics derivation invocation.</summary>
public sealed record AnalyticsDerivationJob(
    Guid JobId,
    MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision,
    string JobKind,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset SourceCutoffUtc,
    long Generation = 1,
    string? MetricKey = null,
    TimeSpan? ForecastHorizon = null,
    string? DimensionsSha256 = null,
    RollupInterval? RollupInterval = null,
    DateTimeOffset? RequestedAtUtc = null)
{
    public void Validate()
    {
        if (JobId == Guid.Empty || TargetId is null || TargetRevision.Value < 1 ||
            JobKind is not ("rollup" or "baseline" or "forecast" or "evidence" or "correlation" or "incident") ||
            FromUtc.Offset != TimeSpan.Zero || ToUtc.Offset != TimeSpan.Zero || SourceCutoffUtc.Offset != TimeSpan.Zero ||
            RequestedAtUtc is { Offset: var requestedOffset } && requestedOffset != TimeSpan.Zero ||
            ToUtc <= FromUtc || ToUtc - FromUtc > TimeSpan.FromDays(90) || SourceCutoffUtc < ToUtc ||
            Generation < 1 || JobKind is ("baseline" or "forecast" or "rollup") && string.IsNullOrWhiteSpace(MetricKey) || MetricKey?.Length > 128 || MetricKey?.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) == true ||
            DimensionsSha256 is { } dimensionHash && (dimensionHash.Length != 64 || !dimensionHash.All(Uri.IsHexDigit)) ||
            JobKind == "rollup" && RollupInterval is null ||
            ForecastHorizon is { } horizon && (horizon <= TimeSpan.Zero || horizon > TimeSpan.FromDays(90)))
            throw new ArgumentException("Analytics derivation job is outside its bounded contract.", nameof(JobId));
    }
}

public sealed record AnalyticsForecastInput(IReadOnlyList<MetricPoint> Points, double? Capacity)
{
    /// <summary>Production forecast inputs are daily rollups, not raw mixed telemetry.</summary>
    public IReadOnlyList<RollupResult> DailyRollups { get; init; } = Array.Empty<RollupResult>();
    public string? DimensionsSha256 { get; init; }
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
public sealed record AnalyticsEvidenceInput(DateTimeOffset OccurredAtUtc, IReadOnlyList<EvidenceReference> References, IReadOnlyList<string> Tombstones)
{
    public void Validate()
    {
        if (References is null || Tombstones is null || OccurredAtUtc.Offset != TimeSpan.Zero || References.Count > 100_000 || Tombstones.Count > 256)
            throw new ArgumentException("Analytics evidence input is outside its bounded contract.", nameof(OccurredAtUtc));
    }
}

public enum AnalyticsDerivationCompletion { Succeeded = 1, Partial = 2, Failed = 3, Cancelled = 4 }

/// <summary>
/// Repository-only control and input boundary for production derivations.
/// Implementations must use fixed SECURITY DEFINER projections; no target
/// connection or caller-provided SQL is permitted.
/// </summary>
public interface IAnalyticsDerivationStore
{
    /// <summary>
    /// Schedules the bounded set of due derivation jobs under the exact
    /// analytics/derivation lease. The returned count is repository-owned
    /// telemetry and is never used as a claim limit.
    /// </summary>
    ValueTask<int> ScheduleAsync(WorkerLeaseIdentity lease, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<AnalyticsDerivationJob>> ClaimAsync(WorkerLeaseIdentity lease, int maximumJobs, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MetricPoint>> ReadRollupInputsAsync(AnalyticsDerivationJob job, RollupInterval interval, CancellationToken cancellationToken) => throw new NotSupportedException("Rollup derivation is not supported by this adapter.");
    ValueTask<IReadOnlyList<RollupResult>> ReadBaselineInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken);
    ValueTask<AnalyticsForecastInput> ReadForecastInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<AnalyticsEvidenceInput>> ReadEvidenceInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<EvidencePacket>> ReadIncidentInputsAsync(AnalyticsDerivationJob job, CancellationToken cancellationToken);
    ValueTask CompleteAsync(AnalyticsDerivationJob job, WorkerLeaseIdentity lease, AnalyticsDerivationCompletion completion, string? failureDetail, CancellationToken cancellationToken);
}

/// <summary>Bounded read/mutation port for the remaining M10 surfaces. Implementations return sanitized JSON only.</summary>
public sealed record AnalyticsSurfacePage(string Surface, string State, IReadOnlyList<JsonElement> Items, string? NextCursor, DateTimeOffset CutoffUtc, long Generation)
{
    // Generation and target revision are different fences.  The optional
    // properties keep older repository adapters source-compatible while new
    // adapters must populate the independent values returned by the DB.
    public long TargetRevision { get; init; } = Generation;
    public DateTimeOffset SnapshotUtc { get; init; } = CutoffUtc;
}
public sealed record AnalyticsMutationReceipt(Guid OperationId, string State, long? Revision, DateTimeOffset AcceptedAtUtc);
public sealed record BackfillMutationRequest(
    Guid TargetId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? MetricKey,
    long ExpectedRevision,
    Guid OperationId,
    ReadOnlyMemory<byte> RequestDigest,
    string ActorSid,
    Guid CorrelationId,
    string ChangeReason);
public sealed record HostBindingMutationRequest(
    Guid TargetId,
    JsonElement Body,
    long ExpectedRevision,
    Guid OperationId,
    ReadOnlyMemory<byte> RequestDigest,
    string ActorSid,
    Guid CorrelationId,
    string ChangeReason);
public sealed record AttestationMutationRequest(
    JsonElement Body,
    Guid OperationId,
    string ActorSid,
    Guid CorrelationId,
    string ChangeReason,
    ReadOnlyMemory<byte> RequestDigest = default);
public interface IAnalyticsSurfaceRepositoryPort
{
    ValueTask<AnalyticsSurfacePage> ReadSurfaceAsync(Guid targetId, string surface, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken cancellationToken);
    ValueTask<AnalyticsMutationReceipt> StartBackfillAsync(BackfillMutationRequest request, CancellationToken cancellationToken);
    ValueTask<AnalyticsMutationReceipt> BindHostAsync(HostBindingMutationRequest request, CancellationToken cancellationToken);
    ValueTask<AnalyticsMutationReceipt> RecordAttestationAsync(AttestationMutationRequest request, CancellationToken cancellationToken);
}

public interface IAnalyticsQueryService
{
    ValueTask<IReadOnlyList<RollupResult>> GetRollupsAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken);
    ValueTask<AnalyticsRollupPage> GetRollupPageAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken);
    ValueTask<WindowComparisonResult> CompareAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, DateTimeOffset leftStartUtc, DateTimeOffset leftEndUtc, DateTimeOffset rightStartUtc, DateTimeOffset rightEndUtc, CancellationToken cancellationToken);
}

public sealed record RetentionPreviewQuery(AuthorizationContext Authorization, int MaxEntries, RepositoryCallTimeout Timeout, string? Cursor = null);
public sealed record RetentionExecutionRequest(
    AuthorizationContext Authorization,
    string DataClass,
    string ParentSchema,
    string ParentTable,
    string PartitionName,
    Guid ExecutionId,
    RepositoryCallTimeout Timeout,
    long ExpectedPolicyRevision,
    string RequestDigest,
    Guid CorrelationId,
    string ChangeReason,
    string Operation,
    Guid OperationId);

public interface IRetentionRepositoryPort
{
    ValueTask<SqlObserver.Domain.Retention.RetentionPreview> PreviewAsync(RetentionPreviewQuery request, CancellationToken cancellationToken);
    ValueTask<RetentionExecutionResult> ExecuteAsync(RetentionExecutionRequest request, CancellationToken cancellationToken);
}

public interface IRetentionService
{
    ValueTask<SqlObserver.Domain.Retention.RetentionPreview> PreviewAsync(RetentionPreviewQuery request, CancellationToken cancellationToken);
    ValueTask<RetentionExecutionResult> ExecuteAsync(RetentionExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record RetentionPolicyUpdateRequest(AuthorizationContext Authorization, RetentionPolicy Policy, long ExpectedRevision, string ChangeReason, RepositoryCallTimeout Timeout);
public sealed record RetentionPolicyReadResult(RetentionPolicy Policy, long Revision, DateTimeOffset ReadAtUtc);
public interface IRetentionPolicyRepositoryPort
{
    ValueTask<RetentionPolicyReadResult> GetPolicyAsync(string dataClass, CancellationToken cancellationToken);
    ValueTask<RetentionPolicyReadResult> UpdatePolicyAsync(RetentionPolicyUpdateRequest request, CancellationToken cancellationToken);
}
public interface IRetentionPolicyService
{
    ValueTask<RetentionPolicyReadResult> GetPolicyAsync(AuthorizationContext authorization, string dataClass, CancellationToken cancellationToken);
    ValueTask<RetentionPolicyReadResult> UpdatePolicyAsync(RetentionPolicyUpdateRequest request, CancellationToken cancellationToken);
}
