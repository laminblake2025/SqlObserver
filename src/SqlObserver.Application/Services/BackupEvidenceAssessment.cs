using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

/// <summary>Shared trust-aware assessment for detail reads and fleet materialization.</summary>
internal static class BackupEvidenceAssessment
{
    internal static BackupDatabaseAssessment Assess(BackupDatabaseEvidence item, DateTimeOffset atUtc)
    {
        BackupFreshnessPolicy? policy = item.Policy;
        BackupFreshnessEvidence? original = item.Evidence.FreshnessEvidence;
        if (policy is null || original is null)
        {
            var check = new BackupFreshnessCheck(BackupFreshnessStatus.Indeterminate, original is null
                ? BackupFreshnessReason.DatabaseIdentityUnknown : BackupFreshnessReason.PolicyNotConfigured, null, null, null);
            var damage = item.Evidence.Samples.Where(s => s.Freshness.IsDamaged == true)
                .Select(s => new BackupFreshnessIssue(s.Freshness.Kind, s.Freshness.CopyOnly, BackupFreshnessReason.BackupDamaged)).ToArray();
            return new(item, null, check, check, Array.AsReadOnly(damage));
        }
        BackupFreshnessSample[] safe = original.Samples.Select((sample, index) => sample.FinishUtc.HasValue && !item.QualificationTrusted[index]
            ? new BackupFreshnessSample(sample.Kind, sample.CopyOnly, null, sample.SourceLocalFinish,
                sample.HasChecksum, sample.IsDamaged, sample.MetadataComplete) : sample).ToArray();
        var evidence = new BackupFreshnessEvidence(original.TargetId, original.TargetRevision, original.DatabaseFingerprint,
            original.RunId, original.ObservedAtUtc, original.RecoveryModel, original.Scope, original.Coverage, original.LocalReplicaPreferred, safe);
        BackupFreshnessAssessment assessed = BackupFreshnessEvaluator.Assess(policy, evidence, atUtc);
        BackupFreshnessCheck RestoreReason(BackupFreshnessCheck check, BackupKind kind) =>
            check.Reason == BackupFreshnessReason.FinishTimeUnknown && item.Evidence.Samples.Where((s, i) =>
                s.Freshness.Kind == kind && (kind == BackupKind.Full && policy.FullRequirement == FullBackupRequirement.FullImage || !s.Freshness.CopyOnly) &&
                s.Freshness.FinishUtc.HasValue && !item.QualificationTrusted[i]).Any()
                ? check with { Reason = BackupFreshnessReason.TimestampQualificationUnavailable } : check;
        return new(item, assessed.PolicyRevision, RestoreReason(assessed.Full, BackupKind.Full),
            RestoreReason(assessed.Log, BackupKind.Log), assessed.ObservedIssues);
    }
}
