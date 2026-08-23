using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Diagnostics;

public sealed record DiagnosticEventId
{
    public DiagnosticEventId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A diagnostic-event identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public sealed record DiagnosticEventKind
{
    public const int MaximumLength = 64;

    public DiagnosticEventKind(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character => DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Safe event metadata. Content-bearing data is represented only by an optional protected-payload reference.
/// </summary>
public sealed class DiagnosticEventEnvelope : IIngestionRecord
{
    public DiagnosticEventEnvelope(
        DiagnosticEventId eventId,
        MonitoredInstanceId instanceId,
        DiagnosticEventKind kind,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset collectedAtUtc,
        SensitivePayloadReference? protectedPayload = null)
    {
        ArgumentNullException.ThrowIfNull(eventId);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(kind);

        EventId = eventId;
        InstanceId = instanceId;
        Kind = kind;
        OccurredAtUtc = DomainValidation.RequireUtcMicrosecondAligned(
            occurredAtUtc,
            nameof(occurredAtUtc));
        CollectedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(
            collectedAtUtc,
            nameof(collectedAtUtc));
        ProtectedPayload = protectedPayload;
        EstimatedSizeBytes = checked(80 + DomainValidation.GetUtf8ByteCount(kind.Value));
    }

    public DiagnosticEventId EventId { get; }

    public MonitoredInstanceId InstanceId { get; }

    public DiagnosticEventKind Kind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset CollectedAtUtc { get; }

    public SensitivePayloadReference? ProtectedPayload { get; }

    public int EstimatedSizeBytes { get; }
}
