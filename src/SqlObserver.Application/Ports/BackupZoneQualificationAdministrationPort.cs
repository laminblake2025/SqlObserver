using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public sealed record BackupZoneQualificationSnapshot(long Revision,
    BackupHistoricalZoneQualification? Qualification, string? EvidenceSha256, string? RulesSha256 = null);

public enum BackupZoneQualificationMutationStatus { Applied, Replayed, NotFound, Inactive, RevisionConflict }
public sealed record BackupZoneQualificationMutationResult(BackupZoneQualificationMutationStatus Status, long? Revision);
public sealed record SetBackupZoneQualificationRequest(BackupZoneQualificationChange Change,
    AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);

public interface IBackupZoneQualificationAdministrationPort
{
    ValueTask<BackupZoneQualificationMutationResult> SetAsync(SetBackupZoneQualificationRequest request,
        CancellationToken cancellationToken);
    ValueTask<BackupZoneQualificationSnapshot?> ReadAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision revision, CancellationToken cancellationToken);
}
