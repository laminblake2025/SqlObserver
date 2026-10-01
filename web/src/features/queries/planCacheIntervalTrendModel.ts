import type { ObservationSample, ObservationSeries } from "../../components/observationChartModel";
import type { PlanCacheIntervalHistory, PlanCacheIntervalHistoryItem, QueryPerformanceMetric } from "./queryPerformanceTypes";

const metricFields = { cpu: "cpuMilliseconds", duration: "durationMilliseconds", executions: "executions",
  logical_reads: "logicalReads", writes: "writes", rows: "rows" } as const;
const labels = { cpu: "CPU ms/s", duration: "Duration ms/s", executions: "Executions/s",
  logical_reads: "Logical reads/s", writes: "Writes/s", rows: "Rows/s" } as const;
const maximumCounter = 9223372036854775807n;
const maximumRate = BigInt(Number.MAX_SAFE_INTEGER);
export const maximumCacheTrendSamples = 300;
const maximumSeries = 10;

export function cacheIntervalRateLabel(metric: QueryPerformanceMetric): string { return labels[metric]; }
export function formatCacheIntervalRate(value: number): string {
  if (value === 0) return "0";
  if (Math.abs(value) < 0.001 || Math.abs(value) >= 100000)
    return value.toExponential(5).replace(/(\.[0-9]*?)0+e/u, "$1e").replace(/\.e/u,"e").replace(/e\+/u,"e");
  return Number(value.toPrecision(6)).toString();
}

export function planCacheIntervalTrend(history: PlanCacheIntervalHistory, metric: QueryPerformanceMetric) {
  if (!Object.hasOwn(metricFields, metric)) throw new Error("Cache trend metric is invalid.");
  const from = microseconds(history.fromUtc), to = microseconds(history.toUtc);
  if (from === null || to === null || to <= from || to - from > 604800000000n) throw new Error("Cache trend window is invalid.");
  const groups = new Map<string, { label: string; rows: PlanCacheIntervalHistoryItem[] }>();
  let hiddenEntries = 0, invalidIntervals = 0, precisionOmissions = 0, plottedIntervals = 0;
  const hiddenKeys = new Set<string>();
  for (const row of history.items.slice(0, maximumCacheTrendSamples)) {
    if (row.transition !== "comparable") continue;
    const start = microseconds(row.intervalFromUtc), end = microseconds(row.intervalToUtc);
    if (start === null || end === null || end <= start || start < from || end >= to
        || end !== microseconds(row.observedAtUtc)) { invalidIntervals++; continue; }
    const identity = `${row.sourceEpoch}:${row.cacheEntryFingerprint}`;
    if (!groups.has(identity)) {
      if (groups.size >= maximumSeries) { hiddenKeys.add(identity); continue; }
      groups.set(identity, { label: `Entry ${row.cacheEntryFingerprint.slice(0, 12)}… · epoch ${row.sourceEpoch.slice(0, 8)}…`, rows: [] });
    }
    groups.get(identity)!.rows.push(row);
  }
  hiddenEntries = hiddenKeys.size;
  const series: ObservationSeries[] = [];
  for (const [id, group] of groups) {
    // Append intervals oldest first. At a shared endpoint, the older interval's
    // explicit null must precede the next interval's start in the stable chart sort.
    group.rows.sort((a, b) => {
      const left = microseconds(a.intervalFromUtc)!, right = microseconds(b.intervalFromUtc)!;
      return left < right ? -1 : left > right ? 1 : 0;
    });
    const items: ObservationSample[] = [];
    for (const row of group.rows) {
      const counter = nativeCounter(row[metricFields[metric]], metric === "cpu" || metric === "duration");
      if (counter === null) { invalidIntervals++; continue; }
      const duration = microseconds(row.intervalToUtc)! - microseconds(row.intervalFromUtc)!;
      const value = rate(counter * (metric === "cpu" || metric === "duration" ? 1000n : 1000000n), duration);
      if (value === null) { precisionOmissions++; continue; }
      items.push({ time: row.intervalFromUtc!, value }, { time: row.intervalToUtc!, value }, { time: row.intervalToUtc!, value: null });
      plottedIntervals++;
    }
    if (items.length) series.push({ id, label: group.label, items });
  }
  return { series, plottedIntervals, invalidIntervals, precisionOmissions, hiddenEntries,
    hiddenSamples: Math.max(0, history.items.length - maximumCacheTrendSamples),
    unavailableComparisons: history.items.filter(row => row.transition !== "comparable").length };
}

function microseconds(value: string | null): bigint | null {
  const match = value?.match(/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?Z$/u);
  if (!match) return null;
  const fraction = (match[2] ?? "").padEnd(7, "0");
  const milliseconds = Date.parse(`${match[1]}Z`);
  if (fraction[6] !== "0" || !Number.isSafeInteger(milliseconds)
      || new Date(milliseconds).toISOString().slice(0,19) !== match[1]) return null;
  return BigInt(milliseconds) * 1000n + BigInt(fraction.slice(0,6));
}

function nativeCounter(value: string | null, milliseconds: boolean): bigint | null {
  if (value === null || value.length > 45 || !/^(?:0|[1-9]\d{0,18})(?:\.\d{1,24})?$/u.test(value)) return null;
  const [whole = "0", fraction = ""] = value.split(".");
  if (!milliseconds && fraction.length || milliseconds && /[1-9]/u.test(fraction.slice(3))) return null;
  const result = milliseconds ? BigInt(whole) * 1000n + BigInt(fraction.slice(0,3).padEnd(3,"0")) : BigInt(whole);
  return result <= maximumCounter ? result : null;
}

// Round the rational rate to six significant digits with integers first. Tiny
// nonzero rates stay nonzero; the raw counters never pass through Number.
function rate(numerator: bigint, denominator: bigint): number | null {
  if (numerator === 0n) return 0;
  if (numerator > maximumRate * denominator) return null;
  let exponent = numerator.toString().length - denominator.toString().length;
  if (exponent >= 0 ? numerator < denominator * 10n ** BigInt(exponent)
      : numerator * 10n ** BigInt(-exponent) < denominator) exponent--;
  const shift = 5 - exponent;
  const scaledNumerator = shift >= 0 ? numerator * 10n ** BigInt(shift) : numerator;
  const scaledDenominator = shift >= 0 ? denominator : denominator * 10n ** BigInt(-shift);
  const coefficient = (scaledNumerator + scaledDenominator / 2n) / scaledDenominator;
  const value = Number(`${coefficient}e${exponent - 5}`);
  return Number.isFinite(value) && value > 0 && value <= Number.MAX_SAFE_INTEGER ? value : null;
}
