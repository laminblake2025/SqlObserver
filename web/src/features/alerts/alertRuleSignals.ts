export type AlertRuleKind = "metric_threshold" | "collector_health" |
  "backup_full_policy_v2" | "backup_log_policy_v2" | "backup_observed_faults_v2" | "blocking_duration_v1";

export function isAlertRuleKind(value: unknown): value is AlertRuleKind {
  return value === "metric_threshold" || value === "collector_health" ||
    value === "backup_full_policy_v2" || value === "backup_log_policy_v2" || value === "backup_observed_faults_v2" ||
    value === "blocking_duration_v1";
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
    case "blocking_duration_v1": return "Blocking duration";
  }
}

export function ruleExplanation(kind: AlertRuleKind): string | undefined {
  switch (kind) {
    case "backup_full_policy_v2": return "Alerts when databases exceed their configured full backup policy. Complete evidence within policy can clear the alert; missing or unknown evidence keeps it open.";
    case "backup_log_policy_v2": return "Alerts when databases exceed their configured log backup policy. Logs that are not required do not violate policy. Missing or unknown evidence keeps an active alert open.";
    case "backup_observed_faults_v2": return "Alerts on observed damaged or incomplete backup metadata. A sample with no observed faults cannot prove recovery, so the alert stays open until the rule is disabled.";
    case "blocking_duration_v1": return "Alerts on the longest current blocked wait in seconds. Distinct complete observations confirm firing and recovery. Missing, stale or incomplete evidence keeps an active alert open. A complete snapshot with no blocking can confirm recovery.";
    default: return undefined;
  }
}

export function validBlockingRuleSettings(rule: {
  readonly comparison?: unknown; readonly threshold?: unknown; readonly hysteresis?: unknown;
  readonly confirmationCount?: unknown; readonly clearConfirmationCount?: unknown;
  readonly confirmationSeconds?: unknown; readonly evaluationSeconds?: unknown;
}): boolean {
  const integerIn = (value: unknown, min: number, max: number): value is number =>
    typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
  return (rule.comparison === "greater_than" || rule.comparison === "greater_than_or_equal") &&
    typeof rule.threshold === "number" && Number.isFinite(rule.threshold) && rule.threshold >= 0 && rule.threshold <= 86400 &&
    typeof rule.hysteresis === "number" && Number.isFinite(rule.hysteresis) && rule.hysteresis >= 0 && rule.hysteresis <= rule.threshold &&
    integerIn(rule.confirmationCount, 1, 100) && integerIn(rule.clearConfirmationCount, 1, 100) &&
    integerIn(rule.confirmationSeconds, 0, 604800) && integerIn(rule.evaluationSeconds, 5, 60) &&
    rule.confirmationSeconds >= rule.evaluationSeconds * (Math.max(rule.confirmationCount, rule.clearConfirmationCount) - 1);
}

export function alertReasonSummary(reason: string | undefined, value?: number | null): string | undefined {
  const match = reason?.match(/^blocking_duration_v1\|target=[1-9][0-9]*\|reason=([^|]+)\|digest=[a-f0-9]{64}$/);
  if (!match) return reason;
  switch (match[1]) {
    case "current": return typeof value === "number" && Number.isFinite(value)
      ? `Longest current blocked wait: ${value.toLocaleString()} seconds.` : "Complete blocking evidence was observed.";
    case "collector_disabled": return "Blocking collection is disabled. Enable blocking.current for this server to resume evidence. The alert remains open.";
    case "source_unavailable": return "No blocking evidence is available. Check collection status and SQL Server permissions. The alert remains open.";
    case "source_stale": return "Blocking evidence is stale. Check collector connectivity and scheduling. The alert remains open.";
    case "source_incomplete": return "Blocking collection failed or returned partial evidence. Check collector status and permissions. The alert remains open.";
    case "source_inconsistent": return "Blocking evidence failed integrity checks. Check collector diagnostics. The alert remains open.";
    default: return reason;
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
