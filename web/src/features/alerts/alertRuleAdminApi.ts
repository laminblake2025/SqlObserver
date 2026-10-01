import { apiFetch } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { isAlertRuleKind, isBackupRuleKind, validBlockingRuleSettings, validBackupRuleSettings, type AlertRuleKind } from "./alertRuleSignals.ts";

export interface AlertRuleAdminItem {
  readonly ruleId: string;
  readonly name: string;
  readonly kind: AlertRuleKind;
  readonly metricId: string | null;
  readonly comparison: "greater_than" | "greater_than_or_equal" | "less_than" | "less_than_or_equal" | "equal";
  readonly threshold: number;
  readonly hysteresis: number;
  readonly confirmationCount: number;
  readonly confirmationSeconds: number;
  readonly evaluationSeconds: number;
  readonly clearConfirmationCount: number;
  readonly enabled: boolean;
  readonly revision: number;
  readonly updatedAtUtc: string;
}

export interface AlertRuleAdminPage {
  readonly targetId: string;
  readonly listingStartedUtc: string;
  readonly items: readonly AlertRuleAdminItem[];
  readonly hasMore: boolean;
  readonly nextCursor?: string;
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const utc = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;
const comparisons = new Set(["greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal", "equal"]);
const invalid = () => new Error("Alert rule response is invalid.");
const isUtc = (value: unknown): value is string => typeof value === "string" && utc.test(value) && Number.isFinite(Date.parse(value));
const isInteger = (value: unknown, minimum: number, maximum: number): value is number =>
  typeof value === "number" && Number.isSafeInteger(value) && value >= minimum && value <= maximum;

export function parseAlertRuleAdminPage(value: unknown, targetId: string): AlertRuleAdminPage {
  if (value === null || typeof value !== "object" || Array.isArray(value) || !uuid.test(targetId)) throw invalid();
  const page = value as Record<string, unknown>;
  if (typeof page.targetId !== "string" || page.targetId.toLowerCase() !== targetId.toLowerCase() ||
      !isUtc(page.listingStartedUtc) || !Array.isArray(page.items) || page.items.length > 100 ||
      typeof page.hasMore !== "boolean" ||
      (page.hasMore && (page.items.length === 0 || typeof page.nextCursor !== "string" || page.nextCursor.length === 0 ||
        page.nextCursor.length > 1024 || !/^[A-Za-z0-9+/]+={0,2}$/.test(page.nextCursor))) ||
      (!page.hasMore && page.nextCursor !== null)) throw invalid();
  const ids = new Set<string>();
  const items = page.items.map((value: unknown): AlertRuleAdminItem => {
    if (value === null || typeof value !== "object" || Array.isArray(value)) throw invalid();
    const rule = value as Record<string, unknown>;
    if (typeof rule.ruleId !== "string" || !uuid.test(rule.ruleId) ||
        typeof rule.name !== "string" || rule.name.length === 0 || rule.name.length > 128 ||
        !isAlertRuleKind(rule.kind) ||
        rule.kind === "metric_threshold" && (typeof rule.metricId !== "string" || rule.metricId.length === 0 || rule.metricId.length > 128) ||
        rule.kind !== "metric_threshold" && rule.metricId !== null ||
        !comparisons.has(rule.comparison as string) ||
        typeof rule.threshold !== "number" || !Number.isFinite(rule.threshold) ||
        typeof rule.hysteresis !== "number" || !Number.isFinite(rule.hysteresis) || rule.hysteresis < 0 ||
        !isInteger(rule.confirmationCount, 1, 100) || !isInteger(rule.confirmationSeconds, 0, 604800) ||
        !isInteger(rule.evaluationSeconds, 1, 3600) || !isInteger(rule.clearConfirmationCount, 1, 100) ||
        typeof rule.enabled !== "boolean" || !isInteger(rule.revision, 1, Number.MAX_SAFE_INTEGER) ||
        !isUtc(rule.updatedAtUtc) || rule.kind === "blocking_duration_v1" && !validBlockingRuleSettings(rule) || isBackupRuleKind(rule.kind) && !validBackupRuleSettings({
          comparison: rule.comparison, threshold: rule.threshold, hysteresis: rule.hysteresis,
          confirmationCount: rule.confirmationCount, clearConfirmationCount: rule.clearConfirmationCount,
          confirmationSeconds: rule.confirmationSeconds, evaluationSeconds: rule.evaluationSeconds,
        })) throw invalid();
    const key = rule.ruleId.toLowerCase();
    if (ids.has(key)) throw invalid();
    ids.add(key);
    return rule as unknown as AlertRuleAdminItem;
  });
  return { targetId, listingStartedUtc: page.listingStartedUtc, items,
    hasMore: page.hasMore, nextCursor: page.hasMore ? page.nextCursor as string : undefined };
}

export async function getAlertRulesForAdmin(targetId: string, signal: AbortSignal, cursor?: string): Promise<AlertRuleAdminPage> {
  if (!uuid.test(targetId) || cursor !== undefined && (cursor.length === 0 || cursor.length > 1024))
    throw new Error("Invalid alert rule page request.");
  const query = new URLSearchParams({ limit: "100" });
  if (cursor) query.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/rules?${query}`, {
    credentials: "same-origin", headers: { Accept: "application/json" }, signal,
  });
  if (!response.ok) throw new Error(response.status === 403
    ? "A Target Administrator role on this server is required to view alert rules."
    : "Alert rules are temporarily unavailable.");
  return parseAlertRuleAdminPage(await readJsonBounded(response), targetId);
}
