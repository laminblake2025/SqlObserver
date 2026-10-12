using System.Collections.ObjectModel;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;

namespace SqlObserver.Application.Services;

/// <summary>Independent full/log/fault signals; unknown evidence never becomes a clearing zero.</summary>
public static class BackupAlertSignalProjection
{
    public static IReadOnlyList<BackupAlertSignal> Build(BackupAlertEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.TargetId);
        if (evidence.TargetRevision <= 0 || evidence.ReadAtUtc.Offset != TimeSpan.Zero ||
            evidence.ReadAtUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw Invalid();
        BackupFleetSummary? summary = evidence.Summary;
        if (summary is null)
        {
            if (evidence.SourceDigest is not null) throw Invalid();
            return Unknown(BackupAlertSignalReason.NoCurrentAggregate);
        }
        BackupFleetSummaryContract.Validate(summary);
        if (summary.Context.TargetId != evidence.TargetId || summary.Context.TargetRevision != evidence.TargetRevision ||
            summary.Context.AtUtc > evidence.ReadAtUtc || evidence.ReadAtUtc >= summary.ValidUntilUtc ||
            evidence.SourceDigest is not { Length: 64 } digest ||
            digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid();
        if (!summary.Context.CollectorEnabled) return Unknown(BackupAlertSignalReason.CollectorDisabled);
        if (summary.Context.EvidenceStatus != BackupEvidenceStatus.Retained)
            return Unknown(BackupAlertSignalReason.SourceUnavailable);

        BackupAlertSignal full = Check(BackupAlertSignalKind.FullPolicy, summary.Full);
        BackupAlertSignal log = Check(BackupAlertSignalKind.LogPolicy, summary.Log);
        BackupAlertSignal faults = summary.ObservedFaultDatabases > 0
            ? new(BackupAlertSignalKind.ObservedFaults, BackupAlertSignalState.Violated,
                BackupAlertSignalReason.ObservedBackupFaults, summary.ObservedFaultDatabases)
            : summary.Context.Coverage != BackupFreshnessCoverage.Complete
                ? Hold(BackupAlertSignalKind.ObservedFaults, BackupAlertSignalReason.IncompleteCoverage)
            : summary.Full.Indeterminate + summary.Log.Indeterminate > 0
                ? Hold(BackupAlertSignalKind.ObservedFaults, BackupAlertSignalReason.UnknownChecks)
            : summary.Full.Disabled + summary.Log.Disabled > 0
                ? Hold(BackupAlertSignalKind.ObservedFaults, BackupAlertSignalReason.DisabledChecks)
                // The aggregate counts observed faults but does not prove metadata
                // completeness for every unselected differential/copy-only sample.
                : Hold(BackupAlertSignalKind.ObservedFaults, BackupAlertSignalReason.FaultAbsenceUnproven);
        return Array.AsReadOnly(new[] { full, log, faults });

        BackupAlertSignal Check(BackupAlertSignalKind kind, BackupFleetCheckCounts counts)
        {
            // A confirmed violation survives other databases' unknown checks.
            if (counts.PolicyViolated > 0)
                return new(kind, BackupAlertSignalState.Violated, BackupAlertSignalReason.ViolationsObserved, counts.PolicyViolated);
            if (counts.Disabled == summary.Context.DatabaseCount)
                return new(kind, BackupAlertSignalState.Disabled, BackupAlertSignalReason.DisabledChecks, null);
            if (summary.Context.Coverage != BackupFreshnessCoverage.Complete)
                return Hold(kind, BackupAlertSignalReason.IncompleteCoverage);
            if (counts.Indeterminate > 0) return Hold(kind, BackupAlertSignalReason.UnknownChecks);
            if (counts.Disabled > 0) return Hold(kind, BackupAlertSignalReason.DisabledChecks);
            if (kind == BackupAlertSignalKind.LogPolicy && counts.NotRequired == summary.Context.DatabaseCount)
                return new(kind, BackupAlertSignalState.NotRequired, BackupAlertSignalReason.AllLogsNotRequired, 0);
            return new(kind, BackupAlertSignalState.Clear, BackupAlertSignalReason.CompleteChecksWithinPolicy, 0);
        }
    }

    private static ReadOnlyCollection<BackupAlertSignal> Unknown(BackupAlertSignalReason reason) =>
        Array.AsReadOnly(Enum.GetValues<BackupAlertSignalKind>().Select(kind => Hold(kind, reason)).ToArray());
    private static BackupAlertSignal Hold(BackupAlertSignalKind kind, BackupAlertSignalReason reason) =>
        new(kind, BackupAlertSignalState.Unknown, reason, null);
    private static InvalidDataException Invalid() => new("Backup alert evidence has an invalid context.");
}
