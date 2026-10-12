using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record SetServerLifecycleCollectionRequest(
    MonitoredInstanceId TargetId, long ExpectedTargetRevision,
    long ExpectedScheduleRevision, bool ExpectedEnabled, bool Enabled,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public enum ServerLifecycleCollectionMutationStatus
{
    Applied,
    NotFound,
    Inactive,
    RevisionConflict,
}

public sealed record ServerLifecycleCollectionMutationResult(
    ServerLifecycleCollectionMutationStatus Status, long? ScheduleRevision,
    bool? Enabled);

public sealed record ServerLifecycleCollectionState(long TargetRevision, long ScheduleRevision, bool Enabled);

public interface IServerLifecycleCollectionAdministrationPort
{
    ValueTask<ServerLifecycleCollectionState?> GetAsync(MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<ServerLifecycleCollectionMutationResult> SetAsync(
        SetServerLifecycleCollectionRequest request, CancellationToken cancellationToken);
}

public sealed class ServerLifecycleCollectionException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
