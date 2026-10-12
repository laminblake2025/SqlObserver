using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Application.Ports;

public enum BackupPolicyMutationStatus
{ Applied, Replayed, NotFound, Inactive, RevisionConflict, EvidenceUnavailable, DatabaseNotObserved, LogThresholdRequired }
public sealed record BackupPolicyMutationResult(BackupPolicyMutationStatus Status, long? Revision);
public sealed record SetBackupFreshnessPolicyRequest(BackupFreshnessPolicy Policy, long ExpectedPolicyRevision,
    Guid OperationId, AdministrativeAuditEnvelope Audit, RepositoryCallTimeout Timeout);
public sealed record ReadBackupFreshnessPolicyRequest
{
    public ReadBackupFreshnessPolicyRequest(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision,
        string databaseFingerprint, DateTimeOffset atUtc, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentNullException.ThrowIfNull(targetRevision);
        ArgumentNullException.ThrowIfNull(timeout);
        if (databaseFingerprint is not { Length: 64 } || databaseFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase opaque database fingerprint is required.", nameof(databaseFingerprint));
        if (atUtc.Offset != TimeSpan.Zero || atUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ArgumentException("The policy cutoff must be UTC and microsecond aligned.", nameof(atUtc));
        TargetId = targetId; TargetRevision = targetRevision; DatabaseFingerprint = databaseFingerprint;
        AtUtc = atUtc; Timeout = timeout;
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public string DatabaseFingerprint { get; }
    public DateTimeOffset AtUtc { get; }
    public RepositoryCallTimeout Timeout { get; }
}
public interface IBackupFreshnessPolicyPort
{
    ValueTask<BackupPolicyMutationResult> SetAsync(SetBackupFreshnessPolicyRequest request, CancellationToken cancellationToken);
    ValueTask<BackupFreshnessPolicy?> ReadAsync(ReadBackupFreshnessPolicyRequest request, CancellationToken cancellationToken);
}
