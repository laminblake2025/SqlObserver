namespace SqlObserver.Domain.SensitiveData;

/// <summary>Protects content for one target; collection and retrieval must separately enforce capability, role, and audit policy.</summary>
public interface IQuerySensitiveContentProtector
{
    bool IsAvailable { get; }
    ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken cancellationToken);
}

public sealed class UnavailableQuerySensitiveContentProtector : IQuerySensitiveContentProtector
{
    public bool IsAvailable => false;
    public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<ProtectedSensitivePayload?>(null); }
    public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); throw new System.Security.Cryptography.CryptographicException("Query protection unavailable."); }
}
