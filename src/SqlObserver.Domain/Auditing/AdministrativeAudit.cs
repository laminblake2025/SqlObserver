using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Auditing;

/// <summary>A normalized Windows SID; display names and tokens are deliberately excluded.</summary>
public sealed record ActorSecurityIdentifier
{
    public const int MaximumLength = 184;

    public ActorSecurityIdentifier(string value)
    {
        value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character => DomainValidation.IsAsciiDigit(character) || character is 'S' or '-',
            requireLeadingLetter: false);

        string[] segments = value.Split('-', StringSplitOptions.None);
        if (segments.Length < 4 || segments[0] != "S" || segments[1] != "1" ||
            segments.Skip(2).Any(static segment =>
                segment.Length == 0 || segment.Any(static character => !DomainValidation.IsAsciiDigit(character))))
        {
            throw new ArgumentException("The actor identifier must be a normalized Windows SID.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record AuditCorrelationId
{
    public AuditCorrelationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An audit correlation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public sealed record AdministrativeAuditId
{
    public AdministrativeAuditId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An administrative audit identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public enum AdministrativeAuditAction
{
    RegisterObservationTarget = 1,
    UpdateObservationTarget = 2,
    RetireObservationTarget = 3,
    RequestCapabilityRediscovery = 4,
    RecordCapabilityProfile = 5,
}

public enum AdministrativeAuthorizationDecision
{
    Granted = 1,
    Denied = 2,
}

public enum AdministrativeOperationOutcome
{
    Succeeded = 1,
    Denied = 2,
    Failed = 3,
    Conflict = 4,
}

public enum AdministrativeAuditReason
{
    Completed = 1,
    PrincipalDisabled = 2,
    RequiredRoleMissing = 3,
    TargetOutOfScope = 4,
    AlreadyExists = 5,
    RevisionConflict = 6,
    TargetNotFound = 7,
    TargetRetired = 8,
    DiscoveryFailed = 9,
    RepositoryFailure = 10,
}

/// <summary>Safe context carried into an atomic repository mutation.</summary>
public sealed class AdministrativeAuditEnvelope
{
    public AdministrativeAuditEnvelope(
        ActorSecurityIdentifier actorSid,
        AuditCorrelationId correlationId,
        AdministrativeAuditAction action,
        MonitoredInstanceId targetId)
    {
        ArgumentNullException.ThrowIfNull(actorSid);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(targetId);

        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        ActorSid = actorSid;
        CorrelationId = correlationId;
        Action = action;
        TargetId = targetId;
    }

    public ActorSecurityIdentifier ActorSid { get; }

    public AuditCorrelationId CorrelationId { get; }

    public AdministrativeAuditAction Action { get; }

    public MonitoredInstanceId TargetId { get; }
}

/// <summary>A non-mutating denial or failure that still requires append-only audit.</summary>
public sealed class AdministrativeAuditRecord
{
    public AdministrativeAuditRecord(
        AdministrativeAuditEnvelope envelope,
        AdministrativeAuthorizationDecision authorizationDecision,
        AdministrativeOperationOutcome outcome,
        AdministrativeAuditReason reason)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!Enum.IsDefined(authorizationDecision))
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationDecision));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if ((authorizationDecision == AdministrativeAuthorizationDecision.Denied) !=
            (outcome == AdministrativeOperationOutcome.Denied))
        {
            throw new ArgumentException("A denied authorization decision must have a denied outcome, and vice versa.");
        }

        Envelope = envelope;
        AuthorizationDecision = authorizationDecision;
        Outcome = outcome;
        Reason = reason;
    }

    public AdministrativeAuditEnvelope Envelope { get; }

    public AdministrativeAuthorizationDecision AuthorizationDecision { get; }

    public AdministrativeOperationOutcome Outcome { get; }

    public AdministrativeAuditReason Reason { get; }
}

public sealed class AdministrativeAuditReceipt
{
    public AdministrativeAuditReceipt(AdministrativeAuditId auditId, DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(auditId);
        AuditId = auditId;
        RecordedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(recordedAtUtc, nameof(recordedAtUtc));
    }

    public AdministrativeAuditId AuditId { get; }

    public DateTimeOffset RecordedAtUtc { get; }
}
