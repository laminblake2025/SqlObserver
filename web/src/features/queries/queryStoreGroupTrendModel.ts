import type { ObservationSeries } from "../../components/observationChartModel";
import type { QueryStoreGroupTrend } from "./queryPerformanceTypes";

const hourMilliseconds = 3_600_000;

export function queryStoreGroupTrendSeries(trend: QueryStoreGroupTrend): readonly ObservationSeries[] {
  const from = Date.parse(trend.fromUtc);
  const to = Date.parse(trend.toUtc);
  const byBucket = new Map(trend.points.map(point => [Date.parse(point.bucketStartUtc), point]));
  const cpu: { time: string; value: number | null }[] = [];
  const duration: { time: string; value: number | null }[] = [];
  for (let bucket = Math.floor(from / hourMilliseconds) * hourMilliseconds;
       bucket < to; bucket += hourMilliseconds) {
    const point = byBucket.get(bucket);
    const time = new Date(Math.max(bucket, from)).toISOString();
    cpu.push({ time, value: point?.runtimeComparableRows ? chartValue(point.cpuMilliseconds) : null });
    duration.push({ time, value: point?.runtimeComparableRows ? chartValue(point.durationMilliseconds) : null });
  }
  return [
    { id: "group-cpu", label: "Comparable CPU (ms)", items: cpu },
    { id: "group-duration", label: "Comparable duration (ms)", items: duration },
  ];
}

export function queryStoreGroupTrendSummary(trend: QueryStoreGroupTrend) {
  const expectedBuckets = Math.ceil(Date.parse(trend.toUtc) / hourMilliseconds)
    - Math.floor(Date.parse(trend.fromUtc) / hourMilliseconds);
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
  for (const point of trend.points)
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

function chartValue(value: string | null): number | null {
  if (value === null) return null;
  const number = Number(value);
  return Number.isSafeInteger(number) ? number : null;
}
