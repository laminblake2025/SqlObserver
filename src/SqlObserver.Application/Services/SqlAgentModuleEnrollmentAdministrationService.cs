using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class SqlAgentModuleEnrollmentAdministrationService(
    ISqlAgentModuleEnrollmentAdministrationPort repository, IAdministrativeAuditPort audit)
{
    public async ValueTask<SqlAgentModuleEnrollmentMutationResult> SetAsync(AuthorizationContext authorization,
        SqlAgentModuleEnrollmentChange change, AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
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
            throw new UnauthorizedAccessException("Target administration is required.");
        }
        var result = await repository.SetAsync(new(change, envelope, timeout), cancellationToken).ConfigureAwait(false);
        if (result.Status is SqlAgentModuleEnrollmentMutationStatus.Applied or SqlAgentModuleEnrollmentMutationStatus.Replayed &&
            result.Revision != change.ExpectedRevision + 1)
            throw new InvalidDataException("Invalid module enrollment mutation receipt.");
        return result;
    }

    public async ValueTask<SqlAgentModuleEnrollmentReadResult> ReadAsync(AuthorizationContext authorization,
        MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(revision);
        authorization.RequireAny(targetId, ApplicationRole.TargetAdministrator);
        var result = await repository.ReadAsync(targetId, revision, cancellationToken).ConfigureAwait(false);
        if (result.Status == SqlAgentModuleEnrollmentReadStatus.Found &&
            (result.Snapshot is null || result.Snapshot.Enrollment is { } enrollment &&
                (enrollment.TargetId != targetId || enrollment.TargetRevision != revision)))
            throw new InvalidDataException("Invalid module enrollment read receipt.");
        return result;
    }
}
