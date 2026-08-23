using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Application.Services;

public sealed class OnboardObservationTargetCommand
{
    public OnboardObservationTargetCommand(
        AuthorizationContext authorization,
        ObservationTargetRegistration registration,
        AuditCorrelationId correlationId,
        RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(timeout);
        Authorization = authorization;
        Registration = registration;
        CorrelationId = correlationId;
        Timeout = timeout;
    }

    public AuthorizationContext Authorization { get; }

    public ObservationTargetRegistration Registration { get; }

    public AuditCorrelationId CorrelationId { get; }

    public RepositoryCallTimeout Timeout { get; }
}

public enum ObservationTargetOnboardingStatus
{
    RegisteredPendingDiscovery = 1,
    AlreadyExists = 2,
    Denied = 3,
    Conflict = 4,
}

public sealed class ObservationTargetOnboardingResult
{
    public ObservationTargetOnboardingResult(
        ObservationTargetOnboardingStatus status,
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
            ObservationTargetOnboardingStatus.RegisteredPendingDiscovery or
            ObservationTargetOnboardingStatus.AlreadyExists;

        if (targetRequired != (target is not null))
        {
            throw new ArgumentException("Successful onboarding or exact replay requires a target snapshot.", nameof(target));
        }

        Status = status;
        Target = target;
        Reason = reason;
    }

    public ObservationTargetOnboardingStatus Status { get; }

    public ObservationTarget? Target { get; }

    public AdministrativeAuditReason Reason { get; }
}

public interface IObservationTargetOnboardingService
{
    ValueTask<ObservationTargetOnboardingResult> OnboardAsync(
        OnboardObservationTargetCommand command,
        CancellationToken cancellationToken);
}

/// <summary>Deny-by-default target registration with mandatory denial and atomic mutation audit.</summary>
public sealed class ObservationTargetOnboardingService : IObservationTargetOnboardingService
{
    private readonly IObservationTargetRepositoryPort _targets;
    private readonly IAdministrativeAuditPort _audit;

    public ObservationTargetOnboardingService(
        IObservationTargetRepositoryPort targets,
        IAdministrativeAuditPort audit)
    {
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async ValueTask<ObservationTargetOnboardingResult> OnboardAsync(
        OnboardObservationTargetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var envelope = new AdministrativeAuditEnvelope(
            command.Authorization.ActorSid,
            command.CorrelationId,
            AdministrativeAuditAction.RegisterObservationTarget,
            command.Registration.TargetId);
        AdministrativeAuditReason? denialReason = GetDenialReason(command.Authorization);

        if (denialReason is not null)
        {
            var denial = new AdministrativeAuditRecord(
                envelope,
                AdministrativeAuthorizationDecision.Denied,
                AdministrativeOperationOutcome.Denied,
                denialReason.Value);
            using var auditCancellation = new CancellationTokenSource(command.Timeout.Value);
            await _audit.AppendAsync(
                    new AppendAdministrativeAuditRequest(denial, command.Timeout),
                    auditCancellation.Token)
                .ConfigureAwait(false);
            return new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.Denied,
                target: null,
                denialReason.Value);
        }

        ObservationTargetRegistrationResult registration = await _targets.RegisterAsync(
                new RegisterObservationTargetRequest(
                    command.Registration,
                    envelope,
                    command.Timeout),
                cancellationToken)
            .ConfigureAwait(false);

        return registration.Status switch
        {
            ObservationTargetRegistrationStatus.Registered => new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.RegisteredPendingDiscovery,
                registration.Target,
                AdministrativeAuditReason.Completed),
            ObservationTargetRegistrationStatus.AlreadyExists => new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.AlreadyExists,
                registration.Target,
                AdministrativeAuditReason.AlreadyExists),
            ObservationTargetRegistrationStatus.TargetIdConflict or
            ObservationTargetRegistrationStatus.TargetKeyConflict => new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.Conflict,
                target: null,
                AdministrativeAuditReason.AlreadyExists),
            _ => throw new InvalidOperationException("The target repository returned an unknown registration status."),
        };
    }

    private static AdministrativeAuditReason? GetDenialReason(AuthorizationContext authorization)
    {
        if (!authorization.IsActive)
        {
            return AdministrativeAuditReason.PrincipalDisabled;
        }

        if (!authorization.HasRole(ApplicationRole.TargetAdministrator))
        {
            return AdministrativeAuditReason.RequiredRoleMissing;
        }

        return authorization.HasRoleForAllTargets(ApplicationRole.TargetAdministrator)
            ? null
            : AdministrativeAuditReason.TargetOutOfScope;
    }
}
