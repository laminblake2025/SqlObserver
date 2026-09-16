import assert from "node:assert/strict";
import test from "node:test";
import {
  coreMetricDefinition,
  formatCoreMetricValue,
  healthSummaryMetricDefinitions,
  latestDimensionlessMetric,
} from "../src/features/health/healthDisplayModel.ts";

const metric = (metricId, observedAtUtc, value, dimensions = [], sampleId = `${metricId}-${observedAtUtc}`) => ({
  sampleId,
  metricId,
  observedAtUtc,
  value,
  dimensions,
});

test("health summary selects directly emitted snapshot metrics with human units", () => {
  assert.deepEqual(
    healthSummaryMetricDefinitions.map(({ metricId, label, unit }) => ({ metricId, label, unit })),
    [
      { metricId: "engine.user_connections", label: "User connections", unit: "connections" },
      { metricId: "engine.process_physical_memory_bytes", label: "SQL physical memory", unit: "GiB" },
      { metricId: "engine.page_life_expectancy_seconds", label: "Page life expectancy", unit: "seconds" },
    ],
  );
});

test("latest dimensionless metric ignores dimensional rows and preserves an explicit zero", () => {
  const metrics = [
    metric("engine.user_connections", "2026-09-10T11:00:00Z", 14),
    metric("engine.user_connections", "2026-09-10T12:00:00Z", 91, [{ key: "scope", value: "database" }]),
    metric("engine.user_connections", "2026-09-10T12:00:00Z", 0, [], "latest-zero"),
  ];

  assert.equal(latestDimensionlessMetric(metrics, "engine.user_connections")?.sampleId, "latest-zero");
  assert.equal(formatCoreMetricValue(latestDimensionlessMetric(metrics, "engine.user_connections"), coreMetricDefinition("engine.user_connections")), "0 connections");
  assert.equal(latestDimensionlessMetric(metrics, "engine.page_life_expectancy_seconds"), undefined);
});

test("physical memory is converted from source bytes while timestamps remain source evidence", () => {
  const memory = metric("engine.process_physical_memory_bytes", "2026-09-10T12:00:00.123Z", 2 * 1024 ** 3);
  assert.equal(formatCoreMetricValue(memory), "2 GiB");
  assert.equal(memory.observedAtUtc, "2026-09-10T12:00:00.123Z");
});

test("same-time observations resolve deterministically without treating missing as zero", () => {
  const first = metric("engine.user_connections", "2026-09-10T12:00:00Z", 1, [], "a");
  const second = metric("engine.user_connections", "2026-09-10T12:00:00Z", 2, [], "b");
  assert.equal(latestDimensionlessMetric([second, first], "engine.user_connections")?.sampleId, "b");
  assert.equal(latestDimensionlessMetric([], "engine.user_connections"), undefined);
});
