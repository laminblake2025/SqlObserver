import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface VolumeHistoryIdentity { readonly sampleTargetRevision: number; readonly volumeKey: string }
export type VolumeIdentityKind = "volume_id" | "mount_point" | "file_scoped_unknown";
export interface VolumeHistoryCoverage { readonly runCount: number; readonly completeRunCount: number; readonly gapRunCount: number;
  readonly retainedObservationCount: number; readonly retentionDays: number | null; readonly hasVisibilityGap: boolean }
export interface VolumeHistoryCatalogItem extends VolumeHistoryIdentity {
  readonly identityKind: VolumeIdentityKind; readonly observationCount: number; readonly firstCompletedAtUtc: string; readonly lastCompletedAtUtc: string;
  readonly firstTotalBytes: string | null; readonly firstAvailableBytes: string | null; readonly lastTotalBytes: string | null; readonly lastAvailableBytes: string | null;
  readonly comparisonState: string; readonly availableBytesDelta: string | null; readonly totalBytesDelta: string | null;
}
export interface VolumeHistorySample { readonly runId: string; readonly completedAtUtc: string; readonly observedAtUtc: string | null;
  readonly collectedAtUtc: string | null; readonly outcome: string; readonly lossKind: string | null; readonly minimumLostItems: number | null;
  readonly state: string; readonly reason: string; readonly mappedFileCount: number | null; readonly totalBytes: string | null; readonly availableBytes: string | null }
export interface VolumeHistoryHeader { readonly instanceId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string;
  readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string;
  readonly coverage: VolumeHistoryCoverage; readonly nextCursor: string | null }
export interface VolumeHistoryCatalogPage extends VolumeHistoryHeader { readonly items: readonly VolumeHistoryCatalogItem[] }
export interface VolumeHistoryPage extends VolumeHistoryHeader, VolumeHistoryIdentity { readonly identityKind: VolumeIdentityKind | null; readonly items: readonly VolumeHistorySample[] }
export type VolumeHistoryReadPage = VolumeHistoryCatalogPage | VolumeHistoryPage;
export interface VolumeHistoryBinding { readonly instanceId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string;
  readonly selected?: VolumeHistoryIdentity }
export const invalidVolumeHistory = "Volume history returned an invalid or mismatched response. Refresh this history read to retry.";
const invalid = (): never => { throw new Error(invalidVolumeHistory); };
export const volumeHistoryKey = (v: unknown): v is string => typeof v === "string" && /^[a-f0-9]{64}$/u.test(v);
const integer = (v: unknown, min: number, max: number): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= min && v <= max;
export function validVolumeHistoryIdentity(v: unknown): v is VolumeHistoryIdentity {
  return Boolean(v && typeof v === "object" && integer((v as VolumeHistoryIdentity).sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER) && volumeHistoryKey((v as VolumeHistoryIdentity).volumeKey));
}
const kinds = ["volume_id", "mount_point", "file_scoped_unknown"];
const states = ["available", "partial", "unavailable"];
const pageReasons = ["retained_window", "visibility_gaps", "no_completed_runs", "retained_evidence_unavailable", "revision_boundary"];
const sampleReasons = ["observed_capacity", "collection_gap", "collection_failed", "collection_loss", "stored_count_mismatch", "volume_absent", "capacity_unknown", "source_clock_skew"];
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection"];
const comparisonStates = ["eligible", "insufficient_observations", "identity_not_comparable", "visibility_gaps", "capacity_changed", "capacity_unknown"];
export function isSqlVolumeHistoryCursor(v: unknown): v is string {
  if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false;
  try { return btoa(atob(v)) === v; } catch { return false; }
}
function exact(v: unknown, keys: string): Record<string, unknown> {
  if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return v as Record<string, unknown>;
}
export function volumeHistoryUtc(v: unknown): string {
  if (typeof v !== "string") return invalid();
  try { return canonicalUtcTimestamp(v.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
const at = (v: string) => utcMicroseconds(v);
export function volumeHistoryWindow(fromUtc: string, toUtc: string) {
  const from = volumeHistoryUtc(fromUtc), to = volumeHistoryUtc(toUtc), duration = at(to) - at(from);
  if (duration <= 0n || duration > 31n * 86400n * 1000000n) return invalid();
  return { fromUtc: from, toUtc: to };
}
export function previousVolumeHistoryWindow(fromUtc: string, toUtc: string) {
  const window = volumeHistoryWindow(fromUtc, toUtc), value = at(window.fromUtc) * 2n - at(window.toUtc);
  const remainder = (value % 1000000n + 1000000n) % 1000000n;
  const base = new Date(Number((value - remainder) / 1000n)).toISOString().slice(0, 19);
  return { fromUtc: volumeHistoryUtc(base + "." + remainder.toString().padStart(6, "0") + "Z"), toUtc: window.fromUtc };
}
function bytes(v: unknown, signed = false): string | null {
  if (v === null) return null;
  if (typeof v !== "string" || !(signed ? /^-?(?:0|[1-9]\d{0,18})$/u : /^(?:0|[1-9]\d{0,18})$/u).test(v) || v === "-0") return invalid();
  const n = BigInt(v); if (n > 9223372036854775807n || n < -9223372036854775808n) return invalid(); return v;
}
function capacity(total: unknown, available: unknown) {
  const t = bytes(total), a = bytes(available); if ((t === null) !== (a === null) || t !== null && (BigInt(t) <= 0n || BigInt(a!) > BigInt(t))) return invalid();
  return { total: t, available: a };
}
function coverage(v: unknown): VolumeHistoryCoverage {
  const r = exact(v, "runCount|completeRunCount|gapRunCount|retainedObservationCount|retentionDays|hasVisibilityGap");
  if (!integer(r.runCount, 0, 10000) || !integer(r.completeRunCount, 0, 10000) || !integer(r.gapRunCount, 0, 10000) || r.completeRunCount + r.gapRunCount !== r.runCount
    || !integer(r.retainedObservationCount, 0, 250000) || r.retentionDays !== null && !integer(r.retentionDays, 1, Number.MAX_SAFE_INTEGER)
    || typeof r.hasVisibilityGap !== "boolean" || r.gapRunCount > 0 && !r.hasVisibilityGap) return invalid();
  return { runCount: r.runCount, completeRunCount: r.completeRunCount, gapRunCount: r.gapRunCount, retainedObservationCount: r.retainedObservationCount,
    retentionDays: r.retentionDays as number | null, hasVisibilityGap: r.hasVisibilityGap };
}
export function parseVolumeHistoryPage(value: unknown, binding: VolumeHistoryBinding, prior: readonly VolumeHistoryReadPage[] = [], cursor?: string): VolumeHistoryReadPage {
  if (!notificationId(binding.instanceId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) || binding.selected && !validVolumeHistoryIdentity(binding.selected)) return invalid();
  const window = volumeHistoryWindow(binding.fromUtc, binding.toUtc), selected = binding.selected;
  const r = exact(value, "instanceId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|coverage|items|nextCursor" + (selected ? "|sampleTargetRevision|volumeKey|identityKind" : ""));
  const fromUtc = volumeHistoryUtc(r.fromUtc), toUtc = volumeHistoryUtc(r.toUtc), cutoffUtc = volumeHistoryUtc(r.cutoffUtc), repositoryTimeUtc = volumeHistoryUtc(r.repositoryTimeUtc);
  if (!notificationId(r.instanceId) || r.instanceId.toLowerCase() !== binding.instanceId.toLowerCase() || r.targetRevision !== binding.targetRevision
    || at(fromUtc) !== at(window.fromUtc) || at(toUtc) !== at(window.toUtc) || !volumeHistoryKey(r.readEpoch) || at(cutoffUtc) > at(repositoryTimeUtc)
    || !states.includes(String(r.state)) || !pageReasons.includes(String(r.reason)) || !Array.isArray(r.items) || r.items.length > (selected ? 250 : 25)
    || r.nextCursor !== null && (!isSqlVolumeHistoryCursor(r.nextCursor) || prior.some(page => page.nextCursor === r.nextCursor)) || cursor !== undefined && (!isSqlVolumeHistoryCursor(cursor) || cursor === r.nextCursor)
    || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && prior.at(-1)?.nextCursor !== cursor
    || prior.length && (prior[0]!.readEpoch !== r.readEpoch || at(prior[0]!.cutoffUtc) !== at(cutoffUtc) || JSON.stringify(prior[0]!.coverage) !== JSON.stringify(coverage(r.coverage)))) return invalid();
  const common: VolumeHistoryHeader = { instanceId: r.instanceId, targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, readEpoch: r.readEpoch,
    repositoryTimeUtc, state: r.state as string, reason: r.reason as string, coverage: coverage(r.coverage), nextCursor: r.nextCursor as string | null };
  if (selected && common.coverage.retainedObservationCount > common.coverage.runCount) return invalid();
  const expectedState = common.coverage.runCount === 0 || common.coverage.retainedObservationCount === 0 ? "unavailable" : common.coverage.hasVisibilityGap ? "partial" : "available";
  const expectedReason = common.coverage.runCount === 0 ? "no_completed_runs" : common.coverage.retainedObservationCount === 0 ? "retained_evidence_unavailable" : common.coverage.hasVisibilityGap ? "visibility_gaps" : "retained_window";
  if (common.state !== expectedState || (common.reason === "revision_boundary" ? !common.coverage.hasVisibilityGap : common.reason !== expectedReason)
    || common.nextCursor !== null && r.items.length !== (selected ? 250 : 25)) return invalid();
  if (selected) {
    if (r.sampleTargetRevision !== selected.sampleTargetRevision || r.volumeKey !== selected.volumeKey || r.identityKind !== null && !kinds.includes(String(r.identityKind))
      || prior.length && (prior[0] as VolumeHistoryPage).identityKind !== r.identityKind) return invalid();
    const seen = new Set(prior.flatMap(p => (p as VolumeHistoryPage).items.map(item => item.runId.toLowerCase())));
    let tail = (prior.at(-1) as VolumeHistoryPage | undefined)?.items.at(-1);
    const items = r.items.map(v => {
      const row = exact(v, "runId|completedAtUtc|observedAtUtc|collectedAtUtc|outcome|lossKind|minimumLostItems|state|reason|mappedFileCount|totalBytes|availableBytes");
      const completedAtUtc = volumeHistoryUtc(row.completedAtUtc), observedAtUtc = row.observedAtUtc === null ? null : volumeHistoryUtc(row.observedAtUtc), collectedAtUtc = row.collectedAtUtc === null ? null : volumeHistoryUtc(row.collectedAtUtc);
      if (!notificationId(row.runId) || seen.has(row.runId.toLowerCase()) || at(completedAtUtc) < at(fromUtc) || at(completedAtUtc) >= at(toUtc) || at(completedAtUtc) > at(cutoffUtc)
        || tail && (at(completedAtUtc) < at(tail.completedAtUtc) || at(completedAtUtc) === at(tail.completedAtUtc) && row.runId.toLowerCase() <= tail.runId.toLowerCase())
        || !outcomes.includes(String(row.outcome)) || !states.includes(String(row.state)) || !sampleReasons.includes(String(row.reason)) || row.lossKind !== null && !losses.includes(String(row.lossKind))
        || row.minimumLostItems !== null && !integer(row.minimumLostItems, 0, 2147483647) || row.mappedFileCount !== null && !integer(row.mappedFileCount, 1, 1000)
        || collectedAtUtc !== null && at(collectedAtUtc) > at(cutoffUtc)) return invalid();
      const cap = capacity(row.totalBytes, row.availableBytes);
      if ((observedAtUtc === null) !== (collectedAtUtc === null) || (observedAtUtc === null) !== (row.mappedFileCount === null) || cap.total !== null && observedAtUtc === null || observedAtUtc !== null && r.identityKind === null
        || common.state === "available" && row.state !== "available"
        || row.state === "available" && (row.outcome !== "succeeded" || row.lossKind !== "none" || row.minimumLostItems !== 0 || row.reason !== "observed_capacity" || cap.total === null || observedAtUtc === null || collectedAtUtc === null
          || at(observedAtUtc) > at(cutoffUtc) || (at(observedAtUtc) - at(collectedAtUtc) > 300000000n || at(collectedAtUtc) - at(observedAtUtc) > 300000000n))
        || row.reason === "volume_absent" && (cap.total !== null || observedAtUtc !== null || collectedAtUtc !== null || row.mappedFileCount !== null)) return invalid();
      const item: VolumeHistorySample = { ...(row as unknown as VolumeHistorySample), completedAtUtc, observedAtUtc, collectedAtUtc, totalBytes: cap.total, availableBytes: cap.available };
      seen.add(item.runId.toLowerCase()); tail = item; return item;
    });
    if (common.nextCursor === null) {
      const received = [...prior.flatMap(page => (page as VolumeHistoryPage).items), ...items];
      if (received.length !== common.coverage.runCount || received.filter(item => item.state === "available").length !== common.coverage.completeRunCount) return invalid();
    }
    return { ...common, ...selected, identityKind: r.identityKind as VolumeIdentityKind | null, items };
  }
  const seen = new Set(prior.flatMap(p => (p as VolumeHistoryCatalogPage).items.map(identityKey)));
  let tail = (prior.at(-1) as VolumeHistoryCatalogPage | undefined)?.items.at(-1);
  const items = r.items.map(v => {
    const row = exact(v, "sampleTargetRevision|volumeKey|identityKind|observationCount|firstCompletedAtUtc|lastCompletedAtUtc|firstTotalBytes|firstAvailableBytes|lastTotalBytes|lastAvailableBytes|comparisonState|availableBytesDelta|totalBytesDelta");
    if (!validVolumeHistoryIdentity(row) || !kinds.includes(String(row.identityKind)) || !integer(row.observationCount, 1, common.coverage.runCount) || !comparisonStates.includes(String(row.comparisonState))
      || seen.has(identityKey(row)) || tail && compareIdentity(tail, row) >= 0) return invalid();
    const firstCompletedAtUtc = volumeHistoryUtc(row.firstCompletedAtUtc), lastCompletedAtUtc = volumeHistoryUtc(row.lastCompletedAtUtc);
    if (at(firstCompletedAtUtc) < at(fromUtc) || at(lastCompletedAtUtc) < at(firstCompletedAtUtc) || at(lastCompletedAtUtc) >= at(toUtc) || at(lastCompletedAtUtc) > at(cutoffUtc)) return invalid();
    const first = capacity(row.firstTotalBytes, row.firstAvailableBytes), last = capacity(row.lastTotalBytes, row.lastAvailableBytes);
    const item: VolumeHistoryCatalogItem = { ...(row as unknown as VolumeHistoryCatalogItem), firstCompletedAtUtc, lastCompletedAtUtc,
      firstTotalBytes: first.total, firstAvailableBytes: first.available, lastTotalBytes: last.total, lastAvailableBytes: last.available,
      availableBytesDelta: bytes(row.availableBytesDelta, true), totalBytesDelta: bytes(row.totalBytesDelta, true) };
    if (item.comparisonState === "eligible" ? common.coverage.hasVisibilityGap || item.identityKind !== "volume_id" || item.observationCount < 2 || first.total === null || last.total === null || first.total !== last.total
      || item.totalBytesDelta !== "0" || item.availableBytesDelta !== (BigInt(last.available!) - BigInt(first.available!)).toString()
      : item.availableBytesDelta !== null || item.totalBytesDelta !== null) return invalid();
    seen.add(identityKey(item)); tail = item; return item;
  });
  if (common.nextCursor === null && [...prior.flatMap(page => (page as VolumeHistoryCatalogPage).items), ...items].reduce((sum,item) => sum + item.observationCount,0) !== common.coverage.retainedObservationCount) return invalid();
  return { ...common, items };
}
export function identityKey(identity: VolumeHistoryIdentity) { return identity.sampleTargetRevision + ":" + identity.volumeKey; }
function compareIdentity(a: VolumeHistoryIdentity, b: VolumeHistoryIdentity) { return a.sampleTargetRevision - b.sampleTargetRevision || a.volumeKey.localeCompare(b.volumeKey); }
export function comparisonEligibility(current: readonly VolumeHistoryPage[], previous: readonly VolumeHistoryPage[]): string | undefined {
  if (!current.length || !previous.length || current.at(-1)!.nextCursor || previous.at(-1)!.nextCursor) return "Read every page in both windows before comparison.";
  const c = current[0]!, p = previous[0]!;
  if (c.instanceId !== p.instanceId || c.targetRevision !== p.targetRevision || c.sampleTargetRevision !== p.sampleTargetRevision || c.volumeKey !== p.volumeKey || c.identityKind !== "volume_id" || p.identityKind !== "volume_id") return "Comparison requires the same revision and volume-ID source key; mount points and unknown file identities do not establish continuity.";
  if (c.coverage.hasVisibilityGap || p.coverage.hasVisibilityGap) return "Comparison is unavailable because a received window contains visibility gaps.";
  const rows = [...current.flatMap(page => page.items), ...previous.flatMap(page => page.items)];
  if (current.flatMap(page => page.items).length < 2 || previous.flatMap(page => page.items).length < 2) return "Comparison needs at least two complete observations in each window.";
  if (rows.some(row => row.state !== "available" || row.totalBytes === null || row.availableBytes === null || row.lossKind !== "none")) return "Comparison requires complete observations with known capacity.";
  if (new Set(rows.map(row => row.totalBytes)).size !== 1) return "Total capacity changed; these windows cannot be compared as one capacity trend.";
  return undefined;
}
/** Plot conversion is approximate; exact capacities remain decimal strings in the received table. */
export function approximateVolumeGib(bytes: string | null): number | null { return bytes === null ? null : Number(BigInt(bytes)) / 1073741824; }
export function approximateVolumeAxisLabel(value: number): string {
  return Math.abs(value) >= 1000 ? value.toExponential(1).replace("e+", "e") : value.toLocaleString(undefined, { maximumFractionDigits: 2, useGrouping: false });
}
/** Convert only the bounded relative position, preserving sub-millisecond window geometry. */
export function volumeHistoryCompletionFraction(completedAtUtc: string, fromUtc: string, toUtc: string, shift = 0n): number {
  const window = volumeHistoryWindow(fromUtc,toUtc);
  return Number(at(volumeHistoryUtc(completedAtUtc)) + shift - at(window.fromUtc)) / Number(at(window.toUtc) - at(window.fromUtc));
}
export function volumeHistorySegments(rows: readonly VolumeHistorySample[], kind: VolumeIdentityKind | null): readonly (readonly VolumeHistorySample[])[] {
  const segments: VolumeHistorySample[][] = []; let segment: VolumeHistorySample[] = [], total: string | null = null, completed: bigint | undefined;
  for (const row of rows) {
    const valid = row.state === "available" && row.reason === "observed_capacity" && row.totalBytes !== null && row.availableBytes !== null, time = at(row.completedAtUtc);
    if (!valid || kind !== "volume_id" || total !== null && row.totalBytes !== total || completed !== undefined && time - completed > 600000000n) { if (segment.length) segments.push(segment); segment = []; }
    if (valid) { segment.push(row); total = row.totalBytes; } else total = null;
    completed = time;
  }
  if (segment.length) segments.push(segment); return segments;
}
