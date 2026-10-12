import { notificationId, notificationUtc, validNotificationReason } from "./alertSnoozeModel.ts";
import { validAssigneeSid } from "./alertAssignmentModel.ts";
import { parseRuleCatalog, prepareRule, type RuleTemplate, type RuleValues } from "./alertRuleAdminActions.ts";
import { isAlertRuleKind } from "./alertRuleSignals.ts";
export type { RuleValues, RuleTemplate };
export interface ProductCatalog { readonly catalogDigest: string; readonly items: readonly RuleTemplate[]; readonly actorSid: string; readonly allTargetAdministrator: boolean }
export interface TemplateVersion {
  readonly templateId: string; readonly version: number; readonly currentVersion: number; readonly title: string;
  readonly ownerSid: string; readonly actorSid: string; readonly sourceTemplateName: string; readonly catalogDigest: string;
  readonly versionDigest: string; readonly values: RuleValues; readonly reason: string; readonly operationId: string;
  readonly auditId: string; readonly createdAtUtc: string; readonly serverNowUtc: string;
}
export interface RuleGroup { readonly kind: "environment" | "tag"; readonly value: string }
export type DeploymentMode = "create_missing" | "replace_named" | "rollback";
export type MemberStatus = "pending" | "applied" | "skipped" | "conflict" | "failed" | "stopped";
export interface DeploymentMember {
  readonly targetId: string; readonly targetName: string; readonly targetRevision: number; readonly groupingRevision: number;
  readonly ruleId: string; readonly expectedRuleRevision: number | null; readonly beforeValues: RuleValues | null;
  readonly desiredValues: RuleValues; readonly decision: "create" | "update" | "restore" | "disable_created" | "skip_existing" | "skip_unchanged" | "incompatible";
  readonly compatibility: "available" | "unavailable"; readonly compatibilityReason: string | null; readonly operationId: string;
  readonly status: MemberStatus; readonly resultRevision: number | null; readonly resultAuditId: string | null;
  readonly resultAtUtc: string | null; readonly resultCode: string | null; readonly currentRuleRevision: number | null;
  readonly currentValues: RuleValues | null; readonly matchesApplied: boolean | null;
}
export interface DeploymentStatus {
  readonly deploymentId: string; readonly revision: number; readonly actorSid: string; readonly group: RuleGroup | null;
  readonly mode: DeploymentMode; readonly sourceDeploymentId: string | null; readonly template: TemplateVersion;
  readonly previewAtUtc: string; readonly expiresAtUtc: string; readonly sealed: boolean; readonly pendingCount: number;
  readonly serverNowUtc: string; readonly reason: string; readonly items: readonly DeploymentMember[];
}
export interface DeploymentSummary {
  readonly deploymentId: string; readonly revision: number; readonly actorSid: string; readonly group: RuleGroup | null;
  readonly mode: DeploymentMode; readonly sourceDeploymentId: string | null; readonly templateId: string;
  readonly templateVersion: number; readonly templateTitle: string; readonly previewAtUtc: string; readonly sealed: boolean;
  readonly pendingCount: number; readonly memberCount: number; readonly serverNowUtc: string;
}
export interface BatchItem { readonly targetId: string; readonly ruleId: string; readonly status: MemberStatus;
  readonly code: string | null; readonly ruleRevision: number | null; readonly auditId: string | null; readonly recordedAtUtc: string | null }
export interface TemplateReceipt { readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string;
  readonly templateId: string; readonly version: number; readonly versionDigest: string }
export interface PreviewReceipt { readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string; readonly deployment: DeploymentStatus }
export interface BatchReceipt { readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string;
  readonly deploymentId: string; readonly revision: number; readonly action: "apply" | "stop"; readonly items: readonly BatchItem[];
  readonly pendingCount: number; readonly sealed: boolean }
export interface NativePage<T> { readonly serverNowUtc: string; readonly items: readonly T[]; readonly next: string | number | null }
export type TemplateCommand = { readonly kind: "create_template"; readonly body: { readonly operationId: string; readonly templateId: string; readonly title: string; readonly sourceTemplateName: string; readonly catalogDigest: string; readonly values: RuleValues; readonly reason: string } }
  | { readonly kind: "publish_template"; readonly templateId: string; readonly body: { readonly operationId: string; readonly expectedVersion: number; readonly title: string; readonly sourceTemplateName: string; readonly catalogDigest: string; readonly values: RuleValues; readonly reason: string } }
  | { readonly kind: "preview"; readonly body: { readonly operationId: string; readonly deploymentId: string; readonly templateId: string; readonly templateVersion: number; readonly group: RuleGroup; readonly mode: "create_missing" | "replace_named"; readonly reason: string } }
  | { readonly kind: "rollback"; readonly sourceDeploymentId: string; readonly body: { readonly operationId: string; readonly deploymentId: string; readonly reason: string } }
  | { readonly kind: "apply" | "stop"; readonly deploymentId: string; readonly body: { readonly operationId: string; readonly expectedRevision: number; readonly reason: string } };
export type CommandReceipt = TemplateReceipt | PreviewReceipt | BatchReceipt;
const invalid = (): never => { throw new Error("The rule template response could not be verified."); };
export function positive(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value > 0; }
const count = (value: unknown, high: number): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= high;
export function safeTemplateText(value: unknown, maximum = 256): value is string {
  return validNotificationReason(value) && new TextEncoder().encode(value).length <= maximum;
}
const digest = (value: unknown): value is string => typeof value === "string" && /^[a-f0-9]{64}$/u.test(value);
function exact(value: unknown, keys: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return value as Record<string, unknown>;
}
const nullableId = (value: unknown): string | null => value === null ? null : notificationId(value) ? value : invalid();
const nullableRevision = (value: unknown): number | null => value === null ? null : positive(value) ? value : invalid();
const nullableUtc = (value: unknown): string | null => value === null ? null : notificationUtc(value);
const nullableText = (value: unknown): string | null => value === null ? null : safeTemplateText(value, 512) ? value : invalid();
export function parseRuleValues(value: unknown): RuleValues {
  const r = exact(value, "name|kind|metricId|comparison|threshold|hysteresis|confirmationCount|confirmationSeconds|evaluationSeconds|clearConfirmationCount|enabled");
  if (typeof r.name !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u.test(r.name) || !isAlertRuleKind(r.kind)
    || (r.kind === "metric_threshold" ? typeof r.metricId !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/u.test(r.metricId) : r.metricId !== null)
    || typeof r.enabled !== "boolean") return invalid();
  const v = r as unknown as RuleValues;
  try { prepareRule(v, { ...v, unit: "native" }); } catch { return invalid(); }
  // Collector-health settings remain catalog-fixed when editing, but producer values still need numeric bounds.
  if (!Number.isFinite(v.threshold) || v.threshold < 0 || v.threshold > 1_000_000 || !Number.isFinite(v.hysteresis) || v.hysteresis < 0 || v.hysteresis > 1_000_000
    || !count(v.confirmationCount, 100) || v.confirmationCount === 0 || !count(v.clearConfirmationCount, 100) || v.clearConfirmationCount === 0
    || !count(v.confirmationSeconds, 604800) || !count(v.evaluationSeconds, 3600) || v.evaluationSeconds === 0
    || !["greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal", "equal"].includes(v.comparison)) return invalid();
  return { name: v.name, kind: v.kind, metricId: v.metricId, comparison: v.comparison, threshold: v.threshold,
    hysteresis: v.hysteresis, confirmationCount: v.confirmationCount, confirmationSeconds: v.confirmationSeconds,
    evaluationSeconds: v.evaluationSeconds, clearConfirmationCount: v.clearConfirmationCount, enabled: v.enabled };
}
export function parseProductCatalog(value: unknown): ProductCatalog {
  const r = exact(value, "catalogDigest|items|actorSid|allTargetAdministrator");
  if (!digest(r.catalogDigest) || !validAssigneeSid(r.actorSid) || typeof r.allTargetAdministrator !== "boolean") return invalid();
  if (!Array.isArray(r.items)) return invalid();
  r.items.forEach(item => exact(item, "name|kind|metricId|unit|comparison|threshold|hysteresis|confirmationCount|confirmationSeconds|evaluationSeconds|clearConfirmationCount"));
  const items = parseRuleCatalog({ targetId: "11111111-1111-4111-8111-111111111111", digest: r.catalogDigest, items: r.items }, "11111111-1111-4111-8111-111111111111");
  if (!items.length || new Set(items.map(item => item.name)).size !== items.length) return invalid();
  return { catalogDigest: r.catalogDigest, items, actorSid: r.actorSid, allTargetAdministrator: r.allTargetAdministrator };
}
export function parseTemplateVersion(value: unknown, id?: string, version?: number): TemplateVersion {
  const r = exact(value, "templateId|version|currentVersion|title|ownerSid|actorSid|sourceTemplateName|catalogDigest|versionDigest|values|reason|operationId|auditId|createdAtUtc|serverNowUtc");
  if (!notificationId(r.templateId) || id && r.templateId.toLowerCase() !== id.toLowerCase() || !positive(r.version) || version !== undefined && r.version !== version
    || !positive(r.currentVersion) || r.currentVersion < r.version || !safeTemplateText(r.title) || !validAssigneeSid(r.ownerSid) || !validAssigneeSid(r.actorSid)
    || !safeTemplateText(r.sourceTemplateName, 128) || !digest(r.catalogDigest) || !digest(r.versionDigest) || !validNotificationReason(r.reason)
    || !notificationId(r.operationId) || !notificationId(r.auditId)) return invalid();
  return { ...(r as unknown as TemplateVersion), values: parseRuleValues(r.values), createdAtUtc: notificationUtc(r.createdAtUtc), serverNowUtc: notificationUtc(r.serverNowUtc) };
}
export function parseRuleGroup(value: unknown): RuleGroup {
  const r = exact(value, "kind|value"); if (!["environment", "tag"].includes(String(r.kind)) || typeof r.value !== "string" || !/^[a-z0-9][a-z0-9._:-]{0,63}$/u.test(r.value)) return invalid();
  return r as unknown as RuleGroup;
}
const statuses = ["pending", "applied", "skipped", "conflict", "failed", "stopped"];
export function parseDeploymentMember(value: unknown): DeploymentMember {
  const r = exact(value, "targetId|targetName|targetRevision|groupingRevision|ruleId|expectedRuleRevision|beforeValues|desiredValues|decision|compatibility|compatibilityReason|operationId|status|resultRevision|resultAuditId|resultAtUtc|resultCode|currentRuleRevision|currentValues|matchesApplied");
  if (!notificationId(r.targetId) || !safeTemplateText(r.targetName, 256) || !positive(r.targetRevision) || !count(r.groupingRevision, Number.MAX_SAFE_INTEGER)
    || !notificationId(r.ruleId) || !notificationId(r.operationId) || !statuses.includes(String(r.status))
    || !["create", "update", "restore", "disable_created", "skip_existing", "skip_unchanged", "incompatible"].includes(String(r.decision))
    || !["available", "unavailable"].includes(String(r.compatibility)) || r.matchesApplied !== null && typeof r.matchesApplied !== "boolean") return invalid();
  const desiredValues = parseRuleValues(r.desiredValues), currentValues = r.currentValues === null ? null : parseRuleValues(r.currentValues);
  const currentRuleRevision = nullableRevision(r.currentRuleRevision), resultRevision = nullableRevision(r.resultRevision);
  if ((currentValues === null) !== (currentRuleRevision === null) || r.matchesApplied === true && (r.status !== "applied" || currentRuleRevision !== resultRevision || JSON.stringify(currentValues) !== JSON.stringify(desiredValues))) return invalid();
  return { ...(r as unknown as DeploymentMember), expectedRuleRevision: nullableRevision(r.expectedRuleRevision),
    beforeValues: r.beforeValues === null ? null : parseRuleValues(r.beforeValues), desiredValues, currentValues, currentRuleRevision,
    resultRevision, resultAuditId: nullableId(r.resultAuditId), resultAtUtc: nullableUtc(r.resultAtUtc),
    resultCode: nullableText(r.resultCode), compatibilityReason: nullableText(r.compatibilityReason) };
}
function deploymentBase(r: Record<string, unknown>) {
  if (!notificationId(r.deploymentId) || !positive(r.revision) || !validAssigneeSid(r.actorSid) || !["create_missing", "replace_named", "rollback"].includes(String(r.mode))
    || typeof r.sealed !== "boolean" || !count(r.pendingCount, 100) || r.sealed && r.pendingCount !== 0) return invalid();
  const group = r.group === null ? null : parseRuleGroup(r.group), sourceDeploymentId = nullableId(r.sourceDeploymentId);
  if (r.mode === "rollback" ? group !== null || sourceDeploymentId === null : group === null || sourceDeploymentId !== null) return invalid();
  return { group, sourceDeploymentId, previewAtUtc: notificationUtc(r.previewAtUtc), serverNowUtc: notificationUtc(r.serverNowUtc) };
}
export function parseDeploymentStatus(value: unknown, id?: string): DeploymentStatus {
  const r = exact(value, "deploymentId|revision|actorSid|group|mode|sourceDeploymentId|template|previewAtUtc|expiresAtUtc|sealed|pendingCount|serverNowUtc|reason|items"), base = deploymentBase(r);
  if (id && String(r.deploymentId).toLowerCase() !== id.toLowerCase() || !validNotificationReason(r.reason) || !Array.isArray(r.items) || r.items.length < 1 || r.items.length > 100) return invalid();
  const items = r.items.map(parseDeploymentMember); if (new Set(items.map(item => item.targetId.toLowerCase())).size !== items.length || items.filter(item => item.status === "pending").length !== r.pendingCount) return invalid();
  return { ...(r as unknown as DeploymentStatus), ...base, template: parseTemplateVersion(r.template), expiresAtUtc: notificationUtc(r.expiresAtUtc), items };
}
export function parseDeploymentSummary(value: unknown): DeploymentSummary {
  const r = exact(value, "deploymentId|revision|actorSid|group|mode|sourceDeploymentId|templateId|templateVersion|templateTitle|previewAtUtc|sealed|pendingCount|memberCount|serverNowUtc"), base = deploymentBase(r);
  if (!notificationId(r.templateId) || !positive(r.templateVersion) || !safeTemplateText(r.templateTitle) || !positive(r.memberCount) || r.memberCount > 100 || Number(r.pendingCount) > r.memberCount) return invalid();
  return { ...(r as unknown as DeploymentSummary), ...base };
}
export function parseBatchReceipt(value: unknown, command?: Extract<TemplateCommand, { kind: "apply" | "stop" }>, members?: readonly DeploymentMember[]): BatchReceipt {
  const r = exact(value, "operationId|auditId|recordedAtUtc|deploymentId|revision|action|items|pendingCount|sealed");
  if (!notificationId(r.operationId) || !notificationId(r.auditId) || !notificationId(r.deploymentId) || !positive(r.revision)
    || !["apply", "stop"].includes(String(r.action)) || !count(r.pendingCount, 100) || typeof r.sealed !== "boolean" || r.sealed && r.pendingCount !== 0
    || !Array.isArray(r.items) || r.items.length > (r.action === "apply" ? 10 : 100)
    || command && (r.operationId !== command.body.operationId || r.deploymentId !== command.deploymentId || r.action !== command.kind || r.revision !== command.body.expectedRevision + 1)) return invalid();
  const seen = new Set<string>(), items = r.items.map(value => {
    const item = exact(value, "targetId|ruleId|status|code|ruleRevision|auditId|recordedAtUtc");
    if (!notificationId(item.targetId) || !notificationId(item.ruleId) || seen.has(item.targetId.toLowerCase()) || !statuses.includes(String(item.status)) || item.status === "pending"
      || members && !members.some(member => member.targetId === item.targetId && member.ruleId === item.ruleId)) return invalid();
    seen.add(item.targetId.toLowerCase()); return { ...(item as unknown as BatchItem), code: nullableText(item.code), ruleRevision: nullableRevision(item.ruleRevision), auditId: nullableId(item.auditId), recordedAtUtc: nullableUtc(item.recordedAtUtc) };
  });
  return { ...(r as unknown as BatchReceipt), recordedAtUtc: notificationUtc(r.recordedAtUtc), items };
}
export function parseCommandReceipt(value: unknown, command: TemplateCommand, members?: readonly DeploymentMember[]): CommandReceipt {
  if (command.kind === "apply" || command.kind === "stop") return parseBatchReceipt(value, command, members);
  if (command.kind === "preview" || command.kind === "rollback") {
    const r = exact(value, "operationId|auditId|recordedAtUtc|deployment");
    if (r.operationId !== command.body.operationId || !notificationId(r.auditId)) return invalid();
    const deployment = parseDeploymentStatus(r.deployment, command.body.deploymentId);
    if (deployment.revision !== 1) return invalid();
    if (command.kind === "preview" ? deployment.template.templateId !== command.body.templateId || deployment.template.version !== command.body.templateVersion || JSON.stringify(deployment.group) !== JSON.stringify(command.body.group) || deployment.mode !== command.body.mode
      : deployment.mode !== "rollback" || deployment.sourceDeploymentId !== command.sourceDeploymentId) return invalid();
    return { operationId: r.operationId as string, auditId: r.auditId, recordedAtUtc: notificationUtc(r.recordedAtUtc), deployment };
  }
  if (command.kind !== "create_template" && command.kind !== "publish_template") return invalid();
  const r = exact(value, "operationId|auditId|recordedAtUtc|templateId|version|versionDigest"), id = command.kind === "create_template" ? command.body.templateId : command.templateId;
  if (r.operationId !== command.body.operationId || r.templateId !== id || !notificationId(r.auditId) || !digest(r.versionDigest)
    || r.version !== (command.kind === "create_template" ? 1 : command.body.expectedVersion + 1) || !positive(r.version)) return invalid();
  return { ...(r as unknown as TemplateReceipt), recordedAtUtc: notificationUtc(r.recordedAtUtc) };
}
export function parseNativePage<T>(value: unknown, kind: "templates" | "versions" | "deployments" | "history", prior: readonly NativePage<T>[] = [], before?: string | number, resourceId?: string): NativePage<T> {
  const anchor = kind === "templates" ? "nextBeforeTemplateId" : kind === "versions" ? "nextBeforeVersion" : kind === "deployments" ? "nextBeforeDeploymentId" : "nextBeforeOperationId";
  const resource = kind === "versions" ? "templateId" : kind === "history" ? "deploymentId" : undefined;
  const r = exact(value, "serverNowUtc|items|" + anchor + (resource ? "|" + resource : ""));
  if (resource && r[resource] !== resourceId || !Array.isArray(r.items) || r.items.length > 50 || Boolean(prior.length) !== (before !== undefined) || before !== undefined && prior.at(-1)?.next !== before) return invalid();
  const parse = (item: unknown): unknown => kind === "templates" ? parseTemplateVersion(item) : kind === "versions" ? parseTemplateVersion(item, resourceId)
    : kind === "deployments" ? parseDeploymentSummary(item) : parseBatchReceipt(item);
  const identity = (item: unknown): string | number => {
    const v = item as TemplateVersion & DeploymentSummary & BatchReceipt;
    return kind === "templates" ? v.templateId : kind === "versions" ? v.version : kind === "deployments" ? v.deploymentId : v.operationId;
  };
  const seen = new Set(prior.flatMap(page => page.items.map(identity))), items = r.items.map(parse) as T[];
  for (const item of items) { const key = identity(item); if (seen.has(key)) return invalid(); seen.add(key); }
  const next = r[anchor]; if (next !== null && (items.length !== 50 || next !== identity(items.at(-1)) || (kind === "versions" ? !positive(next) : !notificationId(next)))) return invalid();
  if (kind === "history" && items.some(item => (item as BatchReceipt).deploymentId !== resourceId)) return invalid();
  if (kind === "versions" && items.some((item, index) => index > 0 && (item as TemplateVersion).version >= (items[index - 1] as TemplateVersion).version || before !== undefined && (item as TemplateVersion).version >= Number(before))) return invalid();
  return { serverNowUtc: notificationUtc(r.serverNowUtc), items, next: next as string | number | null };
}
export function commandResource(command: TemplateCommand): { readonly kind: "template" | "deployment"; readonly id: string; readonly version?: number } {
  if (command.kind === "create_template") return { kind: "template", id: command.body.templateId, version: 1 };
  if (command.kind === "publish_template") return { kind: "template", id: command.templateId, version: command.body.expectedVersion + 1 };
  return { kind: "deployment", id: command.kind === "preview" || command.kind === "rollback" ? command.body.deploymentId : command.deploymentId };
}
