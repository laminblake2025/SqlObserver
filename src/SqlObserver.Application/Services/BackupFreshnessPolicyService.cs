using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Backups;

namespace SqlObserver.Application.Services;

public sealed class BackupFreshnessPolicyService(IBackupFreshnessPolicyPort repository, IAdministrativeAuditPort audit)
{
    public async ValueTask<BackupPolicyMutationResult> SetAsync(AuthorizationContext authorization,
        SetBackupFreshnessPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        if (request.Audit.ActorSid != authorization.ActorSid || request.Audit.TargetId != request.Policy.TargetId ||
            request.Audit.Action != AdministrativeAuditAction.UpdateObservationTarget)
            throw new ArgumentException("Policy audit identity does not match the caller and target.", nameof(request));
        if (!authorization.CanAccess(ApplicationRole.TargetAdministrator, request.Policy.TargetId))
        {
            var reason = !authorization.IsActive ? AdministrativeAuditReason.PrincipalDisabled :
                !authorization.HasRole(ApplicationRole.TargetAdministrator) ? AdministrativeAuditReason.RequiredRoleMissing :
                AdministrativeAuditReason.TargetOutOfScope;
            using var deadline = new CancellationTokenSource(request.Timeout.Value);
            await audit.AppendAsync(new(new(request.Audit, AdministrativeAuthorizationDecision.Denied,
                AdministrativeOperationOutcome.Denied, reason), request.Timeout), deadline.Token).ConfigureAwait(false);
            throw new UnauthorizedAccessException("TargetAdministrator on this target is required.");
        }
        return await repository.SetAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<BackupFreshnessPolicy?> ReadAsync(AuthorizationContext authorization,
        ReadBackupFreshnessPolicyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        authorization.RequireAny(request.TargetId, ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator);
        return repository.ReadAsync(request, cancellationToken);
    }
}
