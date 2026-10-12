using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record SetCpuComponentsCollectionRequest(
    MonitoredInstanceId TargetId, long ExpectedTargetRevision,
    bool ExpectedEnabled, bool Enabled,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public enum CpuComponentsCollectionMutationStatus
{
    Applied,
    NotFound,
    Inactive,
    RevisionConflict,
}

public sealed record CpuComponentsCollectionMutationResult(
    CpuComponentsCollectionMutationStatus Status, long? ScheduleRevision,
    bool? Enabled);

public sealed record CpuComponentsCollectionState(long TargetRevision, long ScheduleRevision, bool Enabled);

public interface ICpuComponentsCollectionAdministrationPort
{
    ValueTask<CpuComponentsCollectionState?> GetAsync(MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<CpuComponentsCollectionMutationResult> SetAsync(
        SetCpuComponentsCollectionRequest request, CancellationToken cancellationToken);
}
