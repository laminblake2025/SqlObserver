using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class AlertQueryService(IAlertRepositoryPort repository) : IAlertQueryService
{
    private static readonly ApplicationRole[] ReadRoles =
    [
        ApplicationRole.Viewer, ApplicationRole.Operator, ApplicationRole.TargetAdministrator,
        ApplicationRole.SecurityAdministrator, ApplicationRole.Auditor,
    ];

    public ValueTask<IReadOnlyList<AlertActiveDto>> ListActiveAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(targetId);
        if (limit is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        authorization.RequireAny(targetId, ReadRoles);
        return repository.ListActiveAsync(targetId, limit, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cancellationToken);
    }
    public ValueTask<AlertActivePage> ListActivePageAsync(AuthorizationContext authorization, MonitoredInstanceId targetId, int limit, AlertActiveCursor? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(targetId);
        if (limit is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        authorization.RequireAny(targetId, ReadRoles);
        if (cursor is not null && (cursor.TargetId.Value != targetId.Value || cursor.SnapshotUtc.Offset != TimeSpan.Zero)) throw new UnauthorizedAccessException();
        return repository.ListActivePageAsync(targetId, limit, cursor, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), cancellationToken);
    }
}

public sealed class AlertAdministrationService(IAlertRepositoryPort repository, IAdministrativeAuditPort audit, IAlertDestinationApprovalPort? destinationApproval = null) : IAlertAdministrationService
{
    public ValueTask<AdministrativeAuditReceipt> UpsertRuleAsync(AuthorizationContext authorization, AlertRuleWriteRequest request, CancellationToken cancellationToken) =>
        WriteAsync(authorization, request.Audit, ApplicationRole.TargetAdministrator, request.Audit.TargetId, request.IdempotencyKey, () => repository.UpsertRuleAsync(request, cancellationToken), cancellationToken);

    public ValueTask<AdministrativeAuditReceipt> UpsertMaintenanceAsync(AuthorizationContext authorization, MaintenanceWriteRequest request, CancellationToken cancellationToken) =>
        WriteAsync(authorization, request.Audit, ApplicationRole.TargetAdministrator, request.Audit.TargetId, request.IdempotencyKey, () => repository.UpsertMaintenanceAsync(request, cancellationToken), cancellationToken);

    public ValueTask<AdministrativeAuditReceipt> CancelMaintenanceAsync(AuthorizationContext authorization, MaintenanceCancellationRequest request, CancellationToken cancellationToken) =>
        WriteAsync(authorization, request.Audit, ApplicationRole.TargetAdministrator, request.Audit.TargetId, request.IdempotencyKey, () => repository.CancelMaintenanceAsync(request, cancellationToken), cancellationToken);

    public ValueTask<AdministrativeAuditReceipt> AcknowledgeAsync(AuthorizationContext authorization, AlertAcknowledgeRequest request, CancellationToken cancellationToken) =>
        WriteAnyAsync(authorization, request.Audit, request.Audit.TargetId, request.IdempotencyKey, () => repository.AcknowledgeAsync(request, cancellationToken), cancellationToken);

    public ValueTask<AdministrativeAuditReceipt> UpsertDestinationAsync(AuthorizationContext authorization, AlertDestinationWriteRequest request, CancellationToken cancellationToken) =>
        UpsertDestinationCoreAsync(authorization, request, cancellationToken);

    private async ValueTask<AdministrativeAuditReceipt> UpsertDestinationCoreAsync(AuthorizationContext authorization, AlertDestinationWriteRequest request, CancellationToken cancellationToken)
    {
        return await WriteAsync(authorization, request.Audit, ApplicationRole.SecurityAdministrator, request.Audit.TargetId, request.IdempotencyKey, async () =>
        {
            if (request.Approve)
            {
                if (destinationApproval is null) throw new InvalidOperationException("Destination preflight is not configured.");
                AlertDestinationApproval approval = await destinationApproval.PreflightAsync(request, cancellationToken).ConfigureAwait(false);
                // Carry the server-derived immutable configuration fingerprint through
                // the repository command; callers never get to author this value.
                request = request with { Approval = approval, RequestDigest = approval.ConfigurationContentDigest ?? request.RequestDigest };
            }
            else if (request.Approval is not null)
            {
                throw new ArgumentException("Approval metadata is server-owned.", nameof(request));
            }
            return await repository.UpsertDestinationAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<AdministrativeAuditReceipt> CancelDeliveryAsync(AuthorizationContext authorization, AlertDeliveryAdminCancellationRequest request, CancellationToken cancellationToken) =>
        WriteAnyAsync(authorization, request.Audit, request.Audit.TargetId, request.IdempotencyKey, () => repository.CancelDeliveryAdminAsync(request, cancellationToken), cancellationToken);

    private async ValueTask<AdministrativeAuditReceipt> WriteAsync(AuthorizationContext authorization, AdministrativeAuditEnvelope envelope, ApplicationRole role, MonitoredInstanceId targetId, string operationId, Func<ValueTask<AdministrativeAuditReceipt>> write, CancellationToken cancellationToken, bool? allowedOverride = null)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(envelope);
        if (!Guid.TryParseExact(operationId, "D", out Guid parsedOperation) || parsedOperation == Guid.Empty || !string.Equals(operationId, parsedOperation.ToString("D"), StringComparison.Ordinal))
            throw new InvalidDataException("The operation identifier must be a canonical lowercase RFC4122 UUID.");
        bool allowed = allowedOverride ?? (authorization.IsActive && authorization.CanAccess(role, targetId));
        if (allowed)
        {
            try { return await write().ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is AlertRepositoryOperationException or AlertDestinationValidationException or InvalidDataException or InvalidOperationException or TimeoutException)
            {
                AlertDestinationValidationException? destinationException = exception as AlertDestinationValidationException;
                bool destinationValidation = destinationException is not null;
                bool targetMissing = exception is AlertRepositoryOperationException { Code: "target_not_found" };
                AdministrativeOperationOutcome outcome = exception is AlertRepositoryOperationException repositoryException && repositoryException.StatusCode == 409 || destinationException?.StatusCode == 409 || exception is InvalidOperationException && !destinationValidation
                    ? AdministrativeOperationOutcome.Conflict
                    : AdministrativeOperationOutcome.Failed;
                AdministrativeAuditReason failureReason = targetMissing
                    ? AdministrativeAuditReason.TargetNotFound
                    : exception is InvalidDataException || destinationValidation && !destinationException!.IsTransient
                    ? AdministrativeAuditReason.InvalidRequest
                    : outcome == AdministrativeOperationOutcome.Conflict ? AdministrativeAuditReason.RevisionConflict : AdministrativeAuditReason.RepositoryFailure;
                await audit.AppendAsync(new AppendAdministrativeAuditRequest(new AdministrativeAuditRecord(envelope, AdministrativeAuthorizationDecision.Granted, outcome, failureReason, parsedOperation, exception is AlertRepositoryOperationException repositoryError ? repositoryError.Code : exception.GetType().Name), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        AdministrativeAuditReason reason = !authorization.IsActive ? AdministrativeAuditReason.PrincipalDisabled : !authorization.CanAccess(targetId) ? AdministrativeAuditReason.TargetOutOfScope : !authorization.HasRole(role) ? AdministrativeAuditReason.RequiredRoleMissing : AdministrativeAuditReason.TargetOutOfScope;
        var record = new AdministrativeAuditRecord(envelope, AdministrativeAuthorizationDecision.Denied, AdministrativeOperationOutcome.Denied, reason, parsedOperation);
        await audit.AppendAsync(new AppendAdministrativeAuditRequest(record, new RepositoryCallTimeout(TimeSpan.FromSeconds(5))), cancellationToken).ConfigureAwait(false);
        throw new UnauthorizedAccessException("The requested M8 administrative operation is forbidden.");
    }

    private ValueTask<AdministrativeAuditReceipt> WriteAnyAsync(AuthorizationContext authorization, AdministrativeAuditEnvelope envelope, MonitoredInstanceId targetId, string operationId, Func<ValueTask<AdministrativeAuditReceipt>> write, CancellationToken cancellationToken)
    {
        bool allowed = authorization.IsActive && authorization.CanAccess(ApplicationRole.Operator, targetId) || authorization.IsActive && authorization.CanAccess(ApplicationRole.TargetAdministrator, targetId);
        return WriteAsync(authorization, envelope, ApplicationRole.Operator, targetId, operationId, write, cancellationToken, allowed);
    }
}
