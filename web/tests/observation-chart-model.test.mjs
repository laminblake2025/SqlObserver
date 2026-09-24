import assert from "node:assert/strict";
import test from "node:test";
import { nearestObservation, observationSegments, prepareObservationChart } from "../src/components/observationChartModel.ts";

const from = "2026-09-23T23:00:00Z";
const to = "2026-09-24T02:00:00Z";

test("chart keeps the full UTC window, missing samples, and nonzero value domain", () => {
  const chart = prepareObservationChart([
    { id: "duration", label: "Duration", items: [
      { time: "2026-09-24T01:00:00Z", value: 105 },
      { time: "2026-09-24T00:00:00Z", value: null },
      { time: "2026-09-23T23:30:00Z", value: 95 },
      { time: "2026-09-24T03:00:00Z", value: 1_000_000 },
    ] },
    { id: "cpu", label: "CPU", items: [{ time: "2026-09-24T00:30:00Z", value: 98 }] },
  ], from, to, { lower: 90, upper: 110, label: "Expected" }, { value: 115, label: "Alert" });

  assert.ok(chart);
  assert.equal(chart.from, Date.parse(from));
  assert.equal(chart.to, Date.parse(to));
  assert.equal(chart.pointCount, 3);
  assert.ok(chart.minimum > 0 && chart.minimum < 90);
  assert.ok(chart.maximum > 115 && chart.maximum < 1_000_000);
  assert.deepEqual(chart.series[0].items.map(item => item?.value ?? null), [95, null, 105]);
  assert.equal(observationSegments(chart.series[0].items).length, 2);
  assert.equal(nearestObservation(chart.series[0].items, Date.parse("2026-09-24T00:50:00Z"))?.value, 105);
});

test("all-negative evidence remains on a negative axis and no-data windows stay empty", () => {
  const chart = prepareObservationChart([
    { id: "delta", label: "Delta", items: [
      { time: from, value: -20 },
      { time: to, value: -10 },
    ] },
  ], from, to);
  assert.ok(chart);
  assert.ok(chart.maximum < 0);
  assert.equal(prepareObservationChart([
    { id: "empty", label: "Empty", items: [{ time: from, value: null }] },
  ], from, to), null);
});
