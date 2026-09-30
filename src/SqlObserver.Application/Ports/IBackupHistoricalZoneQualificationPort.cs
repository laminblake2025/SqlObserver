using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

/// <summary>Returns explicitly qualified historical rules for the exact target revision; absence is not inferred from the current zone.</summary>
public interface IBackupHistoricalZoneQualificationPort
{
    ValueTask<BackupHistoricalZoneQualification?> GetAsync(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, CancellationToken cancellationToken);
}
