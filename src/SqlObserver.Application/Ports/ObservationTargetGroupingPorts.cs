using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record ObservationTargetGrouping(Guid TargetId, string? Environment, IReadOnlyList<string> Tags, long Revision);

public enum ObservationTargetGroupingMutationStatus
{
    Applied,
    NotFound,
    RevisionConflict,
    AlreadyRetired,
}

public sealed record ObservationTargetGroupingMutationResult(
    ObservationTargetGroupingMutationStatus Status, long? Revision);

public sealed record SetObservationTargetGroupingRequest(
    MonitoredInstanceId TargetId, long ExpectedRevision, string? Environment,
    IReadOnlyList<string> Tags, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public interface IObservationTargetGroupingPort
{
    Task<IReadOnlyDictionary<Guid, ObservationTargetGrouping>> ReadAsync(
        IReadOnlyList<Guid> targetIds, RepositoryCallTimeout timeout, CancellationToken cancellationToken);

    Task<ObservationTargetGroupingMutationResult> SetAsync(
        SetObservationTargetGroupingRequest request, CancellationToken cancellationToken);
}
