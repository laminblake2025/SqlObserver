using SqlObserver.Domain.Collection;

namespace SqlObserver.Domain.Backups;

/// <summary>Backup-age policy checks; these do not certify recovery points or restore chains.</summary>
public static class BackupFreshnessEvaluator
{
    public static BackupFreshnessAssessment Assess(BackupFreshnessPolicy policy,
        BackupFreshnessEvidence evidence, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evidence);
        DomainValidation.RequireUtcMicrosecondAligned(atUtc, nameof(atUtc));
        if (policy.TargetId != evidence.TargetId || policy.TargetRevision != evidence.TargetRevision
            || policy.DatabaseFingerprint != evidence.DatabaseFingerprint)
            throw new ArgumentException("Backup freshness policy and evidence identities differ.", nameof(evidence));

        // Preserve observed faults even when missing coverage prevents a fleet-level conclusion.
        var issues = new List<BackupFreshnessIssue>();
        foreach (BackupFreshnessSample sample in evidence.Samples)
        {
            if (sample.IsDamaged == true)
                issues.Add(new(sample.Kind, sample.CopyOnly, BackupFreshnessReason.BackupDamaged));
            if (policy.RequireChecksums && sample.HasChecksum == false)
                issues.Add(new(sample.Kind, sample.CopyOnly, BackupFreshnessReason.ChecksumMissing));
        }
        BackupFreshnessCheck full, log;
        BackupFreshnessReason? unavailable = CoverageReason();
        if (!policy.Enabled)
        {
            full = Disabled(policy.MaximumFullAge); log = Disabled(policy.MaximumLogAge);
        }
        else if (unavailable.HasValue)
        {
            full = Unknown(unavailable.Value, policy.MaximumFullAge);
            log = Unknown(unavailable.Value, policy.MaximumLogAge);
        }
        else
        {
            full = Check(evidence.Samples.Where(sample => sample.Kind == BackupKind.Full
                && (policy.FullRequirement == FullBackupRequirement.FullImage || !sample.CopyOnly)), policy.MaximumFullAge);
            log = evidence.RecoveryModel switch
            {
                BackupRecoveryModel.Simple => new(BackupFreshnessStatus.NotRequired, BackupFreshnessReason.SimpleRecovery, null, null, null),
                BackupRecoveryModel.Unknown => Unknown(BackupFreshnessReason.RecoveryModelUnknown, policy.MaximumLogAge),
                _ when !policy.MaximumLogAge.HasValue => Unknown(BackupFreshnessReason.LogThresholdNotConfigured, null),
                _ => Check(evidence.Samples.Where(sample => sample.Kind == BackupKind.Log && !sample.CopyOnly), policy.MaximumLogAge!.Value),
            };
        }
        return new(atUtc, policy.PolicyRevision, full, log, issues.AsReadOnly());

        BackupFreshnessReason? CoverageReason()
        {
            if (evidence.Coverage != BackupFreshnessCoverage.Complete)
                return evidence.Coverage switch
                {
                    BackupFreshnessCoverage.NotCollected => BackupFreshnessReason.NotCollected,
                    BackupFreshnessCoverage.Partial => BackupFreshnessReason.SourceIncomplete,
                    BackupFreshnessCoverage.Truncated => BackupFreshnessReason.SourceTruncated,
                    BackupFreshnessCoverage.PermissionDenied => BackupFreshnessReason.PermissionDenied,
                    BackupFreshnessCoverage.Failed => BackupFreshnessReason.CollectionFailed,
                    BackupFreshnessCoverage.Unsupported => BackupFreshnessReason.Unsupported,
                    _ => throw new ArgumentException("Backup freshness coverage is invalid.", nameof(evidence)),
                };
            if (evidence.ObservedAtUtc > atUtc) return BackupFreshnessReason.FutureSourceTime;
            if (atUtc - evidence.ObservedAtUtc > policy.MaximumObservationAge) return BackupFreshnessReason.StaleObservation;
            return evidence.Scope switch
            {
                BackupFreshnessScope.Unknown => BackupFreshnessReason.TopologyUnknown,
                BackupFreshnessScope.AvailabilityGroupLocalOnly => BackupFreshnessReason.OtherReplicaEvidenceRequired,
                _ => null,
            };
        }

        BackupFreshnessCheck Check(IEnumerable<BackupFreshnessSample> candidates, TimeSpan maximumAge)
        {
            BackupFreshnessSample[] selected = candidates.ToArray();
            if (selected.Length == 0) return Unknown(BackupFreshnessReason.BackupNotObserved, maximumAge);
            // An unresolved candidate can be newer than every known timestamp.
            if (selected.Any(sample => !sample.FinishUtc.HasValue)) return Unknown(BackupFreshnessReason.FinishTimeUnknown, maximumAge);
            BackupFreshnessSample latest = selected.OrderByDescending(sample => sample.FinishUtc).ThenBy(sample => sample.CopyOnly).First();
            if (latest.FinishUtc!.Value > evidence.ObservedAtUtc || latest.FinishUtc.Value > atUtc)
                return Unknown(BackupFreshnessReason.FutureSourceTime, maximumAge, latest);
            TimeSpan age = atUtc - latest.FinishUtc.Value;
            if (latest.IsDamaged == true)
                return Violation(BackupFreshnessReason.BackupDamaged, maximumAge, age, latest);
            if (latest.MetadataComplete != true || !latest.IsDamaged.HasValue)
                return Unknown(BackupFreshnessReason.BackupMetadataUnknown, maximumAge, latest, age);
            if (policy.RequireChecksums && latest.HasChecksum == false)
                return Violation(BackupFreshnessReason.ChecksumMissing, maximumAge, age, latest);
            if (policy.RequireChecksums && !latest.HasChecksum.HasValue)
                return Unknown(BackupFreshnessReason.ChecksumUnknown, maximumAge, latest, age);
            return age > maximumAge ? Violation(BackupFreshnessReason.AgeExceeded, maximumAge, age, latest)
                : new(BackupFreshnessStatus.WithinThreshold, BackupFreshnessReason.RecentBackup, maximumAge, age, latest);
        }
    }

    private static BackupFreshnessCheck Disabled(TimeSpan? age) =>
        new(BackupFreshnessStatus.Disabled, BackupFreshnessReason.PolicyDisabled, age, null, null);
    private static BackupFreshnessCheck Unknown(BackupFreshnessReason reason, TimeSpan? maximumAge,
        BackupFreshnessSample? sample = null, TimeSpan? age = null) =>
        new(BackupFreshnessStatus.Indeterminate, reason, maximumAge, age, sample);
    private static BackupFreshnessCheck Violation(BackupFreshnessReason reason, TimeSpan maximumAge,
        TimeSpan age, BackupFreshnessSample sample) =>
        new(BackupFreshnessStatus.PolicyViolated, reason, maximumAge, age, sample);
}
