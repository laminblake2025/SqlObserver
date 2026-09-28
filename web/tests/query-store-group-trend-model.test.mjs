import test from "node:test";
import assert from "node:assert/strict";
import { queryStoreGroupTrendSeries, queryStoreGroupTrendSummary } from "../src/features/queries/queryStoreGroupTrendModel.ts";

test("plan trend leaves missing and incomparable hours empty and sums waits exactly", () => {
  const baseline = {
    bucketStartUtc: "2026-09-25T11:00:00Z", sourceRows: 1,
    runtimeComparableRows: 0, runtimeBaselineRows: 1, runtimeResetRows: 0,
    runtimeAmbiguousRows: 0, cpuMilliseconds: null, durationMilliseconds: null,
    executions: null, logicalReads: null, writes: null, rows: null,
    waitComparableRows: 0, waitBaselineRows: 1, waitResetRows: 0,
    waitUnavailableRows: 0, waitStaleRows: 0, waitCategories: {},
  };
  const comparable = {
    ...baseline, bucketStartUtc: "2026-09-25T13:00:00Z",
    runtimeComparableRows: 1, runtimeBaselineRows: 0,
    cpuMilliseconds: "125", durationMilliseconds: "250", executions: "2",
    logicalReads: "4", writes: "1", rows: "2",
    waitComparableRows: 1, waitBaselineRows: 0,
    waitCategories: { "3": "9007199254740993" },
  };
  const trend = { fromUtc: "2026-09-25T11:30:00Z", toUtc: "2026-09-25T14:00:00Z",
    points: [baseline, comparable] };
  const [cpu, duration] = queryStoreGroupTrendSeries(trend);
  assert.deepEqual(cpu.items, [
    { time: "2026-09-25T11:30:00.000Z", value: null },
    { time: "2026-09-25T12:00:00.000Z", value: null },
    { time: "2026-09-25T13:00:00.000Z", value: 125 },
  ]);
  assert.equal(duration.items[2].value, 250);
  assert.deepEqual(queryStoreGroupTrendSummary(trend), {
    sourceRows: 2, comparableRows: 1, baselineRows: 1, resetRows: 0,
    ambiguousRows: 0, waitComparableRows: 1, waitBaselineRows: 1,
    waitResetRows: 0, waitUnavailableRows: 0, waitStaleRows: 0,
    missingBuckets: 1,
    waits: [{ category: 3, milliseconds: "9007199254740993" }],
  });
  assert.equal(queryStoreGroupTrendSeries({ ...trend, points: [{ ...comparable,
    cpuMilliseconds: "9007199254740993" }] })[0].items[2].value, null);
});
