import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface TempDbSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface TempDbWindowBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface TempDbCoverage { readonly runCount: number; readonly completeRunCount: number; readonly gapRunCount: number; readonly failedRunCount: number; readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly emptyRunCount: number; readonly retainedFileCount: number; readonly hasVisibilityGap: boolean }
export interface TempDbReceipt extends TempDbSelection { readonly completedAtUtc: string; readonly outcome: string; readonly lossKind: string | null; readonly minimumLostItems: number | null; readonly expectedFileCount: number | null; readonly storedFileCount: number; readonly summaryPresent: boolean; readonly observedAtUtc: string | null; readonly state: string; readonly reason: string }
export interface TempDbHeader extends TempDbWindowBinding { readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string; readonly nextCursor: string | null }
export interface TempDbReceiptPage extends TempDbHeader { readonly coverage: TempDbCoverage; readonly items: readonly TempDbReceipt[] }
export interface TempDbSummary { readonly observedAtUtc: string; readonly sourceState: number; readonly totalBytes: string | null; readonly usedBytes: string | null; readonly logTotalBytes: string | null; readonly logUsedBytes: string | null; readonly truncated: boolean }
export interface TempDbFile { readonly fileId: number; readonly sizeBytes: string; readonly usedBytes: string; readonly freeBytes: string; readonly sourceState: number; readonly observedAtUtc: string }
export interface TempDbSnapshotPage extends TempDbHeader, TempDbSelection { readonly receipt: TempDbReceipt | null; readonly summary: TempDbSummary | null; readonly items: readonly TempDbFile[] }
export const invalidTempDbHistory = "TempDB history returned invalid or mismatched evidence. Retry this exact read or refresh its source.";
const invalid = (): never => { throw new Error(invalidTempDbHistory); };
const integer = (v: unknown, min: number, max: number): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= min && v <= max;
const token = (v: unknown, allowed: readonly string[]): v is string => typeof v === "string" && allowed.includes(v);
function exact(v: unknown, keys: string): Record<string, unknown> {
  if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return v as Record<string, unknown>;
}
export function tempDbUtc(v: unknown): string { if (typeof v !== "string") return invalid(); try { return canonicalUtcTimestamp(v.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); } }
export function tempDbWindow(fromUtc: string, toUtc: string) {
  const from = tempDbUtc(fromUtc), to = tempDbUtc(toUtc), duration = utcMicroseconds(to) - utcMicroseconds(from);
  if (duration <= 0n || duration > 7n * 86400n * 1000000n) throw new Error("TempDB history supports a shared UTC window of up to seven days. Choose a shorter window.");
  return { fromUtc: from, toUtc: to };
}
export function validTempDbSelection(v: unknown): v is TempDbSelection { if (!v || typeof v !== "object") return false; const r = v as TempDbSelection; return notificationId(r.runId) && integer(r.sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER); }
export const tempDbSelectionIdentity = (v: TempDbSelection | null | undefined) => v === null ? "invalid" : v ? JSON.stringify([v.runId.toLowerCase(), v.sampleTargetRevision]) : "default";
export function tempDbContextIdentity(v: TempDbWindowBinding | undefined) { if (!v) return "unavailable"; try { const w = tempDbWindow(v.fromUtc, v.toUtc); return JSON.stringify([v.targetId.toLowerCase(), v.targetRevision, utcMicroseconds(w.fromUtc).toString(), utcMicroseconds(w.toUtc).toString()]); } catch { return JSON.stringify(v); } }
export function tempDbCursor(v: unknown): v is string { if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false; try { return btoa(atob(v)) === v; } catch { return false; } }
function bytes(v: unknown): string { if (typeof v !== "string" || !/^(?:0|[1-9]\d{0,18})$/u.test(v) || BigInt(v) > 9223372036854775807n) return invalid(); return v; }
const optionalBytes = (v: unknown) => v === null ? null : bytes(v);
const optionalCount = (v: unknown, max: number) => v === null ? null : integer(v, 0, max) ? v : invalid();
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection"];
const reasons = ["observed_files", "observed_empty", "collection_failed", "collection_loss", "collection_gap", "source_schema_unsupported", "payload_missing", "stored_count_mismatch", "source_clock_skew", "source_partial"];
function header(value: unknown, binding: TempDbWindowBinding, extra: string) {
  const r = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|nextCursor|" + extra);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) || !notificationId(r.targetId) || r.targetId.toLowerCase() !== binding.targetId.toLowerCase() || r.targetRevision !== binding.targetRevision || typeof r.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(r.readEpoch) || !token(r.state, ["available", "partial", "unavailable"]) || r.nextCursor !== null && !tempDbCursor(r.nextCursor)) return invalid();
  const w = tempDbWindow(binding.fromUtc, binding.toUtc), fromUtc = tempDbUtc(r.fromUtc), toUtc = tempDbUtc(r.toUtc), cutoffUtc = tempDbUtc(r.cutoffUtc), repositoryTimeUtc = tempDbUtc(r.repositoryTimeUtc);
  if (utcMicroseconds(fromUtc) !== utcMicroseconds(w.fromUtc) || utcMicroseconds(toUtc) !== utcMicroseconds(w.toUtc) || utcMicroseconds(cutoffUtc) > utcMicroseconds(repositoryTimeUtc)) return invalid();
  return { row: r, common: { targetId: r.targetId.toLowerCase(), targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, repositoryTimeUtc, readEpoch: r.readEpoch, state: r.state, reason: String(r.reason), nextCursor: r.nextCursor } as TempDbHeader };
}
function receipt(value: unknown, h: TempDbHeader): TempDbReceipt {
  const r = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|lossKind|minimumLostItems|expectedFileCount|storedFileCount|summaryPresent|observedAtUtc|state|reason");
  if (!validTempDbSelection(r) || r.sampleTargetRevision > h.targetRevision || !token(r.outcome, outcomes) || r.lossKind !== null && !token(r.lossKind, losses) || !integer(r.storedFileCount, 0, 10000) || typeof r.summaryPresent !== "boolean" || !token(r.state, ["available", "partial", "unavailable"]) || !token(r.reason, reasons)) return invalid();
  const completedAtUtc = tempDbUtc(r.completedAtUtc), observedAtUtc = r.observedAtUtc === null ? null : tempDbUtc(r.observedAtUtc), expectedFileCount = optionalCount(r.expectedFileCount, 10000), minimumLostItems = optionalCount(r.minimumLostItems, 2147483647);
  if (utcMicroseconds(completedAtUtc) < utcMicroseconds(h.fromUtc) || utcMicroseconds(completedAtUtc) >= utcMicroseconds(h.toUtc) || utcMicroseconds(completedAtUtc) > utcMicroseconds(h.cutoffUtc) || r.summaryPresent !== (observedAtUtc !== null) || r.state === "available" && (!["observed_files", "observed_empty"].includes(r.reason) || r.outcome !== "succeeded" || r.lossKind !== "none" || !r.summaryPresent || expectedFileCount !== r.storedFileCount) || r.reason === "observed_empty" && r.storedFileCount !== 0 || r.reason === "observed_files" && r.storedFileCount === 0) return invalid();
  if (r.state !== "available" && ["observed_files", "observed_empty"].includes(r.reason) || r.state === "available" && (minimumLostItems !== 0 || observedAtUtc === null || utcMicroseconds(observedAtUtc) > utcMicroseconds(h.cutoffUtc) || abs(utcMicroseconds(observedAtUtc) - utcMicroseconds(completedAtUtc)) > 300000000n)) return invalid();
  return { ...r, runId: r.runId.toLowerCase(), completedAtUtc, observedAtUtc, expectedFileCount, minimumLostItems } as unknown as TempDbReceipt;
}
const abs = (v: bigint) => v < 0n ? -v : v;
function continuation(h: TempDbHeader, prior: readonly TempDbHeader[], cursor?: string) {
  if (Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!tempDbCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || h.nextCursor === cursor) || prior.some(p => p.nextCursor !== null && p.nextCursor === h.nextCursor) || prior.length && (prior[0]!.readEpoch !== h.readEpoch || utcMicroseconds(prior[0]!.cutoffUtc) !== utcMicroseconds(h.cutoffUtc) || prior[0]!.state !== h.state || prior[0]!.reason !== h.reason)) return invalid();
}
export function parseTempDbReceiptPage(value: unknown, binding: TempDbWindowBinding, prior: readonly TempDbReceiptPage[] = [], cursor?: string): TempDbReceiptPage {
  const { row, common } = header(value, binding, "coverage|items"), cov = exact(row.coverage, "runCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|emptyRunCount|retainedFileCount|hasVisibilityGap");
  for (const key of ["runCount", "completeRunCount", "gapRunCount", "failedRunCount", "lostRunCount", "missingPayloadRunCount", "emptyRunCount", "retainedFileCount"]) if (!integer(cov[key], 0, key === "retainedFileCount" ? 250000 : 10000)) return invalid();
  const coverage = cov as unknown as TempDbCoverage;
  if (typeof coverage.hasVisibilityGap !== "boolean" || coverage.completeRunCount + coverage.gapRunCount !== coverage.runCount || coverage.failedRunCount > coverage.gapRunCount || coverage.lostRunCount > coverage.gapRunCount || coverage.missingPayloadRunCount > coverage.gapRunCount || coverage.emptyRunCount > coverage.completeRunCount || coverage.gapRunCount > 0 && !coverage.hasVisibilityGap) return invalid();
  continuation(common, prior, cursor);
  if (!Array.isArray(row.items) || row.items.length > 100 || common.state !== (coverage.runCount === 0 ? "unavailable" : coverage.hasVisibilityGap ? "partial" : "available") || common.reason !== (coverage.runCount === 0 ? "no_completed_runs" : coverage.hasVisibilityGap ? "visibility_gaps" : "retained_window") && !(common.reason === "revision_boundary" && coverage.hasVisibilityGap) || prior.length && JSON.stringify(prior[0]!.coverage) !== JSON.stringify(coverage)) return invalid();
  const items = row.items.map(v => receipt(v, common)), all = [...prior.flatMap(p => p.items), ...items], seen = new Set<string>();
  let tail: TempDbReceipt | undefined;
  for (const item of all) { if (seen.has(item.runId) || tail && (utcMicroseconds(item.completedAtUtc) > utcMicroseconds(tail.completedAtUtc) || utcMicroseconds(item.completedAtUtc) === utcMicroseconds(tail.completedAtUtc) && item.runId >= tail.runId)) return invalid(); seen.add(item.runId); tail = item; }
  const counts = { runCount: all.length, completeRunCount: all.filter(r => r.state === "available").length, gapRunCount: all.filter(r => r.state !== "available").length, failedRunCount: all.filter(r => !["succeeded", "partial"].includes(r.outcome)).length, lostRunCount: all.filter(r => r.lossKind !== null && r.lossKind !== "none").length, missingPayloadRunCount: all.filter(r => r.expectedFileCount !== null && r.expectedFileCount > r.storedFileCount || ["succeeded", "partial"].includes(r.outcome) && !r.summaryPresent).length, emptyRunCount: all.filter(r => r.reason === "observed_empty").length, retainedFileCount: all.reduce((n, r) => n + r.storedFileCount, 0) };
  if (Object.entries(counts).some(([k, n]) => n > coverage[k as keyof typeof counts] || common.nextCursor === null && n !== coverage[k as keyof typeof counts]) || common.nextCursor !== null && (items.length !== 100 || all.length >= coverage.runCount)) return invalid();
  return { ...common, coverage, items };
}
export function parseTempDbSnapshotPage(value: unknown, binding: TempDbWindowBinding, selected: TempDbSelection, prior: readonly TempDbSnapshotPage[] = [], cursor?: string): TempDbSnapshotPage {
  const { row, common } = header(value, binding, "runId|sampleTargetRevision|receipt|summary|items"); continuation(common, prior, cursor);
  if (!validTempDbSelection(row) || row.runId.toLowerCase() !== selected.runId.toLowerCase() || row.sampleTargetRevision !== selected.sampleTargetRevision || !Array.isArray(row.items) || row.items.length > 100) return invalid();
  const received = row.receipt === null ? null : receipt(row.receipt, common);
  if (received && (tempDbSelectionIdentity(received) !== tempDbSelectionIdentity(selected) || common.state !== received.state || common.reason !== received.reason) || !received && (row.summary !== null || row.items.length || common.nextCursor !== null || common.state !== "unavailable" || !["snapshot_not_retained", "snapshot_outside_window"].includes(common.reason))) return invalid();
  let summary: TempDbSummary | null = null;
  if (row.summary !== null) { const s = exact(row.summary, "observedAtUtc|sourceState|totalBytes|usedBytes|logTotalBytes|logUsedBytes|truncated"); if (!integer(s.sourceState, 1, 6) || typeof s.truncated !== "boolean") return invalid(); summary = { observedAtUtc: tempDbUtc(s.observedAtUtc), sourceState: s.sourceState, totalBytes: optionalBytes(s.totalBytes), usedBytes: optionalBytes(s.usedBytes), logTotalBytes: optionalBytes(s.logTotalBytes), logUsedBytes: optionalBytes(s.logUsedBytes), truncated: s.truncated }; }
  if (received && (received.summaryPresent !== (summary !== null) || summary && utcMicroseconds(summary.observedAtUtc) !== utcMicroseconds(received.observedAtUtc!)) || prior.length && (JSON.stringify(prior[0]!.receipt) !== JSON.stringify(received) || JSON.stringify(prior[0]!.summary) !== JSON.stringify(summary))) return invalid();
  if (received?.state === "available" && summary && (summary.truncated || summary.sourceState !== 1 && !(summary.sourceState === 6 && received.storedFileCount === 0))) return invalid();
  const items = row.items.map(v => { const f = exact(v, "fileId|sizeBytes|usedBytes|freeBytes|sourceState|observedAtUtc"); if (!integer(f.fileId, 1, 65535) || !integer(f.sourceState, 1, 4)) return invalid(); const item = { fileId: f.fileId, sizeBytes: bytes(f.sizeBytes), usedBytes: bytes(f.usedBytes), freeBytes: bytes(f.freeBytes), sourceState: f.sourceState, observedAtUtc: tempDbUtc(f.observedAtUtc) }; if (summary && utcMicroseconds(item.observedAtUtc) !== utcMicroseconds(summary.observedAtUtc)) return invalid(); return item; });
  const all = [...prior.flatMap(p => p.items), ...items];
  if (all.some((f, i) => i > 0 && f.fileId <= all[i - 1]!.fileId) || all.length > (received?.storedFileCount ?? 0) || common.nextCursor === null && all.length !== (received?.storedFileCount ?? 0) || common.nextCursor !== null && (items.length !== 100 || all.length >= (received?.storedFileCount ?? 0))) return invalid();
  return { ...common, runId: selected.runId.toLowerCase(), sampleTargetRevision: selected.sampleTargetRevision, receipt: received, summary, items };
}
