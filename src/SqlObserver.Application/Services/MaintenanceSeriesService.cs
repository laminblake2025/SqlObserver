using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class MaintenanceSeriesService(IMaintenanceSeriesRepositoryPort repository,
    IAdministrativeAuditPort audit, TimeProvider clock) : IMaintenanceSeriesService
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static void Require(AuthorizationContext authorization, MonitoredInstanceId targetId)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(targetId);
        authorization.RequireAny(targetId, [ApplicationRole.TargetAdministrator]);
    }
    private static void Identity(Guid seriesId)
    { if (seriesId == Guid.Empty) throw new ArgumentException("Series identity is required.", nameof(seriesId)); }
    private static void Page(int limit, Guid? before)
    {
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        if (before == Guid.Empty) throw new ArgumentException("A nonempty continuation identity is required.", nameof(before));
    }
    public async ValueTask<MaintenanceSeriesStatus?> GetAsync(AuthorizationContext authorization,
        MonitoredInstanceId targetId, Guid seriesId, CancellationToken cancellationToken)
    {
        Require(authorization, targetId); Identity(seriesId);
        MaintenanceSeriesStatus? value = await repository.GetAsync(targetId, seriesId, Timeout, cancellationToken).ConfigureAwait(false);
        if (value is not null && (value.TargetId != targetId || value.SeriesId != seriesId)) throw new InvalidDataException("The maintenance series read returned a different resource.");
        return value;
    }
    public async ValueTask<MaintenanceSeriesListPage> ListAsync(AuthorizationContext authorization, MonitoredInstanceId targetId,
        int limit, Guid? beforeSeriesId, CancellationToken cancellationToken)
    {
        Require(authorization, targetId); Page(limit, beforeSeriesId);
        var page = await repository.ListAsync(targetId, limit, beforeSeriesId, Timeout, cancellationToken).ConfigureAwait(false);
        if (page.TargetId != targetId || page.Items.Count > limit || page.Items.Any(row => row.TargetId != targetId))
            throw new InvalidDataException("The maintenance series page does not match its requested resource.");
        return page;
    }
    public async ValueTask<MaintenanceSeriesOccurrencePage> ListOccurrencesAsync(AuthorizationContext authorization,
        MonitoredInstanceId targetId, Guid seriesId, int limit, Guid? beforeWindowId, CancellationToken cancellationToken)
    {
        Require(authorization, targetId); Identity(seriesId); Page(limit, beforeWindowId);
        var page = await repository.ListOccurrencesAsync(targetId, seriesId, limit, beforeWindowId, Timeout, cancellationToken).ConfigureAwait(false);
        if (page.TargetId != targetId || page.SeriesId != seriesId || page.Items.Count > limit)
            throw new InvalidDataException("The maintenance occurrence page does not match its requested resource.");
        return page;
    }
    public MaintenanceSeriesPreview Preview(AuthorizationContext authorization, MonitoredInstanceId targetId,
        Guid seriesId, MaintenanceSeriesDefinition definition, DateOnly? effectiveFromDate)
    {
        Require(authorization, targetId); Identity(seriesId);
        MaintenanceSeriesExpansionResult expanded = MaintenanceSeriesExpansion.Expand(seriesId, definition);
        DateTimeOffset now = clock.GetUtcNow();
        return new(targetId, seriesId, now, definition.DefinitionDigest, MaintenanceSeriesExpansion.Version,
            expanded.Occurrences.Where(row => row.StartsAtUtc > now && (effectiveFromDate is null || row.LocalDate >= effectiveFromDate)).ToArray(),
            expanded.SkippedDates);
    }
    public async ValueTask<MaintenanceSeriesWriteReceipt> WriteAsync(AuthorizationContext authorization,
        MaintenanceSeriesWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        if (request.Audit.ActorSid != authorization.ActorSid) throw new ArgumentException("The audit actor must match the authenticated principal.", nameof(request));
        Guid operationId = Guid.ParseExact(request.IdempotencyKey, "D");
        bool allowed = authorization.IsActive && authorization.CanAccess(ApplicationRole.TargetAdministrator, request.Audit.TargetId);
        if (!allowed)
        {
            AdministrativeAuditReason reason = !authorization.IsActive ? AdministrativeAuditReason.PrincipalDisabled :
                !authorization.CanAccess(request.Audit.TargetId) ? AdministrativeAuditReason.TargetOutOfScope : AdministrativeAuditReason.RequiredRoleMissing;
            await audit.AppendAsync(new(new(request.Audit, AdministrativeAuthorizationDecision.Denied,
                AdministrativeOperationOutcome.Denied, reason, operationId), Timeout), cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Target Administrator on this server is required for recurring maintenance.");
        }
        try
        {
            // Never filter by the current clock or reject ended/cancelled series here.
            // The repository checks immutable replay before native first-write fences.
            IReadOnlyList<MaintenanceSeriesOccurrence> occurrences = request.Definition is null ? [] :
                MaintenanceSeriesExpansion.Expand(request.SeriesId, request.Definition).Occurrences;
            return await repository.WriteAsync(request with { Occurrences = occurrences }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is AlertRepositoryOperationException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            bool conflict = error is AlertRepositoryOperationException { StatusCode: 409 };
            await audit.AppendAsync(new(new(request.Audit, AdministrativeAuthorizationDecision.Granted,
                conflict ? AdministrativeOperationOutcome.Conflict : AdministrativeOperationOutcome.Failed,
                conflict ? AdministrativeAuditReason.RevisionConflict : AdministrativeAuditReason.RepositoryFailure,
                operationId, error is AlertRepositoryOperationException native ? native.Code : error.GetType().Name), Timeout), cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
