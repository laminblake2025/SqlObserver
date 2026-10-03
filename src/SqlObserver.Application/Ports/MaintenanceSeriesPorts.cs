using System.Globalization;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record MaintenanceSeriesStatus(MonitoredInstanceId TargetId, Guid SeriesId, long Revision,
    MaintenanceSeriesDefinition Definition, DateOnly? CancelledFromDate, DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc, string ExpansionVersion, DateTimeOffset ServerNowUtc, int OccurrenceCount);
public sealed record MaintenanceSeriesListPage(MonitoredInstanceId TargetId, DateTimeOffset ServerNowUtc,
    IReadOnlyList<MaintenanceSeriesStatus> Items, Guid? NextBeforeSeriesId);
public sealed record MaintenanceSeriesOccurrenceItem(Guid WindowId, DateOnly LocalDate, DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc, string Reason, long Revision, DateTimeOffset? CancelledAtUtc, long SeriesRevision,
    bool Exception, int UtcOffsetMinutes, bool Ambiguous, string ExpansionVersion);
public sealed record MaintenanceSeriesOccurrencePage(MonitoredInstanceId TargetId, Guid SeriesId, DateTimeOffset ServerNowUtc,
    IReadOnlyList<MaintenanceSeriesOccurrenceItem> Items, Guid? NextBeforeWindowId);
public sealed record MaintenanceSeriesPreview(MonitoredInstanceId TargetId, Guid SeriesId, DateTimeOffset ServerNowUtc,
    string DefinitionDigest, string ExpansionVersion, IReadOnlyList<MaintenanceSeriesOccurrence> Occurrences,
    IReadOnlyList<MaintenanceSeriesSkippedDate> SkippedDates);
public sealed record MaintenanceSeriesWriteReceipt(AdministrativeAuditReceipt Audit, Guid SeriesId, long Revision,
    DateTimeOffset AppliedAtUtc, int CreatedOccurrences, int UpdatedOccurrences, int CancelledOccurrences, int PreservedExceptions);

public sealed record MaintenanceSeriesWriteRequest
{
    public MaintenanceSeriesWriteRequest(Guid seriesId, long? expectedRevision, DateOnly? effectiveFromDate,
        MaintenanceSeriesDefinition? definition, string reason, string idempotencyKey,
        AdministrativeAuditEnvelope audit, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(audit); ArgumentNullException.ThrowIfNull(timeout);
        if (seriesId == Guid.Empty || !AlertIdentifier.TryParseRfc4122(idempotencyKey, out _)) throw new ArgumentException("Canonical series and operation identities are required.");
        bool create = audit.Action == AdministrativeAuditAction.CreateMaintenanceSeries;
        bool update = audit.Action == AdministrativeAuditAction.UpdateMaintenanceSeries;
        bool cancel = audit.Action == AdministrativeAuditAction.CancelMaintenanceSeries;
        if (!create && !update && !cancel || create && (expectedRevision is not null || effectiveFromDate is not null) ||
            !create && (expectedRevision is null or <= 0 || effectiveFromDate is null) || cancel != (definition is null))
            throw new ArgumentException("The series action and revision-bound payload disagree.");
        Reason = MaintenanceSeriesDefinition.RequireReason(reason);
        if (definition is not null && definition.Reason != reason) throw new ArgumentException("The series reason must match its definition.", nameof(reason));
        SeriesId = seriesId; ExpectedRevision = expectedRevision; EffectiveFromDate = effectiveFromDate; Definition = definition;
        IdempotencyKey = idempotencyKey; Audit = audit; Timeout = timeout;
        string action = create ? "maintenance.series.create" : update ? "maintenance.series.update" : "maintenance.series.cancel";
        RequestDigest = MaintenanceSeriesExpansion.Digest(string.Join('|', action, audit.TargetId.Value.ToString("D"), seriesId.ToString("D"),
            expectedRevision?.ToString(CultureInfo.InvariantCulture) ?? "<null>", effectiveFromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "<null>",
            definition?.DefinitionDigest ?? "<null>", reason));
    }
    public Guid SeriesId { get; }
    public long? ExpectedRevision { get; }
    public DateOnly? EffectiveFromDate { get; }
    public MaintenanceSeriesDefinition? Definition { get; }
    public string Reason { get; }
    public string IdempotencyKey { get; }
    public AdministrativeAuditEnvelope Audit { get; }
    public RepositoryCallTimeout Timeout { get; }
    public string RequestDigest { get; }
    public IReadOnlyList<MaintenanceSeriesOccurrence> Occurrences { get; init; } = [];
    public string ExpansionVersion { get; } = MaintenanceSeriesExpansion.Version;
}

public interface IMaintenanceSeriesRepositoryPort
{
    ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(MaintenanceSeriesWriteRequest request, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesStatus?> GetAsync(MonitoredInstanceId targetId, Guid seriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesListPage> ListAsync(MonitoredInstanceId targetId, int limit, Guid? beforeSeriesId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(MonitoredInstanceId targetId, Guid seriesId, int limit, Guid? beforeWindowId, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
}
public interface IMaintenanceSeriesService
{
    ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(AuthorizationContext authorization, MaintenanceSeriesWriteRequest request, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesStatus?> GetAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid seriesId, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesListPage> ListAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, Guid? beforeSeriesId, CancellationToken cancellationToken);
    ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid seriesId, int limit, Guid? beforeWindowId, CancellationToken cancellationToken);
    MaintenanceSeriesPreview Preview(AuthorizationContext authorization, MonitoredInstanceId targetId, Guid seriesId, MaintenanceSeriesDefinition definition, DateOnly? effectiveFromDate);
}
