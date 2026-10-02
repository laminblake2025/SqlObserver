import type { ObservationSample, ObservationSeries } from "../../components/observationChartModel";
import type { QueryStoreGroupTrend } from "./queryPerformanceTypes";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";

const hourMilliseconds = 3_600_000;
const hourMicroseconds = BigInt(hourMilliseconds) * 1000n;
const nativeUtc = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
const ticks = (value: string) => utcMicroseconds(nativeUtc(value));

function hourlyBuckets(trend: QueryStoreGroupTrend) {
  const from = ticks(trend.fromUtc), to = ticks(trend.toUtc);
  if (from >= to || to - from > 7n * 86400n * 1000000n) throw new Error("Choose a valid plan trend window of up to seven days.");
  const byBucket = new Map(trend.points.map(point => [ticks(point.bucketStartUtc), point]));
  const buckets = [];
  for (let bucket = from - (from % hourMicroseconds + hourMicroseconds) % hourMicroseconds; bucket < to; bucket += hourMicroseconds) {
    buckets.push({ time: bucket < from ? canonicalUtcTimestamp(nativeUtc(trend.fromUtc)) : new Date(Number(bucket / 1000n)).toISOString(), point: byBucket.get(bucket) });
  }
  return buckets;
}

function appendBucket(items: ObservationSample[], time: string, value: number | null, partial: boolean) {
  if (partial && value !== null) items.push({ time, value: null });
  items.push({ time, value });
  if (partial && value !== null) items.push({ time, value: null });
}

export function queryStoreGroupTrendSeries(trend: QueryStoreGroupTrend): readonly ObservationSeries[] {
  const cpu: { time: string; value: number | null }[] = [];
  const duration: { time: string; value: number | null }[] = [];
  for (const { time, point } of hourlyBuckets(trend)) {
    const partial = Boolean(point && point.runtimeBaselineRows + point.runtimeResetRows + point.runtimeAmbiguousRows > 0);
    appendBucket(cpu, time, point?.runtimeComparableRows ? chartValue(point.cpuMilliseconds) : null, partial);
    appendBucket(duration, time, point?.runtimeComparableRows ? chartValue(point.durationMilliseconds) : null, partial);
  }
  return [
    { id: "group-cpu", label: "Comparable CPU (ms)", items: cpu },
    { id: "group-duration", label: "Comparable duration (ms)", items: duration },
  ];
}

export function queryStoreGroupTrendSummary(trend: QueryStoreGroupTrend) {
  const expectedBuckets = hourlyBuckets(trend).length;
  const counts = trend.points.reduce((result, point) => ({
    sourceRows: result.sourceRows + point.sourceRows,
    comparableRows: result.comparableRows + point.runtimeComparableRows,
    baselineRows: result.baselineRows + point.runtimeBaselineRows,
    resetRows: result.resetRows + point.runtimeResetRows,
    ambiguousRows: result.ambiguousRows + point.runtimeAmbiguousRows,
    waitComparableRows: result.waitComparableRows + point.waitComparableRows,
    waitBaselineRows: result.waitBaselineRows + point.waitBaselineRows,
    waitResetRows: result.waitResetRows + point.waitResetRows,
    waitUnavailableRows: result.waitUnavailableRows + point.waitUnavailableRows,
    waitStaleRows: result.waitStaleRows + point.waitStaleRows,
  }), { sourceRows: 0, comparableRows: 0, baselineRows: 0, resetRows: 0,
    ambiguousRows: 0, waitComparableRows: 0, waitBaselineRows: 0,
    waitResetRows: 0, waitUnavailableRows: 0, waitStaleRows: 0 });
  const waits = new Map<number, bigint>();
  for (const point of trend.points.filter(point => point.waitComparableRows > 0))
    for (const [category, milliseconds] of Object.entries(point.waitCategories)) {
      const key = Number(category);
      waits.set(key, (waits.get(key) ?? 0n) + BigInt(milliseconds));
    }
  return {
    ...counts,
    missingBuckets: Math.max(0, expectedBuckets - trend.points.length),
    waits: [...waits].sort((a, b) => a[1] === b[1] ? a[0] - b[0] : a[1] > b[1] ? -1 : 1)
      .map(([category, milliseconds]) => ({ category, milliseconds: milliseconds.toString() })),
  };
}

export function queryStoreGroupWaitCategories(trend: QueryStoreGroupTrend): readonly number[] {
  return [...new Set(trend.points.filter(point => point.waitComparableRows > 0)
    .flatMap(point => Object.keys(point.waitCategories).map(Number)))].filter(category => Number.isInteger(category) && category >= 0 && category <= 31).sort((a, b) => a - b);
}

export function queryStoreGroupWaitTrend(trend: QueryStoreGroupTrend, category: number | null = null) {
  if (category !== null && (!Number.isInteger(category) || category < 0 || category > 31)) throw new Error("Choose a native Query Store wait category.");
  const items: ObservationSample[] = [];
  const values: { timeUtc: string; exact: string | null; note: string }[] = [];
  let precisionOmissions = 0;
  for (const { time, point } of hourlyBuckets(trend)) {
    if (!point) { items.push({ time, value: null }); values.push({ timeUtc: time, exact: null, note: "No observed group rows; gap." }); continue; }
    const excluded = point.waitBaselineRows + point.waitResetRows + point.waitUnavailableRows + point.waitStaleRows;
    const entries = category === null ? Object.values(point.waitCategories) : point.waitCategories[String(category)] === undefined ? [] : [point.waitCategories[String(category)]!];
    const exact = point.waitComparableRows > 0 && entries.length > 0
      ? entries.reduce((sum, value) => { if (!/^(?:0|[1-9]\d{0,28})$/u.test(value)) throw new Error("Query Store wait value is invalid."); return sum + BigInt(value); }, 0n).toString() : null;
    const value = chartValue(exact);
    if (exact !== null && value === null) precisionOmissions++;
    const isolated = excluded > 0 || point.runtimeAmbiguousRows > 0;
    appendBucket(items, time, value, isolated);
    values.push({ timeUtc: time, exact, note: `${point.waitComparableRows} comparable / ${point.sourceRows} observed groups; ${point.waitBaselineRows} baseline, ${point.waitResetRows} reset, ${point.waitUnavailableRows} unavailable, ${point.waitStaleRows} stale excluded.${exact === null ? " No comparable reported category value; gap." : isolated ? " Partial or ambiguous bucket; connecting lines stop at this bucket." : ""}` });
  }
  const series: ObservationSeries = { id: category === null ? "group-waits-all" : `group-waits-${category}`, label: category === null ? "All reported category waits (ms)" : `Category ${category} waits (ms)`, items };
  return { series, values, precisionOmissions };
}

export function queryPlanInspectionUtc(value: string, fromUtc: string, toUtc: string, originalUtc?: string, edited = false): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,6})?)?$/u.test(value)) throw new Error("Enter a valid inspection date and time in UTC.");
  let utc = `${value.length === 16 ? `${value}:00` : value}Z`;
  if (originalUtc && !edited && ticks(utc) === ticks(originalUtc) / 1000n * 1000n) utc = nativeUtc(originalUtc);
  const time = ticks(utc), from = ticks(fromUtc), to = ticks(toUtc);
  if (from >= to || time < from || time >= to) throw new Error("Choose a plan inspection time inside the shared window, before its end.");
  return canonicalUtcTimestamp(utc);
}

function chartValue(value: string | null): number | null {
  if (value === null) return null;
  const number = Number(value);
  return Number.isSafeInteger(number) ? number : null;
}
