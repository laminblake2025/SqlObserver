using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public interface IConfigurationCollectionAdministrationService
{
    ValueTask<ConfigurationCollectionMutationResult> SetAsync(
        AuthorizationContext authorization, MonitoredInstanceId targetId,
        long expectedTargetRevision, bool expectedEnabled, bool enabled,
        AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken);
}

public sealed class ConfigurationCollectionAdministrationService(
    IConfigurationCollectionAdministrationPort repository,
    IAdministrativeAuditPort audit) : IConfigurationCollectionAdministrationService
{
    public async ValueTask<ConfigurationCollectionMutationResult> SetAsync(
        AuthorizationContext authorization, MonitoredInstanceId targetId,
        long expectedTargetRevision, bool expectedEnabled, bool enabled,
        AuditCorrelationId correlationId, RepositoryCallTimeout timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedTargetRevision, 1);
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
        return await repository.SetAsync(new SetConfigurationCollectionRequest(targetId,
            expectedTargetRevision, expectedEnabled, enabled, envelope, timeout),
            cancellationToken).ConfigureAwait(false);
    }
}
