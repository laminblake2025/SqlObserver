import { utcMicroseconds } from "../../utcTimestamp.ts";
import { planCacheIntervalTrend, cacheIntervalRateLabel } from "./planCacheIntervalTrendModel.ts";
import type { PlanCacheHistorySelection, PlanCacheIntervalHistory, QueryPerformanceMetric, QueryStoreGroupTrend } from "./queryPerformanceTypes.ts";

export interface RankingTrendRequest {
  readonly kind: "query_store" | "plan_cache";
  readonly targetId: string; readonly databaseId: number;
  readonly queryFingerprint: string; readonly planFingerprint: string;
  readonly databaseIncarnation?: string; readonly fromUtc: string; readonly toUtc: string;
}
export interface RankingTrendPoint { readonly timeUtc: string; readonly value: number; }
export interface RankingTrendValue { readonly timeUtc: string; readonly endUtc?: string; readonly exact: string | null; readonly note: string; }
export interface RankingTrendPreview {
  readonly unit: string; readonly source: string; readonly coverage: string;
  readonly segments: readonly { readonly id: string; readonly points: readonly RankingTrendPoint[] }[];
  readonly values: readonly RankingTrendValue[]; readonly precisionOmissions: number;
}
export const rankingTrendMaximumRows = 225;
export const rankingTrendConcurrency = 2;
export const rankingTrendUtc = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
const fields = { cpu: "cpuMilliseconds", duration: "durationMilliseconds", executions: "executions", logical_reads: "logicalReads", writes: "writes", rows: "rows" } as const;
const units = { cpu: "CPU ms/hour bucket", duration: "Duration ms/hour bucket", executions: "Executions/hour bucket", logical_reads: "Logical reads/hour bucket", writes: "Writes/hour bucket", rows: "Rows/hour bucket" } as const;
const hour = 3_600_000_000n;
const digest = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
const guid = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
function windowBounds(fromUtc: string, toUtc: string) {
  const from = rankingTrendUtc(fromUtc), to = rankingTrendUtc(toUtc);
  if (to <= from || to - from > 7n * 86400n * 1000000n) throw new Error("Ranking trend window is invalid.");
  return { from, to };
}
export function rankingTrendRequestKey(request: RankingTrendRequest): string {
  const { from, to } = windowBounds(request.fromUtc, request.toUtc);
  if (!guid(request.targetId) || !Number.isInteger(request.databaseId) || request.databaseId < 1 || request.databaseId > 32767 ||
      !digest(request.queryFingerprint) || !digest(request.planFingerprint) || !["query_store", "plan_cache"].includes(request.kind) ||
      request.kind === "plan_cache" && (request.databaseId < 5 || request.databaseId > 32766 || !digest(request.databaseIncarnation)))
    throw new Error("Ranking trend identity is invalid.");
  return JSON.stringify([request.kind, request.targetId.toLowerCase(), request.databaseId, request.databaseIncarnation ?? null,
    request.queryFingerprint, request.planFingerprint, from.toString(), to.toString()]);
}
export function queryStoreRankingTrendRequest(targetId: string, databaseId: number, queryFingerprint: string,
    planFingerprint: string | undefined, window: { readonly fromUtc: string; readonly toUtc: string }): RankingTrendRequest | undefined {
  if (!planFingerprint) return undefined;
  const request: RankingTrendRequest = { kind: "query_store", targetId, databaseId, queryFingerprint, planFingerprint, ...window };
  try { rankingTrendRequestKey(request); return request; } catch { return undefined; }
}
export function cacheRankingTrendRequest(selection: PlanCacheHistorySelection,
    window: { readonly fromUtc: string; readonly toUtc: string }): RankingTrendRequest | undefined {
  const request: RankingTrendRequest = { kind: "plan_cache", ...selection, ...window };
  try { rankingTrendRequestKey(request); return request; } catch { return undefined; }
}
export function rankingTrendIdentityCounts(requests: readonly (RankingTrendRequest | undefined)[]): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const request of requests) if (request) {
    const key = rankingTrendRequestKey(request); counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return counts;
}
function exactWindow(request: RankingTrendRequest, value: { readonly fromUtc: string; readonly toUtc: string }) {
  rankingTrendRequestKey(request);
  const expected = windowBounds(request.fromUtc, request.toUtc), actual = windowBounds(value.fromUtc, value.toUtc);
  if (expected.from !== actual.from || expected.to !== actual.to) throw new Error("Ranking trend response window changed.");
  return expected;
}
function safeValue(value: string | null): number | null {
  if (value === null || !/^(?:0|[1-9]\d{0,28})$/u.test(value)) return null;
  const exact = BigInt(value);
  return exact <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(exact) : null;
}
/** Hourly sums are compared only across consecutive fully comparable observed buckets. */
export function queryStoreRankingTrend(trend: QueryStoreGroupTrend, request: RankingTrendRequest,
    metric: QueryPerformanceMetric | "waits", category: number | null = null): RankingTrendPreview {
  const { from, to } = exactWindow(request, trend);
  if (request.kind !== "query_store" || metric !== "waits" && !Object.hasOwn(fields, metric) ||
      category !== null && (!Number.isInteger(category) || category < 0 || category > 31) || !Array.isArray(trend.points) || trend.points.length > 169)
    throw new Error("Query Store ranking trend is invalid.");
  const byHour = new Map<bigint, QueryStoreGroupTrend["points"][number]>();
  const firstBucket = from - (from % hour + hour) % hour;
  for (const point of trend.points) {
    const at = rankingTrendUtc(point.bucketStartUtc);
    if (at % hour !== 0n || at < firstBucket || at >= to || byHour.has(at)) throw new Error("Query Store ranking bucket is invalid.");
    byHour.set(at, point);
  }
  const segments: { id: string; points: RankingTrendPoint[] }[] = [];
  const values: RankingTrendValue[] = [];
  let active: RankingTrendPoint[] | undefined;
  let omissions = 0, comparable = 0, unavailable = 0, missing = 0;
  for (let bucket = firstBucket; bucket < to; bucket += hour) {
    const point = byHour.get(bucket);
    const at = bucket < from ? request.fromUtc : new Date(Number(bucket / 1000n)).toISOString();
    if (!point) { missing++; active = undefined; values.push({ timeUtc: at, exact: null, note: "No observed groups; gap" }); continue; }
    const count = metric === "waits" ? point.waitComparableRows : point.runtimeComparableRows;
    const excluded = metric === "waits" ? point.waitBaselineRows + point.waitResetRows + point.waitUnavailableRows + point.waitStaleRows
      : point.runtimeBaselineRows + point.runtimeResetRows + point.runtimeAmbiguousRows;
    if (!Number.isSafeInteger(count) || count < 0 || !Number.isSafeInteger(excluded) || excluded < 0 || count + excluded !== point.sourceRows)
      throw new Error("Query Store ranking coverage is invalid.");
    comparable += count; unavailable += excluded;
    let exact: string | null = null;
    if (count > 0) {
      if (metric === "waits") {
        const entries = category === null ? Object.values(point.waitCategories) : [point.waitCategories[String(category)] ?? "0"];
        if (entries.some(value => !/^(?:0|[1-9]\d{0,28})$/u.test(value))) throw new Error("Query Store ranking wait counter is invalid.");
        exact = entries.reduce((total, value) => total + BigInt(value), 0n).toString();
      } else exact = point[fields[metric]];
    }
    const value = safeValue(exact);
    const counts = metric === "waits" ? `${point.waitBaselineRows} baseline, ${point.waitResetRows} reset, ${point.waitUnavailableRows} unavailable, ${point.waitStaleRows} stale`
      : `${point.runtimeBaselineRows} baseline, ${point.runtimeResetRows} reset, ${point.runtimeAmbiguousRows} ambiguous epochs`;
    const note = `${count} comparable / ${point.sourceRows} observed groups; ${counts} excluded`;
    values.push({ timeUtc: at, exact, note });
    if (value === null) { if (exact !== null) omissions++; active = undefined; continue; }
    // A mixed bucket can supply a real partial sum, but cannot bridge a reset/epoch boundary.
    if (!active || excluded > 0) { active = []; segments.push({ id: `hour:${bucket}`, points: active }); }
    active.push({ timeUtc: at, value });
    if (excluded > 0) active = undefined;
  }
  return { unit: metric === "waits" ? `${category === null ? "All-category waits" : `Category ${category} waits`} ms/hour bucket` : units[metric],
    source: "Query Store comparable hourly group deltas; current target revision; not latest ranking observations or plan-switch times",
    coverage: `${comparable} comparable groups; ${unavailable} excluded groups; ${missing} missing hourly buckets. Mixed/reset/epoch and missing buckets break connecting lines.`,
    segments, values, precisionOmissions: omissions };
}
/** Qualified cache intervals retain separate source epochs/entries and explicit endpoint gaps. */
export function cacheRankingTrend(history: PlanCacheIntervalHistory, request: RankingTrendRequest, metric: QueryPerformanceMetric): RankingTrendPreview {
  exactWindow(request, history);
  if (request.kind !== "plan_cache" || history.targetId.toLowerCase() !== request.targetId.toLowerCase() || history.databaseId !== request.databaseId ||
      history.databaseIncarnation !== request.databaseIncarnation || history.queryFingerprint !== request.queryFingerprint || history.planFingerprint !== request.planFingerprint ||
      history.items.length > 100) throw new Error("Cache ranking trend identity or page bounds changed.");
  const source = planCacheIntervalTrend(history, metric);
  const segments: { id: string; points: RankingTrendPoint[] }[] = [];
  for (const series of source.series) {
    let active: RankingTrendPoint[] = [];
    for (const point of series.items) {
      if (point.value === null || point.value === undefined) { active = []; continue; }
      if (active.length === 0) segments.push({ id: `${series.id}:${segments.length}`, points: active });
      active.push({ timeUtc: point.time, value: point.value });
    }
  }
  const values = history.items.map(row => ({ timeUtc: row.intervalFromUtc ?? row.observedAtUtc, endUtc: row.intervalToUtc ?? undefined,
    exact: row[fields[metric]], note: `${row.transition.replaceAll("_", " ")} · ${row.captureComplete ? "complete capture" : "partial capture"} · entry ${row.cacheEntryFingerprint.slice(0, 12)}… · epoch ${row.sourceEpoch.slice(0, 8)}…` }));
  return { unit: cacheIntervalRateLabel(metric), source: "Plan-cache qualified interval rates, separated by entry and source epoch; exact counters remain in source values",
    coverage: `${source.plottedIntervals} plotted qualified intervals; ${source.unavailableComparisons} unavailable comparisons; ${source.invalidIntervals} invalid intervals. Newest ${history.items.length} of ${history.totalRows} rows${history.nextCursor ? "; more history is available in Inspect trend" : ""}. ${source.hiddenEntries} entries exceed the ten-series preview bound.`,
    segments, values, precisionOmissions: source.precisionOmissions };
}
