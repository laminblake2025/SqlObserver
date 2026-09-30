using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class BackupZoneQualificationAdministrationService(
    IBackupZoneQualificationAdministrationPort repository, IAdministrativeAuditPort audit)
{
    public async ValueTask<BackupZoneQualificationMutationResult> SetAsync(AuthorizationContext authorization,
        BackupZoneQualificationChange change, AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(correlationId); ArgumentNullException.ThrowIfNull(timeout);
        var envelope = new AdministrativeAuditEnvelope(authorization.ActorSid, correlationId,
            AdministrativeAuditAction.UpdateObservationTarget, change.TargetId);
        if (!authorization.CanAccess(ApplicationRole.TargetAdministrator, change.TargetId))
        {
            var reason = !authorization.IsActive ? AdministrativeAuditReason.PrincipalDisabled :
                !authorization.HasRole(ApplicationRole.TargetAdministrator) ? AdministrativeAuditReason.RequiredRoleMissing :
                AdministrativeAuditReason.TargetOutOfScope;
            using var deadline = new CancellationTokenSource(timeout.Value);
            await audit.AppendAsync(new(new(envelope, AdministrativeAuthorizationDecision.Denied,
                AdministrativeOperationOutcome.Denied, reason), timeout), deadline.Token).ConfigureAwait(false);
            throw new UnauthorizedAccessException("TargetAdministrator on this target is required.");
        }
        return await repository.SetAsync(new(change, envelope, timeout), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<BackupZoneQualificationSnapshot?> ReadAsync(AuthorizationContext authorization,
        MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(revision);
        authorization.RequireAny(targetId, ApplicationRole.TargetAdministrator);
        return repository.ReadAsync(targetId, revision, cancellationToken);
    }
}
