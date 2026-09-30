export type AlertRuleKind = "metric_threshold" | "collector_health" |
  "backup_full_policy_v2" | "backup_log_policy_v2" | "backup_observed_faults_v2";

export function isAlertRuleKind(value: unknown): value is AlertRuleKind {
  return value === "metric_threshold" || value === "collector_health" ||
    value === "backup_full_policy_v2" || value === "backup_log_policy_v2" || value === "backup_observed_faults_v2";
}

export function isBackupRuleKind(value: unknown): boolean {
  return value === "backup_full_policy_v2" || value === "backup_log_policy_v2" || value === "backup_observed_faults_v2";
}

export function ruleSignalLabel(kind: AlertRuleKind, metricId: string | null): string {
  switch (kind) {
    case "metric_threshold": return metricId ?? "Metric threshold";
    case "collector_health": return "Collector health";
    case "backup_full_policy_v2": return "Full backup policy";
    case "backup_log_policy_v2": return "Log backup policy";
    case "backup_observed_faults_v2": return "Observed backup faults";
  }
}

export function backupRuleExplanation(kind: AlertRuleKind): string | undefined {
  switch (kind) {
    case "backup_full_policy_v2": return "Alerts when databases exceed their configured full backup policy. Complete evidence within policy can clear the alert; missing or unknown evidence keeps it open.";
    case "backup_log_policy_v2": return "Alerts when databases exceed their configured log backup policy. Logs that are not required do not violate policy. Missing or unknown evidence keeps an active alert open.";
    case "backup_observed_faults_v2": return "Alerts on observed damaged or incomplete backup metadata. A sample with no observed faults cannot prove recovery, so the alert stays open until the rule is disabled.";
    default: return undefined;
  }
}

export function validBackupRuleSettings(rule: {
  readonly comparison: unknown; readonly threshold: unknown; readonly hysteresis: unknown;
  readonly confirmationCount: unknown; readonly clearConfirmationCount: unknown;
  readonly confirmationSeconds: unknown; readonly evaluationSeconds: unknown;
}): boolean {
  return rule.comparison === "greater_than" && rule.threshold === 0 && rule.hysteresis === 0 &&
    typeof rule.confirmationCount === "number" && typeof rule.clearConfirmationCount === "number" &&
    typeof rule.confirmationSeconds === "number" && typeof rule.evaluationSeconds === "number" &&
    rule.evaluationSeconds >= 15 &&
    rule.confirmationSeconds >= rule.evaluationSeconds * (Math.max(rule.confirmationCount, rule.clearConfirmationCount) - 1);
}
