import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface CpuObservationSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface CpuObservationBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface CpuObservationCoverage {
  readonly runCount: number; readonly retainedReceiptCount: number; readonly completeRunCount: number; readonly gapRunCount: number;
  readonly failedRunCount: number; readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly hasVisibilityGap: boolean;
}
export interface CpuObservationValue {
  readonly percent: number | null; readonly rawPercent: number | null;
  readonly quality: "available" | "provider_adjusted" | "unavailable"; readonly reason: string;
  readonly fromNative100ns: string | null; readonly toNative100ns: string | null;
  readonly observedFromUtc: string | null; readonly observedToUtc: string | null;
  readonly normalizationLogicalProcessors: number | null; readonly method: "pdh_calculate_exact_raw_pair";
  readonly providerApiStatus: number | null; readonly providerCStatus: number | null;
  readonly rawFormulaEligible: boolean; readonly rawFormulaReason: string; readonly providerRawPercent: number | null;
}
export interface CpuObservation {
  readonly sourceSchemaVersion: 1; readonly state: string; readonly reason: string;
  readonly sourceAssociationVerified: boolean; readonly associationReason: string; readonly exactPairEligible: boolean;
  readonly sqlProcess: CpuObservationValue; readonly operatingSystem: CpuObservationValue;
}
export interface CpuObservationReceipt extends CpuObservationSelection {
  readonly completedAtUtc: string; readonly outcome: string; readonly lossKind: string; readonly minimumLostItems: number;
  readonly observationPresent: boolean; readonly state: string; readonly reason: string; readonly observation: CpuObservation | null;
}
export interface CpuObservationHeader extends CpuObservationBinding {
  readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string;
}
export interface CpuObservationReceiptPage extends CpuObservationHeader { readonly coverage: CpuObservationCoverage; readonly items: readonly CpuObservationReceipt[]; readonly nextCursor: string | null }
export interface CpuObservationDetailPage extends CpuObservationHeader, CpuObservationSelection { readonly receipt: CpuObservationReceipt | null }
export const invalidCpuObservationHistory = "CPU observation history returned invalid or mismatched evidence. Retry this exact read or refresh its received source.";
const invalid = (): never => { throw new Error(invalidCpuObservationHistory); };
const integer = (value: unknown, min: number, max: number): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
const token = (value: unknown, allowed: readonly string[]): value is string => typeof value === "string" && allowed.includes(value);
function exact(value: unknown, keys: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return value as Record<string, unknown>;
}
export function cpuObservationUtc(value: unknown): string {
  if (typeof value !== "string") return invalid();
  try { return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
export function cpuObservationSourceUtc(value: unknown): string {
  if (typeof value !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$/u.test(value)) return invalid();
  try { utcMicroseconds(value.replace(/(\.\d{6})\dZ$/u, "$1Z")); } catch { return invalid(); }
  return value;
}
export function cpuObservationInt64(value: unknown, signed = false): string {
  if (typeof value !== "string" || !/^(?:0|[1-9]\d{0,18}|-[1-9]\d{0,18})$/u.test(value)) return invalid();
  const number = BigInt(value);
  if (number > 9223372036854775807n || number < (signed ? -9223372036854775808n : 0n)) return invalid();
  return value;
}
const optionalLong = (value: unknown, signed = false) => value === null ? null : cpuObservationInt64(value, signed);
const uint = (value: unknown): number => integer(value, 0, 4294967295) ? value : invalid();
const boolean = (value: unknown): boolean => typeof value === "boolean" ? value : invalid();
const nullableInteger = (value: unknown, min: number, max: number) => value === null ? null : integer(value, min, max) ? value : invalid();
export function cpuObservationWindow(fromUtc: string, toUtc: string) {
  const from = cpuObservationUtc(fromUtc), to = cpuObservationUtc(toUtc), duration = utcMicroseconds(to) - utcMicroseconds(from);
  if (duration <= 0n || duration > 31n * 86400n * 1000000n) throw new Error("CPU observation history supports a shared UTC window of up to 31 days. Choose a shorter window.");
  return { fromUtc: from, toUtc: to };
}
export function validCpuObservationSelection(value: unknown): value is CpuObservationSelection {
  if (!value || typeof value !== "object") return false;
  const row = value as CpuObservationSelection;
  return notificationId(row.runId) && integer(row.sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER);
}
export const cpuObservationSelectionIdentity = (value: CpuObservationSelection | null | undefined) => value === null ? "invalid" : value ? JSON.stringify([value.runId.toLowerCase(), value.sampleTargetRevision]) : "default";
export function cpuObservationContextIdentity(binding: CpuObservationBinding | undefined) {
  if (!binding) return "unavailable";
  try { const window = cpuObservationWindow(binding.fromUtc, binding.toUtc); return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, utcMicroseconds(window.fromUtc).toString(), utcMicroseconds(window.toUtc).toString()]); }
  catch { return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, binding.fromUtc, binding.toUtc]); }
}
export function cpuObservationCursor(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
}
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "visibility_incomplete"];
function coverage(value: unknown): CpuObservationCoverage {
  const row = exact(value, "runCount|retainedReceiptCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|hasVisibilityGap");
  for (const key of ["runCount", "retainedReceiptCount", "completeRunCount", "gapRunCount", "failedRunCount", "lostRunCount", "missingPayloadRunCount"]) if (!integer(row[key], 0, 10000)) return invalid();
  boolean(row.hasVisibilityGap);
  const result = row as unknown as CpuObservationCoverage;
  if (result.retainedReceiptCount > result.runCount || result.completeRunCount > result.retainedReceiptCount || result.completeRunCount + result.gapRunCount !== result.runCount || result.failedRunCount > result.gapRunCount || result.lostRunCount > result.runCount || result.missingPayloadRunCount > result.gapRunCount || result.gapRunCount > 0 && !result.hasVisibilityGap) return invalid();
  return result;
}
const reasonToken = (value: unknown): value is string => typeof value === "string" && /^[A-Za-z][A-Za-z0-9_]{0,63}$/u.test(value);
const decimal = (value: unknown): number | null => value === null ? null : typeof value === "number" && Number.isFinite(value) ? value : invalid();
function component(value: unknown): CpuObservationValue {
  const row = exact(value, "percent|rawPercent|quality|reason|fromNative100ns|toNative100ns|observedFromUtc|observedToUtc|normalizationLogicalProcessors|method|providerApiStatus|providerCStatus|rawFormulaEligible|rawFormulaReason|providerRawPercent");
  const percent = decimal(row.percent), rawPercent = decimal(row.rawPercent), providerRawPercent = decimal(row.providerRawPercent);
  const from = optionalLong(row.fromNative100ns, true), to = optionalLong(row.toNative100ns, true);
  const observedFromUtc = row.observedFromUtc === null ? null : cpuObservationSourceUtc(row.observedFromUtc), observedToUtc = row.observedToUtc === null ? null : cpuObservationSourceUtc(row.observedToUtc);
  const processors = nullableInteger(row.normalizationLogicalProcessors, 1, 4294967295);
  const api = row.providerApiStatus === null ? null : uint(row.providerApiStatus), status = row.providerCStatus === null ? null : uint(row.providerCStatus);
  const eligible = boolean(row.rawFormulaEligible);
  if (!token(row.quality, ["available", "provider_adjusted", "unavailable"]) || !reasonToken(row.reason) || !reasonToken(row.rawFormulaReason) || row.method !== "pdh_calculate_exact_raw_pair" ||
      (row.quality === "unavailable") !== (percent === null) || percent !== null && (percent < 0 || percent > 100 || from === null || to === null || BigInt(from) <= 0n || BigInt(to) <= BigInt(from) || !observedFromUtc || !observedToUtc || processors === null || api !== 0 || ![0,1].includes(status!) || providerRawPercent === null || row.quality === "available" && !eligible) ||
      percent !== null && observedFromUtc !== null && observedToUtc !== null && observedFromUtc >= observedToUtc) return invalid();
  return { ...row, percent, rawPercent, providerRawPercent, fromNative100ns: from, toNative100ns: to, observedFromUtc, observedToUtc, normalizationLogicalProcessors: processors, providerApiStatus: api, providerCStatus: status, rawFormulaEligible: eligible } as unknown as CpuObservationValue;
}
function observation(value: unknown): CpuObservation {
  const row = exact(value, "sourceSchemaVersion|state|reason|sourceAssociationVerified|associationReason|exactPairEligible|sqlProcess|operatingSystem");
  const sqlProcess = component(row.sqlProcess), operatingSystem = component(row.operatingSystem), both = sqlProcess.percent !== null && operatingSystem.percent !== null, any = sqlProcess.percent !== null || operatingSystem.percent !== null;
  const state = both && sqlProcess.quality === "available" && operatingSystem.quality === "available" ? "available" : any ? "partial" : "unavailable";
  const associated = boolean(row.sourceAssociationVerified), same = boolean(row.exactPairEligible);
  if (row.sourceSchemaVersion !== 1 || row.state !== state || row.reason !== (state === "available" ? "measured" : state === "partial" ? "component_quality" : "components_unavailable") || !reasonToken(row.associationReason) || any && !associated || same && (!both || !sqlProcess.rawFormulaEligible || !operatingSystem.rawFormulaEligible || sqlProcess.fromNative100ns !== operatingSystem.fromNative100ns || sqlProcess.toNative100ns !== operatingSystem.toNative100ns)) return invalid();
  return { ...row, sqlProcess, operatingSystem } as unknown as CpuObservation;
}
function receipt(value: unknown, header: CpuObservationHeader): CpuObservationReceipt {
  const row = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|lossKind|minimumLostItems|observationPresent|state|reason|observation");
  if (!validCpuObservationSelection(row) || row.sampleTargetRevision > header.targetRevision || !token(row.outcome, outcomes) || !token(row.lossKind, losses) || !integer(row.minimumLostItems, 0, 6) || row.lossKind === "none" && row.minimumLostItems !== 0) return invalid();
  const present = boolean(row.observationPresent), result = row.observation === null ? null : observation(row.observation), completedAtUtc = cpuObservationUtc(row.completedAtUtc);
  if (present !== (result !== null) || result && (row.outcome !== "succeeded" || row.lossKind !== "none" || row.state !== result.state || row.reason !== result.reason) || !present && (row.state !== "unavailable" || row.reason !== (row.outcome === "succeeded" ? "payload_missing" : "collection_failed")) || utcMicroseconds(completedAtUtc) < utcMicroseconds(header.fromUtc) || utcMicroseconds(completedAtUtc) >= utcMicroseconds(header.toUtc) || utcMicroseconds(completedAtUtc) > utcMicroseconds(header.cutoffUtc)) return invalid();
  return { ...row, runId: row.runId.toLowerCase(), completedAtUtc, observation: result } as unknown as CpuObservationReceipt;
}
function header(value: unknown, binding: CpuObservationBinding, extra: string): { row: Record<string, unknown>; common: CpuObservationHeader } {
  const row = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|" + extra);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) || !notificationId(row.targetId) || row.targetId.toLowerCase() !== binding.targetId.toLowerCase() || row.targetRevision !== binding.targetRevision || typeof row.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(row.readEpoch) || !token(row.state, ["available", "partial", "unavailable"])) return invalid();
  const window = cpuObservationWindow(binding.fromUtc, binding.toUtc), fromUtc = cpuObservationUtc(row.fromUtc), toUtc = cpuObservationUtc(row.toUtc), cutoffUtc = cpuObservationUtc(row.cutoffUtc), repositoryTimeUtc = cpuObservationUtc(row.repositoryTimeUtc);
  if (utcMicroseconds(fromUtc) !== utcMicroseconds(window.fromUtc) || utcMicroseconds(toUtc) !== utcMicroseconds(window.toUtc) || utcMicroseconds(cutoffUtc) > utcMicroseconds(repositoryTimeUtc)) return invalid();
  return { row, common: { targetId: row.targetId.toLowerCase(), targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, repositoryTimeUtc, readEpoch: row.readEpoch, state: String(row.state), reason: String(row.reason) } };
}
export function parseCpuObservationReceiptPage(value: unknown, binding: CpuObservationBinding, prior: readonly CpuObservationReceiptPage[] = [], cursor?: string): CpuObservationReceiptPage {
  const { row, common } = header(value, binding, "coverage|items|nextCursor"), cov = coverage(row.coverage);
  if (!Array.isArray(row.items) || row.items.length > 100 || row.nextCursor !== null && !cpuObservationCursor(row.nextCursor) || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!cpuObservationCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || row.nextCursor === cursor) || prior.some(page => page.nextCursor !== null && page.nextCursor === row.nextCursor)) return invalid();
  const state = cov.runCount === 0 ? "unavailable" : cov.hasVisibilityGap ? "partial" : "available";
  const reason = cov.runCount === 0 ? "no_completed_runs" : cov.hasVisibilityGap ? "visibility_gaps" : "retained_observations";
  if (common.state !== state || (common.reason === "revision_boundary" ? !cov.hasVisibilityGap : common.reason !== reason)) return invalid();
  if (prior.length && (prior[0]!.readEpoch !== common.readEpoch || utcMicroseconds(prior[0]!.cutoffUtc) !== utcMicroseconds(common.cutoffUtc) || prior[0]!.state !== common.state || prior[0]!.reason !== common.reason || JSON.stringify(prior[0]!.coverage) !== JSON.stringify(cov))) return invalid();
  const items = row.items.map(value => receipt(value, common)), all = [...prior.flatMap(page => page.items), ...items];
  let tail = prior.at(-1)?.items.at(-1); const seen = new Set(prior.flatMap(page => page.items.map(item => item.runId)));
  for (const item of items) {
    if (seen.has(item.runId) || tail && (utcMicroseconds(item.completedAtUtc) > utcMicroseconds(tail.completedAtUtc) || utcMicroseconds(item.completedAtUtc) === utcMicroseconds(tail.completedAtUtc) && item.runId >= tail.runId)) return invalid();
    seen.add(item.runId); tail = item;
  }
  const counts = { runCount: all.length, completeRunCount: all.filter(item => item.state === "available").length,
    failedRunCount: all.filter(item => item.outcome !== "succeeded").length, lostRunCount: all.filter(item => item.lossKind !== "none").length,
    missingPayloadRunCount: all.filter(item => item.outcome === "succeeded" && !item.observationPresent).length };
  if (all.length - counts.completeRunCount > cov.gapRunCount || all.filter(item => item.observationPresent).length > cov.retainedReceiptCount) return invalid();
  if (Object.entries(counts).some(([key, count]) => count > cov[key as keyof typeof counts] || row.nextCursor === null && count !== cov[key as keyof typeof counts]) || row.nextCursor !== null && (items.length !== 100 || all.length >= cov.runCount)) return invalid();
  return { ...common, coverage: cov, items, nextCursor: row.nextCursor as string | null };
}

export function parseCpuObservationDetailPage(value: unknown, binding: CpuObservationBinding, selected: CpuObservationSelection): CpuObservationDetailPage {
  if (!validCpuObservationSelection(selected)) return invalid();
  const { row, common } = header(value, binding, "runId|sampleTargetRevision|receipt");
  if (row.runId !== selected.runId.toLowerCase() || row.sampleTargetRevision !== selected.sampleTargetRevision || selected.sampleTargetRevision > binding.targetRevision) return invalid();
  const received = row.receipt === null ? null : receipt(row.receipt, common);
  if (received && (received.runId !== row.runId || received.sampleTargetRevision !== row.sampleTargetRevision || received.state !== common.state || received.reason !== common.reason) || !received && (common.state !== "unavailable" || !["observation_not_retained", "observation_outside_window"].includes(common.reason))) return invalid();
  return { ...common, runId: selected.runId.toLowerCase(), sampleTargetRevision: selected.sampleTargetRevision, receipt: received };
}

/** Completion-time plots never infer a shared denominator or bridge gaps between samples. */
export function cpuObservationPoints(pages: readonly CpuObservationReceiptPage[], component: "sqlProcess" | "operatingSystem") {
  return pages.flatMap(page => page.items).slice().reverse().map(receipt => ({ receipt, value: receipt.observation?.[component] ?? null }));
}

export function previousCpuObservationWindow(fromUtc: string, toUtc: string) {
  const window = cpuObservationWindow(fromUtc, toUtc), from = utcMicroseconds(window.fromUtc), start = from - (utcMicroseconds(window.toUtc) - from);
  const stamp = (value: bigint) => new Date(Number(value / 1000000n) * 1000).toISOString().slice(0,19) + "." + (value % 1000000n).toString().padStart(6,"0") + "Z";
  return cpuObservationWindow(stamp(start), window.fromUtc);
}
