using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public interface IServerLifecycleCollectionAdministrationService
{
    ValueTask<ServerLifecycleCollectionState?> GetAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<ServerLifecycleCollectionMutationResult> SetAsync(
        AuthorizationContext authorization, MonitoredInstanceId targetId,
        long expectedTargetRevision, long expectedScheduleRevision, bool expectedEnabled, bool enabled,
        AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken);
}

public sealed class ServerLifecycleCollectionAdministrationService(
    IServerLifecycleCollectionAdministrationPort repository,
    IAdministrativeAuditPort audit) : IServerLifecycleCollectionAdministrationService
{
    public ValueTask<ServerLifecycleCollectionState?> GetAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.RequireAny(targetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(targetRevision);
        return repository.GetAsync(targetId, targetRevision, timeout, cancellationToken);
    }

    public async ValueTask<ServerLifecycleCollectionMutationResult> SetAsync(
        AuthorizationContext authorization, MonitoredInstanceId targetId,
        long expectedTargetRevision, long expectedScheduleRevision, bool expectedEnabled, bool enabled,
        AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(expectedTargetRevision);
        SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.Revision(expectedScheduleRevision);
        if (expectedEnabled != enabled && expectedScheduleRevision == SqlObserver.Domain.Collection.ServerLifecycleHistoryValidation.MaximumRevision)
            throw new ArgumentOutOfRangeException(nameof(expectedScheduleRevision));
        var envelope = new AdministrativeAuditEnvelope(authorization.ActorSid,
            correlationId, AdministrativeAuditAction.UpdateObservationTarget, targetId);
        if (!authorization.CanAccess(ApplicationRole.TargetAdministrator, targetId))
        {
            AdministrativeAuditReason reason = !authorization.IsActive
                ? AdministrativeAuditReason.PrincipalDisabled
                : !authorization.HasRole(ApplicationRole.TargetAdministrator)
                    ? AdministrativeAuditReason.RequiredRoleMissing
                    : AdministrativeAuditReason.TargetOutOfScope;
            using var auditCancellation = new CancellationTokenSource(timeout.Value);
            await audit.AppendAsync(new AppendAdministrativeAuditRequest(
                new AdministrativeAuditRecord(envelope,
                    AdministrativeAuthorizationDecision.Denied,
                    AdministrativeOperationOutcome.Denied, reason), timeout),
                auditCancellation.Token).ConfigureAwait(false);
            throw new UnauthorizedAccessException("Target administration is required.");
        }
        return await repository.SetAsync(new SetServerLifecycleCollectionRequest(targetId,
            expectedTargetRevision, expectedScheduleRevision, expectedEnabled, enabled, envelope, timeout),
            cancellationToken).ConfigureAwait(false);
    }
}
