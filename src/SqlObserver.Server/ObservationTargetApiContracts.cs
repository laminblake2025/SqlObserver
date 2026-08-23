using System.Text.Json.Serialization;

namespace SqlObserver.Server;

/// <summary>Strict, credential-free target registration body.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RegisterObservationTargetBody(
    [property: JsonRequired] Guid InstanceId,
    string InstanceKey,
    string DisplayName,
    string Host,
    string? NamedInstance,
    int? TcpPort,
    string? CertificateHostName);

/// <summary>Strict, revision-checked target update body.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateObservationTargetBody(
    long ExpectedRevision,
    string DisplayName,
    string Host,
    string? NamedInstance,
    int? TcpPort,
    string? CertificateHostName);

/// <summary>Strict revision precondition for lifecycle actions.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TargetRevisionBody(long ExpectedRevision);

/// <summary>Sanitized target projection returned to authorized callers.</summary>
public sealed record ObservationTargetResponse(
    Guid InstanceId,
    string InstanceKey,
    string DisplayName,
    string Host,
    string? NamedInstance,
    int? TcpPort,
    string? CertificateHostName,
    string AuthenticationMode,
    string EncryptionMode,
    string Lifecycle,
    string CapabilityStatus,
    IReadOnlyList<string> CapabilityReasons,
    long ConfigurationRevision,
    DateTimeOffset DiscoveryRequestedAtUtc,
    DateTimeOffset? LastDiscoveryAtUtc);

/// <summary>Bounded cursor page.</summary>
public sealed record ObservationTargetPageResponse(
    IReadOnlyList<ObservationTargetResponse> Items,
    string? NextCursor);

/// <summary>Safe API error envelope that cannot carry provider exception details.</summary>
public sealed record SqlObserverProblemResponse(
    string Code,
    string Message,
    string CorrelationId);
