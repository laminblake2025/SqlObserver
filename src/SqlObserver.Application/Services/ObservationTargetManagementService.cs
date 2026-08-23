using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Services;

public sealed class UpdateObservationTargetCommand
{
    public UpdateObservationTargetCommand(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        ObservationTargetDisplayName displayName,
        SqlServerConnectionPolicy connectionPolicy,
        AuditCorrelationId correlationId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(connectionPolicy);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        DisplayName = displayName;
        ConnectionPolicy = connectionPolicy;
        CorrelationId = correlationId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public ObservationTargetDisplayName DisplayName { get; }

    public SqlServerConnectionPolicy ConnectionPolicy { get; }

    public AuditCorrelationId CorrelationId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RetireObservationTargetCommand
{
    public RetireObservationTargetCommand(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        AuditCorrelationId correlationId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        CorrelationId = correlationId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public AuditCorrelationId CorrelationId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public sealed class RequestCapabilityRediscoveryCommand
{
    public RequestCapabilityRediscoveryCommand(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision,
        AuditCorrelationId correlationId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        TargetId = targetId;
        ExpectedRevision = expectedRevision;
        CorrelationId = correlationId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public MonitoredInstanceId TargetId { get; }

    public ObservationTargetRevision ExpectedRevision { get; }

    public AuditCorrelationId CorrelationId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public enum ObservationTargetManagementStatus
{
    Applied = 1,
    Denied = 2,
    NotFound = 3,
    RevisionConflict = 4,
    AlreadyRetired = 5,
}

public sealed class ObservationTargetManagementResult
{
    public ObservationTargetManagementResult(
        ObservationTargetManagementStatus status,
        ObservationTarget? target,
        AdministrativeAuditReason reason)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        bool targetRequired = status is
            ObservationTargetManagementStatus.Applied or
            ObservationTargetManagementStatus.AlreadyRetired;

        if (targetRequired != (target is not null))
        {
            throw new ArgumentException("Applied or already-retired management requires a target snapshot.", nameof(target));
        }

        Status = status;
        Target = target;
        Reason = reason;
    }

    public ObservationTargetManagementStatus Status { get; }

    public ObservationTarget? Target { get; }

    public AdministrativeAuditReason Reason { get; }
}

public interface IObservationTargetManagementService
{
    ValueTask<ObservationTargetManagementResult> UpdateAsync(
        UpdateObservationTargetCommand command,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetManagementResult> RetireAsync(
        RetireObservationTargetCommand command,
        CancellationToken cancellationToken);

    ValueTask<ObservationTargetManagementResult> RequestRediscoveryAsync(
        RequestCapabilityRediscoveryCommand command,
        CancellationToken cancellationToken);
}

public sealed class ObservationTargetManagementService : IObservationTargetManagementService
{
    private readonly IObservationTargetRepositoryPort _targets;
    private readonly IAdministrativeAuditPort _audit;

    public ObservationTargetManagementService(
        IObservationTargetRepositoryPort targets,
        IAdministrativeAuditPort audit)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ValueTask<ObservationTargetManagementResult> UpdateAsync(
        UpdateObservationTargetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.Authorization,
            command.TargetId,
            command.CorrelationId,
            AdministrativeAuditAction.UpdateObservationTarget,
            command.Timeout,
            audit => _targets.UpdateAsync(
                new UpdateObservationTargetRequest(
                    command.TargetId,
                    command.ExpectedRevision,
                    command.DisplayName,
                    command.ConnectionPolicy,
                    audit,
                    command.Timeout),
                cancellationToken),
            cancellationToken);
    }

    public ValueTask<ObservationTargetManagementResult> RetireAsync(
        RetireObservationTargetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.Authorization,
            command.TargetId,
            command.CorrelationId,
            AdministrativeAuditAction.RetireObservationTarget,
            command.Timeout,
            audit => _targets.RetireAsync(
                new RetireObservationTargetRequest(
                    command.TargetId,
                    command.ExpectedRevision,
                    audit,
                    command.Timeout),
                cancellationToken),
            cancellationToken);
    }

    public ValueTask<ObservationTargetManagementResult> RequestRediscoveryAsync(
        RequestCapabilityRediscoveryCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.Authorization,
            command.TargetId,
            command.CorrelationId,
            AdministrativeAuditAction.RequestCapabilityRediscovery,
            command.Timeout,
            audit => _targets.RequestRediscoveryAsync(
                new RequestCapabilityRediscoveryRequest(
                    command.TargetId,
                    command.ExpectedRevision,
                    audit,
                    command.Timeout),
                cancellationToken),
            cancellationToken);
    }

    private async ValueTask<ObservationTargetManagementResult> ExecuteAsync(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        AuditCorrelationId correlationId,
        AdministrativeAuditAction action,
        RepositoryCallTimeout timeout,
        Func<AdministrativeAuditEnvelope, ValueTask<ObservationTargetMutationResult>> mutate,
        CancellationToken cancellationToken)
    {
        var envelope = new AdministrativeAuditEnvelope(
            authorization.ActorSid,
            correlationId,
            action,
            targetId);
        AdministrativeAuditReason? denial = GetDenialReason(authorization, targetId);

        if (denial is not null)
        {
            using var auditCancellation = new CancellationTokenSource(timeout.Value);
            await _audit.AppendAsync(
                    new AppendAdministrativeAuditRequest(
                        new AdministrativeAuditRecord(
                            envelope,
                            AdministrativeAuthorizationDecision.Denied,
                            AdministrativeOperationOutcome.Denied,
                            denial.Value),
                        timeout),
                    auditCancellation.Token)
                .ConfigureAwait(false);
            return new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.Denied,
                target: null,
                denial.Value);
        }

        ObservationTargetMutationResult result = await mutate(envelope).ConfigureAwait(false);
        return result.Status switch
        {
            ObservationTargetMutationStatus.Applied => new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.Applied,
                result.Target,
                AdministrativeAuditReason.Completed),
            ObservationTargetMutationStatus.NotFound => new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.NotFound,
                target: null,
                AdministrativeAuditReason.TargetNotFound),
            ObservationTargetMutationStatus.RevisionConflict => new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.RevisionConflict,
                target: null,
                AdministrativeAuditReason.RevisionConflict),
            ObservationTargetMutationStatus.AlreadyRetired => new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.AlreadyRetired,
                result.Target,
                AdministrativeAuditReason.TargetRetired),
            _ => throw new InvalidOperationException("The target repository returned an unknown mutation status."),
        };
    }

    private static AdministrativeAuditReason? GetDenialReason(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId)
    {
        if (!authorization.IsActive)
        {
            return AdministrativeAuditReason.PrincipalDisabled;
        }

        if (!authorization.HasRole(ApplicationRole.TargetAdministrator))
        {
            return AdministrativeAuditReason.RequiredRoleMissing;
        }

        return authorization.CanAccess(ApplicationRole.TargetAdministrator, targetId)
            ? null
            : AdministrativeAuditReason.TargetOutOfScope;
    }
}
