using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record OperationalHealthRequest(MonitoredInstanceId TargetId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, int Limit, string? Cursor, RepositoryCallTimeout Timeout);

public interface IOperationalHealthRepositoryPort
{
    ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest request, CancellationToken cancellationToken);
}

public interface IOperationalHealthQueryService
{
    ValueTask<BackupStatusSnapshot?> GetBackupsAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbSnapshot?> GetTempDbAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
    ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken);
}
