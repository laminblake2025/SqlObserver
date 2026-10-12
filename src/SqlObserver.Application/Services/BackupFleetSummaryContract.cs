using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;

namespace SqlObserver.Application.Services;

/// <summary>Validates persisted aggregate structure; freshness and context must also be checked by the repository.</summary>
public static class BackupFleetSummaryContract
{
    public static void Validate(BackupFleetSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        BackupEvidenceHeader h = summary.Context ?? throw Invalid();
        try { BackupAssessmentQueryService.ValidateHeader(h, new(h.TargetId, h.AtUtc, 100, new(TimeSpan.FromSeconds(5)), h.TargetRevision)); }
        catch (ArgumentException exception) { throw new InvalidDataException("Backup summary context is invalid.", exception); }
        if (summary.ValidUntilUtc.Offset != TimeSpan.Zero || summary.ValidUntilUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0 ||
            summary.ValidUntilUtc < h.AtUtc || summary.ValidUntilUtc - h.AtUtc > BackupFleetSummaryBuilder.MaximumLifetime || !Enum.IsDefined(summary.Status)) throw Invalid();
        int expected = h.EvidenceStatus == BackupEvidenceStatus.Retained ? h.DatabaseCount : 0;
        ValidateCounts(summary.Full, expected); ValidateCounts(summary.Log, expected);
        if (summary.ObservedFaultDatabases < 0 || summary.ObservedFaultDatabases > expected ||
            summary.Status != Status(h, summary.Full, summary.Log)) throw Invalid();
    }

    internal static BackupFleetStatus Status(BackupEvidenceHeader h, BackupFleetCheckCounts full, BackupFleetCheckCounts log) =>
        h.EvidenceStatus != BackupEvidenceStatus.Retained ? BackupFleetStatus.Indeterminate
        : full.PolicyViolated + log.PolicyViolated > 0 ? BackupFleetStatus.PolicyViolated
        : !h.CollectorEnabled || h.Coverage != BackupFreshnessCoverage.Complete || full.Indeterminate + log.Indeterminate > 0 ? BackupFleetStatus.Indeterminate
        : full.Disabled + log.Disabled == h.DatabaseCount * 2 ? BackupFleetStatus.Disabled
        : full.WithinThreshold + log.WithinThreshold > 0 ? BackupFleetStatus.WithinPolicy
        : BackupFleetStatus.Indeterminate;

    private static void ValidateCounts(BackupFleetCheckCounts counts, int expected)
    {
        if (counts is null || counts.UnknownReasons is null || counts.UnknownReasons.Count > 25 ||
            new[] { counts.WithinThreshold, counts.PolicyViolated, counts.Indeterminate, counts.NotRequired, counts.Disabled }.Any(c => c is < 0 or > 512) ||
            counts.Total != expected) throw Invalid();
        int total = 0; int previous = -1;
        foreach (BackupFleetUnknownReason reason in counts.UnknownReasons)
        {
            if (reason is null || !Enum.IsDefined(reason.Reason) || (int)reason.Reason <= previous || reason.Count is < 1 or > 512 ||
                reason.Reason is BackupFreshnessReason.RecentBackup or BackupFreshnessReason.AgeExceeded or BackupFreshnessReason.BackupDamaged
                    or BackupFreshnessReason.ChecksumMissing or BackupFreshnessReason.SimpleRecovery or BackupFreshnessReason.PolicyDisabled) throw Invalid();
            previous = (int)reason.Reason; total += reason.Count;
        }
        if (total != counts.Indeterminate) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("The backup summary aggregate contract is invalid.");
}
