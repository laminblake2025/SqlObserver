using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record QueryPlanReadRequest(
    MonitoredInstanceId TargetId,
    Guid CollectionRunId,
    PlanOpaqueIdentity Plan,
    RepositoryCallTimeout Timeout);

public sealed record QueryPlanReadResult(string Status, string? Xml);

public interface IQueryPlanReadRepositoryPort
{
    ValueTask<ProtectedSensitivePayload?> ReadAsync(QueryPlanReadRequest request, CancellationToken cancellationToken);
    ValueTask AuditAsync(QueryPlanReadRequest request, string actorSid, string outcome, CancellationToken cancellationToken);
}

public interface IQueryPlanReadService
{
    ValueTask<QueryPlanReadResult> ReadAsync(AuthorizationContext authorization, QueryPlanReadRequest request, CancellationToken cancellationToken);
}
