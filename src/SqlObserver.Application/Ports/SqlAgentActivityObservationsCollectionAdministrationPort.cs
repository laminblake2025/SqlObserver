using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record SetSqlAgentActivityObservationsCollectionRequest(
    MonitoredInstanceId TargetId, long ExpectedTargetRevision,
    long ExpectedScheduleRevision, bool ExpectedEnabled, bool Enabled,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public enum SqlAgentActivityObservationsCollectionMutationStatus
{
    Applied,
    NotFound,
    Inactive,
    RevisionConflict,
    EnrollmentUnavailable,
}

public sealed record SqlAgentActivityObservationsCollectionMutationResult(
    SqlAgentActivityObservationsCollectionMutationStatus Status, long? ScheduleRevision,
    bool? Enabled);

public sealed record SqlAgentActivityObservationsCollectionState(long TargetRevision, long ScheduleRevision, bool Enabled);

public interface ISqlAgentActivityObservationsCollectionAdministrationPort
{
    ValueTask<SqlAgentActivityObservationsCollectionState?> GetAsync(MonitoredInstanceId targetId, long targetRevision, RepositoryCallTimeout timeout, CancellationToken cancellationToken);
    ValueTask<SqlAgentActivityObservationsCollectionMutationResult> SetAsync(
        SetSqlAgentActivityObservationsCollectionRequest request, CancellationToken cancellationToken);
}

public sealed class SqlAgentActivityObservationsCollectionException(string code, string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class SqlAgentActivityObservationsCollectionValidation
{
    public const long MaximumRevision = 9007199254740991;
    public static void Revision(long revision)
    {
        if (revision is < 1 or > MaximumRevision) throw new ArgumentOutOfRangeException(nameof(revision));
    }
}
