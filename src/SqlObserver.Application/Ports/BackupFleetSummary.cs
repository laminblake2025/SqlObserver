using SqlObserver.Domain.Backups;

namespace SqlObserver.Application.Ports;

public enum BackupFleetStatus { Indeterminate = 0, WithinPolicy = 1, PolicyViolated = 2, Disabled = 3 }

public sealed record BackupFleetUnknownReason(BackupFreshnessReason Reason, int Count);

/// <summary>One check per database, with unknown causes retained separately from policy violations.</summary>
public sealed record BackupFleetCheckCounts(int WithinThreshold, int PolicyViolated, int Indeterminate,
    int NotRequired, int Disabled, IReadOnlyList<BackupFleetUnknownReason> UnknownReasons)
{
    public int Total => WithinThreshold + PolicyViolated + Indeterminate + NotRequired + Disabled;
}

/// <summary>
/// A bounded aggregate assessed at Context.AtUtc, valid strictly before ValidUntilUtc.
/// Readers must also match the current receipt, target revision, policy history and qualification command.
/// This describes observed backup policy checks, not restore-chain or recovery-point certification.
/// </summary>
public sealed record BackupFleetSummary(BackupEvidenceHeader Context, DateTimeOffset ValidUntilUtc,
    BackupFleetStatus Status, BackupFleetCheckCounts Full, BackupFleetCheckCounts Log, int ObservedFaultDatabases);
