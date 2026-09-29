import { readJsonBounded } from "./alertRuntimeParser.ts";
import type { AlertRuleAdminItem } from "./alertRuleAdminApi";

export interface RuleTemplate {
  readonly name: string;
  readonly kind: AlertRuleAdminItem["kind"];
  readonly metricId: string | null;
  readonly unit: string;
  readonly comparison: AlertRuleAdminItem["comparison"];
  readonly threshold: number;
  readonly hysteresis: number;
  readonly confirmationCount: number;
  readonly confirmationSeconds: number;
  readonly evaluationSeconds: number;
  readonly clearConfirmationCount: number;
}

export type RuleValues = Omit<RuleTemplate, "unit"> & { readonly enabled: boolean };

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const namePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const digestPattern = /^[0-9a-f]{64}$/;
const comparisons = new Set(["greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal", "equal"]);
const numberIn = (value: unknown, minimum: number, maximum: number) =>
  typeof value === "number" && Number.isFinite(value) && value >= minimum && value <= maximum;
const integerIn = (value: unknown, minimum: number, maximum: number) =>
  numberIn(value, minimum, maximum) && Number.isSafeInteger(value);

export function parseRuleCatalog(value: unknown, targetId: string): readonly RuleTemplate[] {
  if (!value || typeof value !== "object" || Array.isArray(value) || !uuid.test(targetId))
    throw new Error("Alert rule catalog is invalid.");
  const catalog = value as Record<string, unknown>;
  if (catalog.targetId !== targetId || typeof catalog.digest !== "string" ||
      !digestPattern.test(catalog.digest) || !Array.isArray(catalog.items) || catalog.items.length > 100)
    throw new Error("Alert rule catalog is invalid.");
  return catalog.items.map((entry: unknown): RuleTemplate => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) throw new Error("Alert rule catalog is invalid.");
    const item = entry as Record<string, unknown>;
    if (typeof item.name !== "string" || !namePattern.test(item.name) ||
        item.kind !== "metric_threshold" && item.kind !== "collector_health" ||
        item.kind === "metric_threshold" && (typeof item.metricId !== "string" || !namePattern.test(item.metricId)) ||
        item.kind === "collector_health" && item.metricId !== null ||
        typeof item.unit !== "string" || !item.unit || !comparisons.has(item.comparison as string) ||
        !numberIn(item.threshold, 0, 1_000_000) || !numberIn(item.hysteresis, 0, 1_000_000) ||
        !integerIn(item.confirmationCount, 1, 100) || !integerIn(item.confirmationSeconds, 0, 604800) ||
        !integerIn(item.evaluationSeconds, 1, 3600) || !integerIn(item.clearConfirmationCount, 1, 100))
      throw new Error("Alert rule catalog is invalid.");
    return item as unknown as RuleTemplate;
  });
}

export async function getRuleCatalog(targetId: string, signal: AbortSignal): Promise<readonly RuleTemplate[]> {
  if (!uuid.test(targetId)) throw new Error("Invalid server identifier.");
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/rules/catalog`, {
    credentials: "same-origin", headers: { Accept: "application/json" }, signal,
  });
  if (!response.ok) throw new Error(response.status === 403
    ? "A Target Administrator role on this server is required to manage alert rules."
    : "Alert rule templates are temporarily unavailable.");
  return parseRuleCatalog(await readJsonBounded(response), targetId);
}

export function prepareRule(values: RuleValues, template: RuleTemplate): RuleValues {
  if (values.kind !== template.kind || values.metricId !== template.metricId ||
      !namePattern.test(values.name) ||
      values.kind === "collector_health" && values.name !== template.name)
    throw new Error("Select an approved rule template and enter a valid rule name.");
  if (values.kind === "collector_health") {
    if (values.comparison !== template.comparison || values.threshold !== template.threshold ||
        values.hysteresis !== template.hysteresis || values.confirmationCount !== template.confirmationCount ||
        values.confirmationSeconds !== template.confirmationSeconds || values.evaluationSeconds !== template.evaluationSeconds)
      throw new Error("Collector health settings must match the approved template.");
  } else if (values.comparison !== "greater_than" && values.comparison !== "greater_than_or_equal" ||
      !numberIn(values.threshold, 0, 1_000_000) || !numberIn(values.hysteresis, 0, values.threshold) ||
      !integerIn(values.confirmationCount, 1, 100) || !integerIn(values.confirmationSeconds, 0, 604800) ||
      !integerIn(values.evaluationSeconds, 15, 3600))
    throw new Error("Enter a valid threshold, hysteresis, and confirmation settings.");
  if (!integerIn(values.clearConfirmationCount, 1, 100))
    throw new Error("Clear confirmation must be between 1 and 100 observations.");
  return values;
}

export class RuleAttempts {
  private readonly creates = new Map<string, { ruleId: string; operationId: string }>();
  private readonly disables = new Map<string, string>();
  private createKey(targetId: string, rule: RuleValues) { return JSON.stringify([targetId.toLowerCase(), rule]); }
  createFor(targetId: string, rule: RuleValues) {
    const key = this.createKey(targetId, rule);
    let attempt = this.creates.get(key);
    if (!attempt) { attempt = { ruleId: crypto.randomUUID(), operationId: crypto.randomUUID() }; this.creates.set(key, attempt); }
    return attempt;
  }
  completeCreate(targetId: string, rule: RuleValues) { this.creates.delete(this.createKey(targetId, rule)); }
  disableFor(targetId: string, ruleId: string, revision: number) {
    const key = JSON.stringify([targetId.toLowerCase(), ruleId.toLowerCase(), revision]);
    let operationId = this.disables.get(key);
    if (!operationId) { operationId = crypto.randomUUID(); this.disables.set(key, operationId); }
    return operationId;
  }
  completeDisable(targetId: string, ruleId: string, revision: number) {
    this.disables.delete(JSON.stringify([targetId.toLowerCase(), ruleId.toLowerCase(), revision]));
  }
}

function mutationError(response: Response): Error {
  if (response.status === 403) return new Error("A Target Administrator role on this server is required.");
  if (response.status === 409) return new Error("This rule changed. Refresh before trying again.");
  if (response.status === 429) return new Error("Too many changes. Wait and retry the same action.");
  if (response.status === 503) return new Error("Alert rule changes are temporarily unavailable. Retry the same action.");
  return new Error("The alert rule change could not be saved.");
}

export async function createRule(targetId: string, rule: RuleValues,
    attempt: { ruleId: string; operationId: string }, signal: AbortSignal): Promise<void> {
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/rules`, {
    method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ ...rule, ...attempt }), signal,
  });
  if (!response.ok) throw mutationError(response);
}

export async function disableRule(targetId: string, rule: AlertRuleAdminItem,
    operationId: string, signal: AbortSignal): Promise<void> {
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/rules/${encodeURIComponent(rule.ruleId)}/disable`, {
    method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ ...rule, operationId, expectedRevision: rule.revision, enabled: false }), signal,
  });
  if (!response.ok) throw mutationError(response);
}
