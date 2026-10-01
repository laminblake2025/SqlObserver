import type { Baseline } from "./analyticsTypes";

export const sqlBaselineChecksum = "702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338";
export const sqlBaselineMetrics: Readonly<Record<string, { unit: string; chartUnit: string; scale: number; label: string }>> = {
  "engine.user_connections": { unit: "count", chartUnit: "connections", scale: 1, label: "SQL connections" },
  "engine.process_physical_memory_bytes": { unit: "bytes", chartUnit: "GiB", scale: 1073741824, label: "SQL process physical memory" },
  "engine.os_available_memory_bytes": { unit: "bytes", chartUnit: "GiB", scale: 1073741824, label: "OS available memory via SQL" },
  "engine.memory_grants_pending": { unit: "count", chartUnit: "grants", scale: 1, label: "SQL memory grants pending" },
  "engine.scheduler_runnable_tasks": { unit: "count", chartUnit: "tasks", scale: 1, label: "SQL scheduler runnable tasks" },
  "engine.batch_requests_per_second": { unit: "batches_per_second", chartUnit: "batches/sec", scale: 1, label: "SQL batch requests" },
  "engine.sql_scheduler_cpu_percent": { unit: "percent", chartUnit: "%", scale: 1, label: "SQL scheduler CPU" },
};
export interface SqlBaselinePage {
  readonly targetId: string; readonly targetRevision: number; readonly metricKey: string; readonly unit: string;
  readonly fromUtc: string; readonly toUtc: string; readonly snapshotUtc: string; readonly generation: number;
  readonly items: readonly Baseline[]; readonly sourceCutoffUtc: string | null; readonly state: string;
}
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Invalid SQL baseline object.");
  return value as Record<string, unknown>;
}
function fields(value: Record<string, unknown>, names: readonly string[]) {
  if (Object.keys(value).some(key => !names.includes(key)) || names.some(key => !(key in value))) throw new Error("Invalid SQL baseline fields.");
}
function utc(value: unknown): string {
  if (typeof value !== "string" || !/(Z|\+00:00)$/.test(value) || !Number.isFinite(Date.parse(value))) throw new Error("Invalid SQL baseline UTC time.");
  return value;
}
function identity(row: Record<string, unknown>) {
  if (row.catalogVersion !== 2 || row.catalogChecksum !== sqlBaselineChecksum || row.algorithmVersion !== "sql-baseline-v2"
    || row.sourceAlgorithmVersion !== "sql-rollup-v2") throw new Error("SQL baseline version does not match.");
}
export function parseSqlBaselinePage(value: unknown, expected: { targetId: string; targetRevision: number; metricKey: string; fromUtc: string; toUtc: string; generation?: number }): SqlBaselinePage {
  const page = object(value);
  fields(page, ["targetId", "targetRevision", "metricKey", "unit", "fromUtc", "toUtc", "snapshotUtc", "generation", "items", "state",
    "catalogVersion", "catalogChecksum", "algorithmVersion", "sourceAlgorithmVersion"]); identity(page);
  const metric = sqlBaselineMetrics[expected.metricKey];
  const from = utc(page.fromUtc), to = utc(page.toUtc), snapshot = utc(page.snapshotUtc);
  if (!metric || typeof page.targetId !== "string" || page.targetId.toLowerCase() !== expected.targetId.toLowerCase()
    || page.targetRevision !== expected.targetRevision || !Number.isSafeInteger(page.targetRevision) || Number(page.targetRevision) < 1
    || page.metricKey !== expected.metricKey || page.unit !== metric.unit || page.generation !== (expected.generation ?? 1)
    || Date.parse(from) !== Date.parse(expected.fromUtc) || Date.parse(to) !== Date.parse(expected.toUtc)
    || Date.parse(to) % 86400000 !== 0 || Date.parse(to) - Date.parse(from) !== 28 * 86400000 || Date.parse(snapshot) < Date.parse(to)
    || !Array.isArray(page.items) || ![0, 168].includes(page.items.length)) throw new Error("SQL baseline window or identity does not match.");
  const hours = new Set<number>(), cutoffs = new Set<string>();
  const items = page.items.map(item => {
    const envelope = object(item); fields(envelope, ["statistics", "sourceCutoffUtc", "catalogVersion", "catalogChecksum", "algorithmVersion", "sourceAlgorithmVersion"]); identity(envelope);
    const cutoff = utc(envelope.sourceCutoffUtc); cutoffs.add(new Date(cutoff).toISOString());
    const row = object(envelope.statistics);
    fields(row, ["metricKey", "dimensionsSha256", "dimensions", "hourOfWeek", "completeDays", "sampleCount", "mean", "stddev", "median", "mad", "p10", "p90",
      "coverage", "confidence", "lowerBound", "upperBound", "windowStartUtc", "windowEndUtc", "generation", "visibilityState"]);
    const dimensions = object(row.dimensions);
    if (row.metricKey !== expected.metricKey || Object.keys(dimensions).length || row.dimensionsSha256 !== "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"
      || row.generation !== page.generation || Date.parse(utc(row.windowStartUtc)) !== Date.parse(from) || Date.parse(utc(row.windowEndUtc)) !== Date.parse(to)
      || Date.parse(cutoff) < Date.parse(to) || Date.parse(cutoff) > Date.parse(snapshot)
      || !Number.isInteger(row.hourOfWeek) || Number(row.hourOfWeek) < 0 || Number(row.hourOfWeek) > 167 || hours.has(Number(row.hourOfWeek))
      || !Number.isInteger(row.completeDays) || Number(row.completeDays) < 0 || Number(row.completeDays) > 28
      || !Number.isInteger(row.sampleCount) || Number(row.sampleCount) < 0 || Number(row.sampleCount) > 4
      || row.coverage !== Number(row.completeDays) / 28 || row.confidence !== Number(row.coverage) * Number(row.sampleCount) / 8
      || row.visibilityState !== (row.sampleCount === 0 ? "unavailable" : row.completeDays === 28 ? "complete" : "partial")
      || row.mean !== null || row.stddev !== null || row.lowerBound !== null || row.upperBound !== null)
      throw new Error("SQL baseline hour or coverage does not match.");
    for (const key of ["median", "mad", "p10", "p90"]) if (row.sampleCount === 0 ? row[key] !== null : typeof row[key] !== "number" || !Number.isFinite(row[key]) || Number(row[key]) < 0)
      throw new Error("Invalid SQL baseline summary.");
    if (Number(row.p10) > Number(row.median) || Number(row.median) > Number(row.p90)) throw new Error("Invalid SQL baseline percentiles.");
    hours.add(Number(row.hourOfWeek));
    return row as unknown as Baseline;
  });
  const state = items.length === 0 ? "no_data" : items.every(row => row.visibilityState === "unavailable") ? "unavailable"
    : items.every(row => row.visibilityState === "complete") ? "complete" : "partial";
  if (page.state !== state || cutoffs.size > 1) throw new Error("SQL baseline response mixes states or snapshots.");
  return { targetId: expected.targetId, targetRevision: expected.targetRevision, metricKey: expected.metricKey, unit: metric.unit,
    fromUtc: from, toUtc: to, snapshotUtc: snapshot, generation: Number(page.generation), items,
    sourceCutoffUtc: [...cutoffs][0] ?? null, state };
}
