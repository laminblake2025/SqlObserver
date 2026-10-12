import { utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface AgentActivitySelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface AgentActivityBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface AgentActivityCoverage { readonly runCount: number; readonly retainedReceiptCount: number; readonly retainedCaptureCount: number; readonly completeSourceRunCount: number; readonly gapRunCount: number; readonly failedRunCount: number; readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly hasVisibilityGap: boolean }
export interface AgentActivityQualification { readonly productionAdmitted: false; readonly serviceSessionAssociationQualified: false; readonly nativeQualification: "Absent" }
export interface AgentActivityHeader extends AgentActivityBinding, AgentActivityQualification { readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string }
export interface AgentActivityReceipt extends AgentActivitySelection { readonly completedAtUtc: string; readonly outcome: string; readonly canonicalReason: string; readonly lossKind: string; readonly minimumLostItems: number; readonly countIsExact: boolean; readonly receiptRetained: boolean; readonly capturePresent: boolean; readonly sourceRowsRead: number | null; readonly retainedJobCount: number | null; readonly truncated: boolean | null; readonly state: string; readonly reason: string }
export interface AgentActivitySourceHeader { readonly sourceObservedAtUtc: string; readonly service: { readonly matchingServiceCount: number; readonly status: number | null; readonly processId: number | null; readonly startupAtUtc: string | null }; readonly session: { readonly sessionId: number | null; readonly agentStartLocal: string | null } }
export interface AgentActivityJob { readonly jobId: string; readonly sessionId: number; readonly requestedLocal: string | null; readonly queuedLocal: string | null; readonly startExecutionLocal: string | null; readonly stopExecutionLocal: string | null; readonly lastExecutedStepId: number | null; readonly lastExecutedStepLocal: string | null; readonly candidateState: "Unknown" | "Running" | "Queued" | "Stopped" }
export interface AgentActivityCapture extends AgentActivityQualification { readonly sourceSchemaVersion: 1; readonly enrollmentRevision: number; readonly before: AgentActivitySourceHeader; readonly after: AgentActivitySourceHeader; readonly jobs: readonly AgentActivityJob[]; readonly sourceRowsRead: number; readonly truncated: boolean; readonly gap: string }
export interface AgentActivityReceiptPage extends AgentActivityHeader { readonly coverage: AgentActivityCoverage; readonly items: readonly AgentActivityReceipt[]; readonly nextCursor: string | null }
export interface AgentActivityDetailPage extends AgentActivityHeader, AgentActivitySelection { readonly receipt: AgentActivityReceipt | null; readonly capture: AgentActivityCapture | null }
export const invalidAgentActivityHistory = "Agent activity history returned invalid or mismatched evidence. Retry this exact read or refresh its received source.";
const invalid = (): never => { throw new Error(invalidAgentActivityHistory); };
const integer = (value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
const boolean = (value: unknown): boolean => typeof value === "boolean" ? value : invalid();
const nullableInteger = (value: unknown, min: number, max: number): number | null => value === null ? null : integer(value, min, max) ? value : invalid();
const token = (value: unknown, allowed: readonly string[]): value is string => typeof value === "string" && allowed.includes(value);
const reasonToken = (value: unknown): value is string => typeof value === "string" && /^[A-Za-z][A-Za-z0-9_]{0,63}$/u.test(value);
function exact(value: unknown, keys: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return value as Record<string, unknown>;
}
const qualificationKeys = "productionAdmitted|serviceSessionAssociationQualified|nativeQualification";
function qualification(row: Record<string, unknown>): AgentActivityQualification {
  if (row.productionAdmitted !== false || row.serviceSessionAssociationQualified !== false || row.nativeQualification !== "Absent") return invalid();
  return { productionAdmitted: false, serviceSessionAssociationQualified: false, nativeQualification: "Absent" };
}
/** UTC identity includes the seventh digit; Date is used only to validate the Gregorian second. */
export function agentActivityUtc(value: unknown): string {
  if (typeof value !== "string") return invalid();
  const match = /^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/u.exec(value);
  if (!match) return invalid();
  try { utcMicroseconds(match[1] + "Z"); } catch { return invalid(); }
  return match[1] + "." + (match[2] ?? "").padEnd(7, "0") + "Z";
}
export function agentActivityTicks(value: unknown): bigint {
  const utc = agentActivityUtc(value);
  return utcMicroseconds(utc.slice(0, 26) + "Z") * 10n + BigInt(utc[26]!);
}
export function agentActivitySourceUtc(value: unknown): string {
  if (typeof value !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$/u.test(value) || Number(value.slice(0, 4)) < 1753) return invalid();
  return agentActivityUtc(value);
}
/** SQL job dates are validated without assigning a zone or deriving elapsed time. */
export function agentActivityLocal(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}$/u.test(value) || Number(value.slice(0, 4)) < 1753) return invalid();
  try { utcMicroseconds(value.slice(0, 19) + "Z"); } catch { return invalid(); }
  return value;
}
export function agentActivityWindow(fromUtc: string, toUtc: string) {
  const from = agentActivityUtc(fromUtc), to = agentActivityUtc(toUtc), duration = agentActivityTicks(to) - agentActivityTicks(from);
  if (agentActivityTicks(from) % 10n !== 0n || agentActivityTicks(to) % 10n !== 0n) throw new Error("Choose UTC window boundaries with at most six fractional digits.");
  if (duration <= 0n || duration > 31n * 86400n * 10000000n) throw new Error("Agent activity history supports a UTC completion window of up to 31 days. Choose a shorter window.");
  return { fromUtc: from.slice(0, 26) + "Z", toUtc: to.slice(0, 26) + "Z" };
}
export function validAgentActivitySelection(value: unknown): value is AgentActivitySelection {
  if (!value || typeof value !== "object") return false;
  const row = value as AgentActivitySelection;
  return notificationId(row.runId) && integer(row.sampleTargetRevision, 1);
}
export const agentActivitySelectionIdentity = (value: AgentActivitySelection | null | undefined) => value === null ? "invalid" : value ? JSON.stringify([value.runId.toLowerCase(), value.sampleTargetRevision]) : "default";
export function agentActivityContextIdentity(binding: AgentActivityBinding | undefined) {
  if (!binding) return "unavailable";
  try { const window = agentActivityWindow(binding.fromUtc, binding.toUtc); return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, window.fromUtc, window.toUtc]); }
  catch { return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, binding.fromUtc, binding.toUtc]); }
}
export function agentActivityCursor(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
}
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "visibility_incomplete"];
const canonicalReasons = ["completed", "source_row_limit", "response_byte_limit", "deadline_exceeded", "transient_target_failure", "permanent_target_failure", "required_permission_missing", "target_unsupported", "output_validation_failed", "lease_ownership_lost", "circuit_currently_open", "capability_profile_missing", "capability_profile_stale", "capability_missing", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "visibility_incomplete", "target_revision_changed"];
function coverage(value: unknown): AgentActivityCoverage {
  const row = exact(value, "runCount|retainedReceiptCount|retainedCaptureCount|completeSourceRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|hasVisibilityGap");
  for (const key of Object.keys(row).filter(key => key !== "hasVisibilityGap")) if (!integer(row[key], 0, 10000)) return invalid();
  boolean(row.hasVisibilityGap);
  const result = row as unknown as AgentActivityCoverage;
  if (result.retainedReceiptCount > result.runCount || result.retainedCaptureCount > result.retainedReceiptCount || result.completeSourceRunCount > result.retainedCaptureCount || result.completeSourceRunCount + result.gapRunCount !== result.runCount || result.failedRunCount > result.gapRunCount || result.lostRunCount > result.gapRunCount || result.missingPayloadRunCount > result.gapRunCount || result.gapRunCount > 0 && !result.hasVisibilityGap) return invalid();
  return result;
}
function receipt(value: unknown, header: AgentActivityHeader): AgentActivityReceipt {
  const row = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|canonicalReason|lossKind|minimumLostItems|countIsExact|receiptRetained|capturePresent|sourceRowsRead|retainedJobCount|truncated|state|reason");
  if (!validAgentActivitySelection(row) || row.sampleTargetRevision > header.targetRevision || !token(row.outcome, outcomes) || !token(row.lossKind, losses) || !integer(row.minimumLostItems, 0, 513) || !token(row.canonicalReason, canonicalReasons) || !reasonToken(row.reason) || !token(row.state, ["available", "partial", "unavailable"])) return invalid();
  const retained = boolean(row.receiptRetained), present = boolean(row.capturePresent), countIsExact = boolean(row.countIsExact), sourceRowsRead = nullableInteger(row.sourceRowsRead, 0, 513), retainedJobCount = nullableInteger(row.retainedJobCount, 0, 512);
  const truncated = row.truncated === null ? null : boolean(row.truncated), completedAtUtc = agentActivityUtc(row.completedAtUtc);
  if (present && (!retained || !["succeeded", "partial"].includes(row.outcome) || sourceRowsRead === null || retainedJobCount !== Math.min(sourceRowsRead, 512) || truncated !== (sourceRowsRead > 512) || truncated !== (row.outcome === "partial") || row.state !== (truncated ? "partial" : "available") || row.reason !== (truncated ? "source_truncated" : "source_complete")) || !present && (sourceRowsRead !== null || retainedJobCount !== null || truncated !== null || row.state !== "unavailable" || row.reason !== (["succeeded", "partial"].includes(row.outcome) ? "payload_missing" : "collection_failed")) || row.outcome === "succeeded" && (row.canonicalReason !== "completed" || row.lossKind !== "none") || row.outcome === "partial" && (row.canonicalReason !== "source_row_limit" || row.lossKind !== "source_row_limit" || row.minimumLostItems !== 1 || countIsExact) || row.lossKind === "none" && (row.minimumLostItems !== 0 || !countIsExact) || agentActivityTicks(completedAtUtc) % 10n !== 0n || agentActivityTicks(completedAtUtc) < agentActivityTicks(header.fromUtc) || agentActivityTicks(completedAtUtc) >= agentActivityTicks(header.toUtc) || agentActivityTicks(completedAtUtc) > agentActivityTicks(header.cutoffUtc)) return invalid();
  return { ...row, runId: row.runId.toLowerCase(), completedAtUtc } as unknown as AgentActivityReceipt;
}
function sourceHeader(value: unknown): AgentActivitySourceHeader {
  const row = exact(value, "sourceObservedAtUtc|service|session"), service = exact(row.service, "matchingServiceCount|status|processId|startupAtUtc"), session = exact(row.session, "sessionId|agentStartLocal");
  if (!integer(service.matchingServiceCount, 0, 2147483647)) return invalid();
  return { sourceObservedAtUtc: agentActivitySourceUtc(row.sourceObservedAtUtc), service: { matchingServiceCount: service.matchingServiceCount, status: nullableInteger(service.status, 1, 7), processId: nullableInteger(service.processId, 0, 2147483647), startupAtUtc: service.startupAtUtc === null ? null : agentActivitySourceUtc(service.startupAtUtc) }, session: { sessionId: nullableInteger(session.sessionId, 1, 2147483647), agentStartLocal: agentActivityLocal(session.agentStartLocal) } };
}
export function agentActivityGap(before: AgentActivitySourceHeader, after: AgentActivitySourceHeader, truncated: boolean): string {
  if (truncated) return "SourceTruncated";
  if (before.service.matchingServiceCount === 0 || after.service.matchingServiceCount === 0) return "ServiceMissing";
  if (before.service.matchingServiceCount !== 1 || after.service.matchingServiceCount !== 1) return "ServiceAmbiguous";
  if (before.service.status === null || after.service.status === null) return "ServiceStateUnavailable";
  if (before.service.status !== 4 || after.service.status !== 4) return "ServiceNotRunning";
  if (!before.service.processId || !after.service.processId || !before.service.startupAtUtc || !after.service.startupAtUtc) return "ServiceEpochUnavailable";
  if (before.service.processId !== after.service.processId || before.service.startupAtUtc !== after.service.startupAtUtc) return "ServiceEpochChanged";
  if (agentActivityTicks(before.service.startupAtUtc) > agentActivityTicks(before.sourceObservedAtUtc) || agentActivityTicks(after.service.startupAtUtc) > agentActivityTicks(after.sourceObservedAtUtc)) return "ServiceClockBoundary";
  if (before.session.sessionId === null || after.session.sessionId === null || before.session.agentStartLocal === null || after.session.agentStartLocal === null) return "SessionUnavailable";
  if (before.session.sessionId !== after.session.sessionId || before.session.agentStartLocal !== after.session.agentStartLocal) return "SessionChanged";
  return "None";
}
function capture(value: unknown): AgentActivityCapture {
  const row = exact(value, "sourceSchemaVersion|enrollmentRevision|before|after|jobs|sourceRowsRead|truncated|gap|" + qualificationKeys), before = sourceHeader(row.before), after = sourceHeader(row.after), flags = qualification(row);
  const truncated = boolean(row.truncated), gap = agentActivityGap(before, after, truncated);
  if (row.sourceSchemaVersion !== 1 || !integer(row.enrollmentRevision, 1) || !integer(row.sourceRowsRead, 0, 513) || !Array.isArray(row.jobs) || row.jobs.length !== Math.min(row.sourceRowsRead, 512) || truncated !== (row.sourceRowsRead > 512) || row.gap !== gap || agentActivityTicks(after.sourceObservedAtUtc) < agentActivityTicks(before.sourceObservedAtUtc) || agentActivityTicks(after.sourceObservedAtUtc) - agentActivityTicks(before.sourceObservedAtUtc) > 100000000n) return invalid();
  let previous = "";
  const jobs = row.jobs.map(value => {
    const job = exact(value, "jobId|sessionId|requestedLocal|queuedLocal|startExecutionLocal|stopExecutionLocal|lastExecutedStepId|lastExecutedStepLocal|candidateState");
    if (!notificationId(job.jobId) || job.jobId !== job.jobId.toLowerCase() || job.jobId <= previous || !integer(job.sessionId, 1, 2147483647) || job.sessionId !== before.session.sessionId) return invalid();
    previous = job.jobId;
    const requestedLocal = agentActivityLocal(job.requestedLocal), queuedLocal = agentActivityLocal(job.queuedLocal), startExecutionLocal = agentActivityLocal(job.startExecutionLocal), stopExecutionLocal = agentActivityLocal(job.stopExecutionLocal), lastExecutedStepLocal = agentActivityLocal(job.lastExecutedStepLocal), lastExecutedStepId = nullableInteger(job.lastExecutedStepId, 0, 2147483647);
    const candidateState = gap !== "None" ? "Unknown" : startExecutionLocal !== null && stopExecutionLocal === null ? "Running" : startExecutionLocal === null && stopExecutionLocal === null && (requestedLocal !== null || queuedLocal !== null) ? "Queued" : startExecutionLocal !== null && stopExecutionLocal !== null ? "Stopped" : "Unknown";
    if (job.candidateState !== candidateState) return invalid();
    return { ...job, requestedLocal, queuedLocal, startExecutionLocal, stopExecutionLocal, lastExecutedStepLocal, lastExecutedStepId, candidateState } as unknown as AgentActivityJob;
  });
  return { ...row, ...flags, before, after, jobs } as unknown as AgentActivityCapture;
}
function header(value: unknown, binding: AgentActivityBinding, extra: string) {
  const row = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|" + qualificationKeys + "|" + extra), flags = qualification(row);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1) || !notificationId(row.targetId) || row.targetId.toLowerCase() !== binding.targetId.toLowerCase() || row.targetRevision !== binding.targetRevision || typeof row.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(row.readEpoch) || !token(row.state, ["available", "partial", "unavailable"]) || !reasonToken(row.reason)) return invalid();
  const window = agentActivityWindow(binding.fromUtc, binding.toUtc), fromUtc = agentActivityUtc(row.fromUtc), toUtc = agentActivityUtc(row.toUtc), cutoffUtc = agentActivityUtc(row.cutoffUtc), repositoryTimeUtc = agentActivityUtc(row.repositoryTimeUtc);
  if (agentActivityTicks(fromUtc) !== agentActivityTicks(window.fromUtc) || agentActivityTicks(toUtc) !== agentActivityTicks(window.toUtc) || agentActivityTicks(cutoffUtc) % 10n !== 0n || agentActivityTicks(repositoryTimeUtc) % 10n !== 0n || agentActivityTicks(cutoffUtc) > agentActivityTicks(repositoryTimeUtc)) return invalid();
  return { row, common: { targetId: row.targetId.toLowerCase(), targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, repositoryTimeUtc, readEpoch: row.readEpoch, state: row.state, reason: row.reason, ...flags } as AgentActivityHeader };
}
export function parseAgentActivityReceiptPage(value: unknown, binding: AgentActivityBinding, prior: readonly AgentActivityReceiptPage[] = [], cursor?: string): AgentActivityReceiptPage {
  const { row, common } = header(value, binding, "coverage|items|nextCursor"), cov = coverage(row.coverage);
  if (!Array.isArray(row.items) || row.items.length > 100 || row.nextCursor !== null && !agentActivityCursor(row.nextCursor) || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!agentActivityCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || row.nextCursor === cursor) || prior.some(page => page.nextCursor !== null && page.nextCursor === row.nextCursor)) return invalid();
  if (prior.length && (prior[0]!.readEpoch !== common.readEpoch || prior[0]!.cutoffUtc !== common.cutoffUtc || prior[0]!.state !== common.state || prior[0]!.reason !== common.reason || JSON.stringify(prior[0]!.coverage) !== JSON.stringify(cov))) return invalid();
  const expectedState = cov.runCount === 0 ? "unavailable" : cov.hasVisibilityGap ? "partial" : "available";
  const expectedReason = cov.runCount === 0 ? "no_completed_runs" : cov.hasVisibilityGap ? "visibility_gaps" : "retained_observations";
  if (common.state !== expectedState || (common.reason === "revision_boundary" ? !cov.hasVisibilityGap : common.reason !== expectedReason)) return invalid();
  const items = row.items.map(value => receipt(value, common)), all = [...prior.flatMap(page => page.items), ...items];
  let tail = prior.at(-1)?.items.at(-1); const seen = new Set(prior.flatMap(page => page.items.map(item => item.runId)));
  for (const item of items) { if (seen.has(item.runId) || tail && (agentActivityTicks(item.completedAtUtc) > agentActivityTicks(tail.completedAtUtc) || item.completedAtUtc === tail.completedAtUtc && item.runId >= tail.runId)) return invalid(); seen.add(item.runId); tail = item; }
  const counts = { runCount: all.length, retainedReceiptCount: all.filter(item => item.receiptRetained).length, retainedCaptureCount: all.filter(item => item.capturePresent).length, completeSourceRunCount: all.filter(item => item.state === "available").length, gapRunCount: all.filter(item => item.state !== "available").length, failedRunCount: all.filter(item => !["succeeded", "partial"].includes(item.outcome)).length, lostRunCount: all.filter(item => item.lossKind !== "none").length, missingPayloadRunCount: all.filter(item => ["succeeded", "partial"].includes(item.outcome) && !item.capturePresent).length };
  if (Object.entries(counts).some(([key, count]) => count > cov[key as Exclude<keyof AgentActivityCoverage, "hasVisibilityGap">] || row.nextCursor === null && count !== cov[key as Exclude<keyof AgentActivityCoverage, "hasVisibilityGap">]) || row.nextCursor !== null && (items.length !== 100 || all.length >= cov.runCount)) return invalid();
  return { ...common, coverage: cov, items, nextCursor: row.nextCursor as string | null };
}
export function parseAgentActivityDetailPage(value: unknown, binding: AgentActivityBinding, selected: AgentActivitySelection): AgentActivityDetailPage {
  if (!validAgentActivitySelection(selected)) return invalid();
  const { row, common } = header(value, binding, "runId|sampleTargetRevision|receipt|capture");
  if (row.runId !== selected.runId.toLowerCase() || row.sampleTargetRevision !== selected.sampleTargetRevision || selected.sampleTargetRevision > binding.targetRevision) return invalid();
  const received = row.receipt === null ? null : receipt(row.receipt, common), source = row.capture === null ? null : capture(row.capture);
  if (received && (received.runId !== row.runId || received.sampleTargetRevision !== row.sampleTargetRevision || received.state !== common.state || received.reason !== common.reason || received.capturePresent !== (source !== null)) || !received && (source !== null || common.state !== "unavailable" || !["observation_not_retained", "observation_outside_window"].includes(common.reason)) || source && received && (source.sourceRowsRead !== received.sourceRowsRead || source.jobs.length !== received.retainedJobCount || source.truncated !== received.truncated)) return invalid();
  return { ...common, runId: selected.runId.toLowerCase(), sampleTargetRevision: selected.sampleTargetRevision, receipt: received, capture: source };
}
