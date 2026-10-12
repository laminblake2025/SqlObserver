namespace SqlObserver.Domain.Alerting;

public static class AlertRuleKindWire
{
    public static string Format(AlertRuleKind kind) => kind switch
    {
        AlertRuleKind.MetricThreshold => "metric_threshold",
        AlertRuleKind.CollectorHealth => "collector_health",
        AlertRuleKind.BackupFullPolicy => "backup_full_policy_v2",
        AlertRuleKind.BackupLogPolicy => "backup_log_policy_v2",
        AlertRuleKind.BackupObservedFaults => "backup_observed_faults_v2",
        AlertRuleKind.BlockingDuration => BlockingAlertRuleContract.SourceKind,
        AlertRuleKind.DeadlockCount => DeadlockAlertRuleContract.SourceKind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static AlertRuleKind Parse(string kind) => kind switch
    {
        "metric_threshold" => AlertRuleKind.MetricThreshold,
        "collector_health" => AlertRuleKind.CollectorHealth,
        "backup_full_policy_v2" => AlertRuleKind.BackupFullPolicy,
        "backup_log_policy_v2" => AlertRuleKind.BackupLogPolicy,
        "backup_observed_faults_v2" => AlertRuleKind.BackupObservedFaults,
        BlockingAlertRuleContract.SourceKind => AlertRuleKind.BlockingDuration,
        DeadlockAlertRuleContract.SourceKind => AlertRuleKind.DeadlockCount,
        _ => throw new ArgumentException("Rule kind is not allowlisted.", nameof(kind)),
    };
}
