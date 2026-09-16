import type { CoreMetricSummary } from "./healthTypes";

export type CoreMetricValueFormat = "count" | "bytes";

export interface CoreMetricDefinition {
  readonly metricId: string;
  readonly label: string;
  readonly unit: string;
  readonly format: CoreMetricValueFormat;
}

const definitions = {
  "engine.batch_requests_total": {
    metricId: "engine.batch_requests_total",
    label: "Batch requests total",
    unit: "batches",
    format: "count",
  },
  "engine.sql_compilations_total": {
    metricId: "engine.sql_compilations_total",
    label: "SQL compilations total",
    unit: "compilations",
    format: "count",
  },
  "engine.sql_recompilations_total": {
    metricId: "engine.sql_recompilations_total",
    label: "SQL recompilations total",
    unit: "recompilations",
    format: "count",
  },
  "engine.page_life_expectancy_seconds": {
    metricId: "engine.page_life_expectancy_seconds",
    label: "Page life expectancy",
    unit: "seconds",
    format: "count",
  },
  "engine.user_connections": {
    metricId: "engine.user_connections",
    label: "User connections",
    unit: "connections",
    format: "count",
  },
  "engine.process_physical_memory_bytes": {
    metricId: "engine.process_physical_memory_bytes",
    label: "SQL physical memory",
    unit: "GiB",
    format: "bytes",
  },
  "engine.committed_memory_bytes": {
    metricId: "engine.committed_memory_bytes",
    label: "Committed memory",
    unit: "GiB",
    format: "bytes",
  },
  "engine.start_time_key": {
    metricId: "engine.start_time_key",
    label: "SQL Server start time key",
    unit: "seconds since 2000-01-01",
    format: "count",
  },
  "engine.target_memory_bytes": {
    metricId: "engine.target_memory_bytes",
    label: "SQL target memory",
    unit: "GiB",
    format: "bytes",
  },
} as const satisfies Readonly<Record<string, CoreMetricDefinition>>;

const coreMetricDefinitions: Readonly<Record<string, CoreMetricDefinition>> = definitions;

export { coreMetricDefinitions };

export const healthSummaryMetricDefinitions = [
  definitions["engine.user_connections"],
  definitions["engine.process_physical_memory_bytes"],
  definitions["engine.page_life_expectancy_seconds"],
] as const satisfies readonly CoreMetricDefinition[];

export function coreMetricDefinition(metricId: string): CoreMetricDefinition {
  return coreMetricDefinitions[metricId] ?? {
    metricId,
    label: humanizeMetricId(metricId),
    unit: "value",
    format: "count",
  };
}

export function latestDimensionlessMetric(
  metrics: readonly CoreMetricSummary[],
  metricId: string,
): CoreMetricSummary | undefined {
  let latest: CoreMetricSummary | undefined;
  for (const metric of metrics) {
    if (metric.metricId !== metricId || metric.dimensions.length !== 0) continue;
    if (latest === undefined || isLaterObservation(metric, latest)) {
      latest = metric;
    }
  }
  return latest;
}

export function formatCoreMetricValue(
  metric: CoreMetricSummary,
  definition: CoreMetricDefinition = coreMetricDefinition(metric.metricId),
): string {
  if (!Number.isFinite(metric.value)) return "Unavailable";
  const value = definition.format === "bytes" ? metric.value / (1024 ** 3) : metric.value;
  return `${value.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${definition.unit}`;
}

function isLaterObservation(candidate: CoreMetricSummary, current: CoreMetricSummary): boolean {
  const candidateTime = observationTime(candidate.observedAtUtc);
  const currentTime = observationTime(current.observedAtUtc);
  if (candidateTime !== currentTime) return candidateTime > currentTime;
  return candidate.sampleId.localeCompare(current.sampleId) > 0;
}

function observationTime(value: string): number {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : Number.NEGATIVE_INFINITY;
}

function humanizeMetricId(metricId: string): string {
  return metricId
    .split(/[._]/u)
    .filter((part) => part.length > 0)
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
    .join(" ");
}
