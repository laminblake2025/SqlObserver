import type { Baseline } from "../analytics/analyticsTypes";
import type { AlertRuleAdminItem } from "../alerts/alertRuleAdminApi.ts";
import type { OverviewSeries } from "./overviewTypes";
import { sqlBaselineMetrics, type SqlBaselinePage } from "../analytics/sqlBaselineModel.ts";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";

export interface ChartBaselineBand {
  readonly targetId: string; readonly metric: string; readonly dimension: string | null; readonly unit: string;
  readonly fromUtc: string; readonly toUtc: string; readonly low: number; readonly high: number; readonly median: number;
  readonly label: string;
  readonly visibilityState?: string; readonly coverage?: number; readonly confidence?: number;
  readonly sourceFromUtc?: string; readonly sourceToUtc?: string;
}
export interface ChartThreshold {
  readonly targetId: string; readonly metric: string; readonly unit: string; readonly value: number;
  readonly label: string;
}
export interface ChartReferences { readonly bands: readonly ChartBaselineBand[]; readonly thresholds: readonly ChartThreshold[]; }

const gib = 1073741824;
const preciseUtc = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
const chartWindow = (fromUtc: string, toUtc: string) => {
  const from = utcMicroseconds(preciseUtc(fromUtc)), to = utcMicroseconds(preciseUtc(toUtc));
  if (to <= from || to - from > 31n * 86400n * 1000000n) throw new Error("Invalid chart window.");
  return { from, to, fromUtc: canonicalUtcTimestamp(preciseUtc(fromUtc)), toUtc: canonicalUtcTimestamp(preciseUtc(toUtc)) };
};
const hourlyInterval = (at: number, window: ReturnType<typeof chartWindow>) => ({
  fromUtc: BigInt(at) * 1000n <= window.from ? window.fromUtc : new Date(at).toISOString(),
  toUtc: BigInt(at + 3600000) * 1000n >= window.to ? window.toUtc : new Date(at + 3600000).toISOString(),
});
const units: Readonly<Record<string, { unit: string; scale: number }>> = {
  "engine.batch_requests_per_second": { unit: "batches/sec", scale: 1 },
  "engine.user_connections": { unit: "connections", scale: 1 },
  "engine.sql_scheduler_cpu_percent": { unit: "%", scale: 1 },
  "engine.scheduler_runnable_tasks": { unit: "tasks", scale: 1 },
  "engine.memory_grants_pending": { unit: "grants", scale: 1 },
  "engine.process_physical_memory_bytes": { unit: "GiB", scale: gib },
  "engine.os_available_memory_bytes": { unit: "GiB", scale: gib },
  "host.memory.available_bytes": { unit: "GiB", scale: gib },
};
export function ruleThresholds(targetId: string, rules: readonly AlertRuleAdminItem[]): readonly ChartThreshold[] {
  return rules.flatMap(rule => {
    const conversion = rule.metricId ? units[rule.metricId] : undefined;
    if (!rule.enabled || rule.kind !== "metric_threshold" || !conversion) return [];
    return [{ targetId, metric: rule.metricId!, unit: conversion.unit, value: rule.threshold / conversion.scale,
      label: `Current rule: ${rule.name} · ${rule.comparison.replaceAll("_", " ")} · revision ${rule.revision}` }];
  });
}

// This is a source-history comparison, not proof that the baseline existed at Rewind time.
export function baselineLookupWindow(fromUtc: string): { fromUtc: string; toUtc: string } {
  const start = Date.parse(fromUtc);
  if (!Number.isFinite(start)) throw new Error("Invalid baseline comparison window.");
  // BaselineV1 uses the preceding 28 complete UTC days. Select that exact
  // source window instead of mixing daily generations in the 200-row API.
  const cutoff = Math.floor(start / 86400000) * 86400000;
  return { fromUtc: new Date(cutoff - 28 * 86400000).toISOString(), toUtc: new Date(cutoff).toISOString() };
}
export function memoryBaselineBands(targetId: string, rows: readonly Baseline[], fromUtc: string, toUtc: string): readonly ChartBaselineBand[] {
  const window = chartWindow(fromUtc, toUtc);
  const from = Date.parse(fromUtc);
  // The old listing contract has no cursor. A full page may omit hours or dimensions.
  if (rows.length >= 200) throw new Error("Baseline listing reached its limit. Narrow the comparison source window.");
  const candidates = rows.filter(row => row.metricKey === "host.memory.available_bytes" && row.dimensions !== undefined
    && Object.keys(row.dimensions).length === 0 && row.windowStartUtc && row.windowEndUtc
    && Date.parse(row.windowEndUtc) <= from && Date.parse(row.windowEndUtc) > Date.parse(row.windowStartUtc)
    && row.visibilityState === "complete" && row.sampleCount > 0 && row.completeDays > 0
    && row.median !== null && row.p10 !== null && row.p90 !== null
    && Number.isFinite(row.median) && Number.isFinite(row.p10) && Number.isFinite(row.p90)
    && row.p10 <= row.median && row.median <= row.p90);
  const latest = [...candidates].sort((a, b) => Date.parse(b.windowEndUtc!) - Date.parse(a.windowEndUtc!) || (b.generation ?? 0) - (a.generation ?? 0))[0];
  if (!latest) return [];
  const byHour = new Map<number, Baseline>();
  for (const row of candidates.filter(row => row.windowStartUtc === latest.windowStartUtc && row.windowEndUtc === latest.windowEndUtc && row.generation === latest.generation)) {
    if (byHour.has(row.hourOfWeek)) throw new Error("Baseline evidence repeats an hourly reference.");
    byHour.set(row.hourOfWeek, row);
  }
  const bands: ChartBaselineBand[] = [];
  for (let at = Math.floor(from / 3600000) * 3600000; BigInt(at) * 1000n < window.to; at += 3600000) {
    const date = new Date(at), row = byHour.get(date.getUTCDay() * 24 + date.getUTCHours());
    if (!row) continue;
    bands.push({ targetId, metric: "host.memory.available_bytes", dimension: null, unit: "GiB",
      ...hourlyInterval(at, window),
      low: row.p10! / gib, high: row.p90! / gib, median: row.median! / gib,
      visibilityState: row.visibilityState, coverage: row.coverage, confidence: row.confidence,
      sourceFromUtc: row.windowStartUtc!, sourceToUtc: row.windowEndUtc!,
      label: `Collector-host memory baseline · p10–p90 · UTC hour ${row.hourOfWeek} · ${row.sampleCount} samples · coverage ${Math.round(row.coverage * 100)}% · confidence ${Math.round(row.confidence * 100)}% · source ${row.windowStartUtc} to ${row.windowEndUtc} · generation ${row.generation}` });
  }
  return bands;
}
export function matchingReferences(series: readonly OverviewSeries[], references?: ChartReferences): ChartReferences {
  const matches = (reference: ChartThreshold | ChartBaselineBand) => series.some(item => item.targetId.toLowerCase() === reference.targetId.toLowerCase()
    && item.metric === reference.metric && item.unit === reference.unit
    && (!("dimension" in reference) || reference.dimension === null || item.dimension === reference.dimension));
  return { bands: (references?.bands ?? []).filter(reference => matches(reference) && Number.isFinite(reference.low) && Number.isFinite(reference.high)
    && Number.isFinite(reference.median) && reference.low <= reference.median && reference.median <= reference.high),
  thresholds: (references?.thresholds ?? []).filter(reference => matches(reference) && Number.isFinite(reference.value)) };
}

export function sqlBaselineBands(page: SqlBaselinePage, fromUtc: string, toUtc: string): readonly ChartBaselineBand[] {
  const window = chartWindow(fromUtc, toUtc);
  const from = Date.parse(fromUtc), metric = sqlBaselineMetrics[page.metricKey];
  if (!metric)
    throw new Error("Invalid SQL baseline chart window.");
  if (utcMicroseconds(preciseUtc(page.toUtc)) > window.from) return [];
  const byHour = new Map(page.items.map(row => [row.hourOfWeek, row]));
  const bands: ChartBaselineBand[] = [];
  for (let at = Math.floor(from / 3600000) * 3600000; BigInt(at) * 1000n < window.to; at += 3600000) {
    const date = new Date(at), row = byHour.get(date.getUTCDay() * 24 + date.getUTCHours());
    if (!row || row.sampleCount === 0 || row.p10 === null || row.p90 === null || row.median === null) continue;
    bands.push({ targetId: page.targetId, metric: page.metricKey, dimension: null, unit: metric.chartUnit,
      ...hourlyInterval(at, window),
      low: row.p10 / metric.scale, high: row.p90 / metric.scale, median: row.median / metric.scale,
      visibilityState: row.visibilityState, coverage: row.coverage, confidence: row.confidence,
      sourceFromUtc: page.fromUtc, sourceToUtc: page.toUtc,
      label: `${metric.label} baseline · ${row.visibilityState} · p10–p90 · UTC hour ${row.hourOfWeek} · ${row.sampleCount} samples · coverage ${Math.round(row.coverage * 100)}% · confidence ${Math.round(row.confidence * 100)}% · source ${page.fromUtc} to ${page.toUtc} · generation ${page.generation}` });
  }
  return bands;
}
