import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import type { ReplicationSelection } from "./replicationSelectionModel.ts";
export type { ReplicationSelection } from "./replicationSelectionModel.ts";

export interface ReplicationWindowBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export type ReplicationBinding = ReplicationWindowBinding;
export interface ReplicationCoverage { readonly runCount: number; readonly completeRunCount: number; readonly gapRunCount: number; readonly failedRunCount: number; readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly emptyRunCount: number; readonly retainedRowCount: number; readonly unboundSourceRowCount: number; readonly hasVisibilityGap: boolean }
export interface ReplicationReceipt extends ReplicationSelection { readonly completedAtUtc: string; readonly outcome: string; readonly lossKind: string | null; readonly minimumLostItems: number | null; readonly expectedRowCount: number; readonly storedRowCount: number; readonly monitorObservedAtUtc: string | null; readonly state: string; readonly reason: string }
export interface ReplicationHeader extends ReplicationWindowBinding { readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string; readonly statusQualification: "legacy_unqualified"; readonly observationTimeBasis: "monitor_capture"; readonly nextCursor: string | null }
export interface ReplicationReceiptPage extends ReplicationHeader { readonly coverage: ReplicationCoverage; readonly items: readonly ReplicationReceipt[] }
export interface ReplicationRow { readonly rowIdentity: string; readonly subscriptionFingerprint: string | null; readonly monitorObservedAtUtc: string; readonly collectedAtUtc: string; readonly role: string; readonly reportedStatus: string; readonly pendingCommands: string | null; readonly latencySeconds: number | null; readonly sourceCoverage: string; readonly stateAvailable: boolean; readonly topology: null; readonly ratePerSecond: null; readonly lastSuccessAgeSeconds: null }
export interface ReplicationSnapshotPage extends ReplicationHeader, ReplicationSelection { readonly receipt: ReplicationReceipt | null; readonly items: readonly ReplicationRow[] }
export const invalidReplicationHistory = "Replication history returned invalid or mismatched evidence. Retry this exact read or refresh its source.";
const invalid = (): never => { throw new Error(invalidReplicationHistory); };
const integer = (v: unknown, min: number, max: number): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= min && v <= max;
const token = (v: unknown, allowed: readonly string[]): v is string => typeof v === "string" && allowed.includes(v);
function exact(v: unknown, keys: string): Record<string, unknown> {
  if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return v as Record<string, unknown>;
}
export function replicationUtc(v: unknown): string { if (typeof v !== "string") return invalid(); try { return canonicalUtcTimestamp(v.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); } }
export function replicationWindow(fromUtc: string, toUtc: string) {
  const from = replicationUtc(fromUtc), to = replicationUtc(toUtc), duration = utcMicroseconds(to) - utcMicroseconds(from);
  if (duration <= 0n || duration > 7n * 86400n * 1000000n) throw new Error("Replication history supports a shared UTC window of up to seven days. Choose a shorter window.");
  return { fromUtc: from, toUtc: to };
}
export function validReplicationSelection(v: unknown): v is ReplicationSelection { if (!v || typeof v !== "object") return false; const r = v as ReplicationSelection; return notificationId(r.runId) && integer(r.sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER); }
export const replicationSelectionIdentity = (v: ReplicationSelection | null | undefined) => v === null ? "invalid" : v ? JSON.stringify([v.runId.toLowerCase(), v.sampleTargetRevision]) : "default";
export function replicationContextIdentity(v: ReplicationWindowBinding | undefined) { if (!v) return "unavailable"; try { const w = replicationWindow(v.fromUtc, v.toUtc); return JSON.stringify([v.targetId.toLowerCase(), v.targetRevision, utcMicroseconds(w.fromUtc).toString(), utcMicroseconds(w.toUtc).toString()]); } catch { return JSON.stringify(v); } }
export function replicationCursor(v: unknown): v is string { if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false; try { return btoa(atob(v)) === v; } catch { return false; } }
const hash = (v: unknown): v is string => typeof v === "string" && /^[a-f0-9]{64}$/u.test(v);
function commands(v: unknown): string | null { if (v === null) return null; if (typeof v !== "string" || !/^(?:0|[1-9]\d{0,18})$/u.test(v) || BigInt(v) > 2000000000n) return invalid(); return v; }
const optionalCount = (v: unknown, max: number) => v === null ? null : integer(v, 0, max) ? v : invalid();
const abs = (v: bigint) => v < 0n ? -v : v;
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "blocking_graph_limit", "duplicate_overlap", "visibility_incomplete"];
const reasons = ["observed_rows", "observed_empty", "collection_failed", "collection_loss", "payload_missing", "stored_count_mismatch", "source_clock_skew", "source_schema_unsupported", "source_partial", "collection_gap"];
function header(value: unknown, binding: ReplicationWindowBinding, extra: string) {
  const r = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|statusQualification|observationTimeBasis|nextCursor|" + extra);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) || !notificationId(r.targetId) || r.targetId.toLowerCase() !== binding.targetId.toLowerCase() || r.targetRevision !== binding.targetRevision || !hash(r.readEpoch) || !token(r.state, ["available", "partial", "unavailable"]) || r.statusQualification !== "legacy_unqualified" || r.observationTimeBasis !== "monitor_capture" || r.nextCursor !== null && !replicationCursor(r.nextCursor)) return invalid();
  const w = replicationWindow(binding.fromUtc, binding.toUtc), fromUtc = replicationUtc(r.fromUtc), toUtc = replicationUtc(r.toUtc), cutoffUtc = replicationUtc(r.cutoffUtc), repositoryTimeUtc = replicationUtc(r.repositoryTimeUtc);
  if (utcMicroseconds(fromUtc) !== utcMicroseconds(w.fromUtc) || utcMicroseconds(toUtc) !== utcMicroseconds(w.toUtc) || utcMicroseconds(cutoffUtc) > utcMicroseconds(repositoryTimeUtc)) return invalid();
  return { row: r, common: { targetId: r.targetId.toLowerCase(), targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, repositoryTimeUtc, readEpoch: r.readEpoch, state: r.state, reason: String(r.reason), statusQualification: r.statusQualification, observationTimeBasis: r.observationTimeBasis, nextCursor: r.nextCursor } as ReplicationHeader };
}
function receipt(value: unknown, h: ReplicationHeader): ReplicationReceipt {
  const r = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|lossKind|minimumLostItems|expectedRowCount|storedRowCount|monitorObservedAtUtc|state|reason");
  if (!validReplicationSelection(r) || r.sampleTargetRevision > h.targetRevision || !token(r.outcome, outcomes) || r.lossKind !== null && !token(r.lossKind, losses) || !integer(r.expectedRowCount, 0, 2048) || !integer(r.storedRowCount, 0, 2048) || !token(r.state, ["available", "partial", "unavailable"]) || !token(r.reason, reasons)) return invalid();
  const completedAtUtc = replicationUtc(r.completedAtUtc), monitorObservedAtUtc = r.monitorObservedAtUtc === null ? null : replicationUtc(r.monitorObservedAtUtc), minimumLostItems = optionalCount(r.minimumLostItems, 2147483647);
  if (utcMicroseconds(completedAtUtc) < utcMicroseconds(h.fromUtc) || utcMicroseconds(completedAtUtc) >= utcMicroseconds(h.toUtc) || utcMicroseconds(completedAtUtc) > utcMicroseconds(h.cutoffUtc) || r.storedRowCount === 0 && monitorObservedAtUtc !== null || r.state === "available" && (!["observed_rows", "observed_empty"].includes(r.reason) || r.outcome !== "succeeded" || r.lossKind !== "none" || minimumLostItems !== 0 || r.expectedRowCount !== r.storedRowCount) || r.reason === "observed_empty" && r.storedRowCount !== 0 || r.reason === "observed_rows" && r.storedRowCount === 0 || r.state !== "available" && ["observed_rows", "observed_empty"].includes(r.reason)) return invalid();
  if (r.state === "available" && r.storedRowCount > 0 && (monitorObservedAtUtc === null || utcMicroseconds(monitorObservedAtUtc) > utcMicroseconds(h.cutoffUtc) || abs(utcMicroseconds(monitorObservedAtUtc) - utcMicroseconds(completedAtUtc)) > 300000000n)) return invalid();
  return { ...r, runId: r.runId.toLowerCase(), completedAtUtc, monitorObservedAtUtc, minimumLostItems } as unknown as ReplicationReceipt;
}
function continuation(h: ReplicationHeader, prior: readonly ReplicationHeader[], cursor?: string) {
  if (Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!replicationCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || h.nextCursor === cursor) || prior.some(p => p.nextCursor !== null && p.nextCursor === h.nextCursor) || prior.length && (prior[0]!.readEpoch !== h.readEpoch || utcMicroseconds(prior[0]!.cutoffUtc) !== utcMicroseconds(h.cutoffUtc) || prior[0]!.state !== h.state || prior[0]!.reason !== h.reason)) return invalid();
}
export function parseReplicationReceiptPage(value: unknown, binding: ReplicationWindowBinding, prior: readonly ReplicationReceiptPage[] = [], cursor?: string): ReplicationReceiptPage {
  const { row, common } = header(value, binding, "coverage|items"), cov = exact(row.coverage, "runCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|emptyRunCount|retainedRowCount|unboundSourceRowCount|hasVisibilityGap");
  for (const key of ["runCount", "completeRunCount", "gapRunCount", "failedRunCount", "lostRunCount", "missingPayloadRunCount", "emptyRunCount", "retainedRowCount", "unboundSourceRowCount"]) if (!integer(cov[key], 0, ["retainedRowCount", "unboundSourceRowCount"].includes(key) ? 250000 : 20480)) return invalid();
  const coverage = cov as unknown as ReplicationCoverage;
  if (typeof coverage.hasVisibilityGap !== "boolean" || coverage.completeRunCount + coverage.gapRunCount !== coverage.runCount || coverage.failedRunCount > coverage.gapRunCount || coverage.lostRunCount > coverage.gapRunCount || coverage.missingPayloadRunCount > coverage.gapRunCount || coverage.emptyRunCount > coverage.completeRunCount || coverage.retainedRowCount + coverage.unboundSourceRowCount > 250000 || (coverage.gapRunCount > 0 || coverage.unboundSourceRowCount > 0) && !coverage.hasVisibilityGap) return invalid();
  continuation(common, prior, cursor);
  const expectedState = coverage.runCount === 0 ? "unavailable" : coverage.hasVisibilityGap ? "partial" : "available";
  const expectedReason = coverage.unboundSourceRowCount > 0 ? "source_unbound" : coverage.runCount === 0 ? "no_completed_runs" : coverage.hasVisibilityGap ? "visibility_gaps" : "retained_window";
  if (!Array.isArray(row.items) || row.items.length > 100 || common.state !== expectedState || common.reason !== expectedReason && !(coverage.unboundSourceRowCount === 0 && coverage.runCount > 0 && common.reason === "revision_boundary" && coverage.hasVisibilityGap) || prior.length && JSON.stringify(prior[0]!.coverage) !== JSON.stringify(coverage)) return invalid();
  const items = row.items.map(v => receipt(v, common)), all = [...prior.flatMap(p => p.items), ...items], seen = new Set<string>();
  let tail: ReplicationReceipt | undefined;
  for (const item of all) { if (seen.has(item.runId) || tail && (utcMicroseconds(item.completedAtUtc) > utcMicroseconds(tail.completedAtUtc) || utcMicroseconds(item.completedAtUtc) === utcMicroseconds(tail.completedAtUtc) && item.runId >= tail.runId)) return invalid(); seen.add(item.runId); tail = item; }
  const counts = { runCount: all.length, completeRunCount: all.filter(r => r.state === "available").length, gapRunCount: all.filter(r => r.state !== "available").length, failedRunCount: all.filter(r => !["succeeded", "partial"].includes(r.outcome)).length, lostRunCount: all.filter(r => r.lossKind !== null && r.lossKind !== "none").length, missingPayloadRunCount: all.filter(r => r.expectedRowCount > r.storedRowCount).length, emptyRunCount: all.filter(r => r.reason === "observed_empty").length, retainedRowCount: all.reduce((n, r) => n + r.storedRowCount, 0) };
  if (Object.entries(counts).some(([k, n]) => n > coverage[k as keyof typeof counts] || common.nextCursor === null && n !== coverage[k as keyof typeof counts]) || common.nextCursor !== null && (items.length !== 100 || all.length >= coverage.runCount)) return invalid();
  return { ...common, coverage, items };
}
export function parseReplicationSnapshotPage(value: unknown, binding: ReplicationWindowBinding, selected: ReplicationSelection, prior: readonly ReplicationSnapshotPage[] = [], cursor?: string): ReplicationSnapshotPage {
  const { row, common } = header(value, binding, "runId|sampleTargetRevision|receipt|items"); continuation(common, prior, cursor);
  if (!validReplicationSelection(row) || row.runId.toLowerCase() !== selected.runId.toLowerCase() || row.sampleTargetRevision !== selected.sampleTargetRevision || !Array.isArray(row.items) || row.items.length > 100) return invalid();
  const received = row.receipt === null ? null : receipt(row.receipt, common);
  if (received && (replicationSelectionIdentity(received) !== replicationSelectionIdentity(selected) || common.state !== received.state || common.reason !== received.reason) || !received && (row.items.length || common.nextCursor !== null || common.state !== "unavailable" || !["snapshot_not_retained", "snapshot_outside_window"].includes(common.reason)) || prior.length && JSON.stringify(prior[0]!.receipt) !== JSON.stringify(received)) return invalid();
  const items = row.items.map(v => {
    const r = exact(v, "rowIdentity|subscriptionFingerprint|monitorObservedAtUtc|collectedAtUtc|role|reportedStatus|pendingCommands|latencySeconds|sourceCoverage|stateAvailable|topology|ratePerSecond|lastSuccessAgeSeconds");
    if (!hash(r.rowIdentity) || r.subscriptionFingerprint !== null && !hash(r.subscriptionFingerprint) || !token(r.role, ["unknown", "publisher", "subscriber", "distributor"]) || !token(r.reportedStatus, ["unknown", "healthy", "warning", "failed", "initializing", "disabled"]) || !token(r.sourceCoverage, ["complete", "local_summary", "visibility_gap"]) || typeof r.stateAvailable !== "boolean" || r.topology !== null || r.ratePerSecond !== null || r.lastSuccessAgeSeconds !== null || r.latencySeconds !== null && (typeof r.latencySeconds !== "number" || !Number.isFinite(r.latencySeconds) || r.latencySeconds < 0 || r.latencySeconds > 86400)) return invalid();
    const item = { ...r, pendingCommands: commands(r.pendingCommands), monitorObservedAtUtc: replicationUtc(r.monitorObservedAtUtc), collectedAtUtc: replicationUtc(r.collectedAtUtc) } as unknown as ReplicationRow;
    if (utcMicroseconds(item.collectedAtUtc) > utcMicroseconds(common.cutoffUtc) || received && received.monitorObservedAtUtc !== null && utcMicroseconds(item.monitorObservedAtUtc) !== utcMicroseconds(received.monitorObservedAtUtc)) return invalid();
    if (received?.state === "available" && (item.sourceCoverage !== "complete" || utcMicroseconds(item.monitorObservedAtUtc) > utcMicroseconds(common.cutoffUtc) || abs(utcMicroseconds(item.monitorObservedAtUtc) - utcMicroseconds(item.collectedAtUtc)) > 300000000n)) return invalid();
    return item;
  });
  const all = [...prior.flatMap(p => p.items), ...items];
  if (all.some((r, i) => i > 0 && r.rowIdentity <= all[i - 1]!.rowIdentity) || all.length > (received?.storedRowCount ?? 0) || common.nextCursor === null && all.length !== (received?.storedRowCount ?? 0) || common.nextCursor !== null && (items.length !== 100 || all.length >= (received?.storedRowCount ?? 0))) return invalid();
  return { ...common, runId: selected.runId.toLowerCase(), sampleTargetRevision: selected.sampleTargetRevision, receipt: received, items };
}
