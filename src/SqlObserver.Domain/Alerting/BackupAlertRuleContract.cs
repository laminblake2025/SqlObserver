namespace SqlObserver.Domain.Alerting;

/// <summary>Backup alert observations are affected-database counts, not ages or collector-health booleans.</summary>
public static class BackupAlertRuleContract
{
    public static bool IsBackup(AlertRuleKind kind) => kind is AlertRuleKind.BackupFullPolicy
        or AlertRuleKind.BackupLogPolicy or AlertRuleKind.BackupObservedFaults;

    public static string SourceKind(AlertRuleKind kind) => kind switch
    {
        AlertRuleKind.BackupFullPolicy => "backup_full_policy_v2",
        AlertRuleKind.BackupLogPolicy => "backup_log_policy_v2",
        AlertRuleKind.BackupObservedFaults => "backup_observed_faults_v2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static void ValidateObservation(AlertRuleDefinition rule, AlertObservation observation)
    {
        ArgumentNullException.ThrowIfNull(rule); ArgumentNullException.ThrowIfNull(observation);
        if (!IsBackup(rule.Kind) || observation.SourceKind != SourceKind(rule.Kind) ||
            observation.SourceCollector != "backups.status" || observation.SourceVersion != "2" ||
            observation.SourceSchemaVersion != 2 || observation.MetricId is not null || observation.CollectorHealthy is not null ||
            !Guid.TryParseExact(observation.SampleId, "D", out Guid sample) || sample == Guid.Empty || observation.SampleId != sample.ToString("D") ||
            observation.RunId == Guid.Empty || observation.Value is { } count && (count < 0 || count > 512 || count != Math.Truncate(count)) ||
            observation.SourceDigest is { } digest && (digest.Length != 64 || digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
            observation.SourceDigest is null && (observation.Value is not null || observation.RunId is not null) ||
            observation.Value is not null && observation.RunId is null ||
            rule.Kind == AlertRuleKind.BackupObservedFaults && observation.Value == 0 ||
            observation.EvidenceDigest != AlertEvidenceDigest.Compute(observation))
            throw new ArgumentException("Backup alert observation has an invalid signal or source binding.", nameof(observation));
    }
}
