using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public interface ISqlAgentModuleEnrollmentResolver
{
    ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken);
}

public sealed record SetSqlAgentModuleEnrollmentRequest(SqlAgentModuleEnrollmentChange Change,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public enum SqlAgentModuleEnrollmentMutationStatus { Applied, Replayed, NotFound, Inactive, RevisionConflict }
public sealed record SqlAgentModuleEnrollmentMutationResult(SqlAgentModuleEnrollmentMutationStatus Status, long? Revision);

public enum SqlAgentModuleEnrollmentReadStatus { Found, NotFound, Inactive, RevisionConflict }
public sealed record SqlAgentModuleEnrollmentReadResult(SqlAgentModuleEnrollmentReadStatus Status, SqlAgentModuleEnrollmentSnapshot? Snapshot);

public interface ISqlAgentModuleEnrollmentAdministrationPort
{
    ValueTask<SqlAgentModuleEnrollmentMutationResult> SetAsync(SetSqlAgentModuleEnrollmentRequest request,
        CancellationToken cancellationToken);
    ValueTask<SqlAgentModuleEnrollmentReadResult> ReadAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken);
}
