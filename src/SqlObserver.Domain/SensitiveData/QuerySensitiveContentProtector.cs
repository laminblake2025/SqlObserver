namespace SqlObserver.Domain.SensitiveData;

/// <summary>Explicit M7 capability boundary. Production composition is fail-closed until M12 key provisioning.</summary>
public interface IQuerySensitiveContentProtector
{
    bool IsAvailable { get; }
    ValueTask<ProtectedSensitivePayload?> ProtectAsync(SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed class UnavailableQuerySensitiveContentProtector : IQuerySensitiveContentProtector
{
    public bool IsAvailable => false;
    public ValueTask<ProtectedSensitivePayload?> ProtectAsync(SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<ProtectedSensitivePayload?>(null); }
}
