using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record SetConfigurationCollectionRequest(
    MonitoredInstanceId TargetId, long ExpectedTargetRevision,
    bool ExpectedEnabled, bool Enabled,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public enum ConfigurationCollectionMutationStatus
{
    Applied,
    NotFound,
    Inactive,
    RevisionConflict,
}

public sealed record ConfigurationCollectionMutationResult(
    ConfigurationCollectionMutationStatus Status, long? ScheduleRevision,
    bool? Enabled);

public interface IConfigurationCollectionAdministrationPort
{
    ValueTask<ConfigurationCollectionMutationResult> SetAsync(
        SetConfigurationCollectionRequest request, CancellationToken cancellationToken);
}
