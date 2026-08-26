using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Auditing;

/// <summary>The terminal state of one read-only MCP invocation.</summary>
public enum McpInvocationOutcome
{
    Succeeded = 1,
    Denied = 2,
    Invalid = 3,
    UnknownTool = 4,
    Timeout = 5,
    Cancelled = 6,
    Limited = 7,
    Oversize = 8,
    RepositoryFailure = 9,
}

public enum McpAuthorizationResult
{
    Allowed = 1,
    Denied = 2,
    NotApplicable = 3,
}

public enum McpInvocationAuditReason
{
    Completed = 1,
    AuthorizationDenied = 2,
    InvalidRequest = 3,
    UnknownTool = 4,
    Timeout = 5,
    Cancelled = 6,
    ConcurrencyLimit = 7,
    ResponseOversize = 8,
    RepositoryFailure = 9,
}

public sealed record McpClientIdentifier
{
    public const int MaximumLength = 512;

    public McpClientIdentifier(string value)
    {
        Value = DomainValidation.RequireSafeText(value, nameof(value), MaximumLength);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A canonical, allowlisted MCP tool identifier; protocol aliases are not accepted.</summary>
public sealed record McpToolName
{
    public const int MaximumLength = 128;

    public McpToolName(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static c => DomainValidation.IsAsciiLetter(c) || DomainValidation.IsAsciiDigit(c) || c is '.' or '_' or '-',
            requireLeadingLetter: true).ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A canonical action name, separate from the transport-level tool name.</summary>
public sealed record McpActionName
{
    public const int MaximumLength = 128;

    public McpActionName(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static c => DomainValidation.IsAsciiLetter(c) || DomainValidation.IsAsciiDigit(c) || c is '.' or '_' or '-',
            requireLeadingLetter: true).ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed class McpParameterDigest
{
    public const int Length = 32;

    public McpParameterDigest(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length)
        {
            throw new ArgumentException("An MCP parameter digest must contain exactly 32 bytes.", nameof(value));
        }

        _value = value.ToArray();
    }

    private readonly byte[] _value;

    public byte[] Value => _value.ToArray();

    public override string ToString() => Convert.ToHexString(_value).ToLowerInvariant();
}

/// <summary>Canonical JSON hashing used for MCP audit metadata; raw parameters never leave this process.</summary>
public static class McpParameterCanonicalizer
{
    public static McpParameterDigest Digest(JsonElement parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            WriteCanonical(parameters, writer);
        }

        return new McpParameterDigest(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("MCP parameters must be valid JSON.", nameof(value));
        }
    }
}

/// <summary>Immutable safe identity and terminal metadata for one MCP invocation.</summary>
public sealed class McpInvocationAuditRecord
{
    public McpInvocationAuditRecord(
        Guid invocationId,
        McpClientIdentifier actor,
        McpToolName tool,
        McpActionName action,
        McpAuthorizationResult authorization,
        McpInvocationOutcome outcome,
        McpInvocationAuditReason reason,
        AuditCorrelationId correlationId,
        McpParameterDigest parameterDigest,
        TimeSpan duration,
        long responseBytes,
        MonitoredInstanceId? targetId = null,
        Guid? incidentId = null,
        string? safeDetail = null)
    {
        if (invocationId == Guid.Empty) throw new ArgumentException("An invocation identifier cannot be empty.", nameof(invocationId));
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(parameterDigest);
        if (!Enum.IsDefined(authorization)) throw new ArgumentOutOfRangeException(nameof(authorization));
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        ArgumentOutOfRangeException.ThrowIfNegative(responseBytes);
        if (incidentId == Guid.Empty) throw new ArgumentException("An incident identifier cannot be empty.", nameof(incidentId));
        if (safeDetail is not null && (safeDetail.Length > 128 || safeDetail.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')))
            throw new ArgumentException("The audit detail must be a bounded safe code.", nameof(safeDetail));
        if ((outcome == McpInvocationOutcome.Denied) != (authorization == McpAuthorizationResult.Denied))
            throw new ArgumentException("Denied outcomes must have denied authorization, and vice versa.");
        if (outcome == McpInvocationOutcome.Succeeded && authorization != McpAuthorizationResult.Allowed)
            throw new ArgumentException("A successful invocation must have allowed authorization.");

        InvocationId = invocationId;
        _actorKind = "mcp_client";
        Actor = actor;
        Tool = tool;
        Action = action;
        Authorization = authorization;
        Outcome = outcome;
        Reason = reason;
        CorrelationId = correlationId;
        ParameterDigest = parameterDigest;
        Duration = duration;
        ResponseBytes = responseBytes;
        TargetId = targetId;
        IncidentId = incidentId;
        SafeDetail = safeDetail ?? string.Empty;
    }

    public Guid InvocationId { get; }
    public McpClientIdentifier Actor { get; }
    private readonly string _actorKind;
    public string ActorKind => _actorKind;
    public McpToolName Tool { get; }
    public McpActionName Action { get; }
    public McpAuthorizationResult Authorization { get; }
    public McpInvocationOutcome Outcome { get; }
    public McpInvocationAuditReason Reason { get; }
    public AuditCorrelationId CorrelationId { get; }
    public McpParameterDigest ParameterDigest { get; }
    public TimeSpan Duration { get; }
    public long ResponseBytes { get; }
    public MonitoredInstanceId? TargetId { get; }
    public Guid? IncidentId { get; }
    public string SafeDetail { get; }
}

public sealed class McpInvocationAuditReceipt
{
    public McpInvocationAuditReceipt(Guid invocationId, DateTimeOffset recordedAtUtc)
    {
        if (invocationId == Guid.Empty) throw new ArgumentException("An invocation identifier cannot be empty.", nameof(invocationId));
        InvocationId = invocationId;
        RecordedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(recordedAtUtc, nameof(recordedAtUtc));
    }

    public Guid InvocationId { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}
