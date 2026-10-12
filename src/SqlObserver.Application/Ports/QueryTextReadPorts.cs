using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record QueryTextReadRequest(
    MonitoredInstanceId TargetId,
    Guid CollectionRunId,
    QueryOpaqueIdentity Query,
    RepositoryCallTimeout Timeout);

public sealed record QueryTextReadResult(string Status, string? Text);

public interface IQueryTextReadRepositoryPort
{
    ValueTask<ProtectedSensitivePayload?> ReadAsync(QueryTextReadRequest request, CancellationToken cancellationToken);
    ValueTask AuditAsync(QueryTextReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken);
}

public interface IQueryTextReadService
{
    ValueTask<QueryTextReadResult> ReadAsync(AuthorizationContext authorization, QueryTextReadRequest request, CancellationToken cancellationToken);
}
