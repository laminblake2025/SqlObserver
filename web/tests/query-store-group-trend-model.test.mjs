import test from "node:test";
import assert from "node:assert/strict";
import { queryStoreGroupTrendSeries, queryStoreGroupTrendSummary, queryStoreGroupWaitCategories, queryStoreGroupWaitTrend, queryPlanInspectionUtc } from "../src/features/queries/queryStoreGroupTrendModel.ts";
import { observationSegments, prepareObservationChart } from "../src/components/observationChartModel.ts";

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

const point = (hour, changes = {}) => ({ bucketStartUtc: `2026-09-25T${hour}:00:00Z`, sourceRows: 1,
  runtimeComparableRows: 1, runtimeBaselineRows: 0, runtimeResetRows: 0, runtimeAmbiguousRows: 0,
  cpuMilliseconds: "125", durationMilliseconds: "250", executions: "2", logicalReads: "4", writes: "1", rows: "2",
  waitComparableRows: 1, waitBaselineRows: 0, waitResetRows: 0, waitUnavailableRows: 0, waitStaleRows: 0,
  waitCategories: { "3": "80", "6": "20" }, ...changes });
const window = { fromUtc: "2026-09-25T11:00:00Z", toUtc: "2026-09-25T16:00:00Z" };

test("wait categories keep exact comparable sums, missing values and unsafe counters distinct", () => {
  const trend = { ...window, points: [point("11"), point("12", { waitCategories: { "6": "0", "31": "9007199254740993" } }),
    point("14", { waitComparableRows: 0, waitResetRows: 1, waitCategories: { "3": "999" } })] };
  assert.deepEqual(queryStoreGroupWaitCategories(trend), [3, 6, 31]);
  const selected = queryStoreGroupWaitTrend(trend, 3);
  assert.deepEqual(selected.values.map(value => value.exact), ["80", null, null, null, null]);
  const zero = queryStoreGroupWaitTrend(trend, 6);
  assert.equal(zero.values[1].exact, "0"); assert.equal(zero.series.items[1].value, 0);
  const all = queryStoreGroupWaitTrend(trend);
  assert.equal(all.values[0].exact, "100"); assert.equal(all.values[1].exact, "9007199254740993");
  assert.equal(all.precisionOmissions, 1); assert.equal(all.series.items[1].value, null);
  assert.equal(queryStoreGroupTrendSummary(trend).waits.find(value => value.category === 3).milliseconds, "80");
  assert.throws(() => queryStoreGroupWaitTrend(trend, 32), /native Query Store/);
});

test("mixed reset unavailable and ambiguous groups isolate partial wait and runtime points", () => {
  const trend = { ...window, toUtc: "2026-09-25T14:00:00Z", points: [point("11"),
    point("12", { sourceRows: 3, runtimeResetRows: 1, runtimeAmbiguousRows: 1, waitResetRows: 1, waitUnavailableRows: 1 }), point("13")] };
  const waits = queryStoreGroupWaitTrend(trend);
  assert.equal(waits.values[1].exact, "100"); assert.match(waits.values[1].note, /connecting lines stop/);
  const waitChart = prepareObservationChart([waits.series], trend.fromUtc, trend.toUtc);
  assert.deepEqual(observationSegments(waitChart.series[0].items).map(segment => segment.length), [1, 1, 1]);
  const runtimeChart = prepareObservationChart(queryStoreGroupTrendSeries(trend), trend.fromUtc, trend.toUtc);
  assert.deepEqual(observationSegments(runtimeChart.series[0].items).map(segment => segment.length), [1, 1, 1]);
});

test("hourly clipping and missing bucket counts preserve exact microseconds across an hour boundary", () => {
  const trend = { fromUtc: "2026-09-25T11:00:00.1234560Z", toUtc: "2026-09-25T14:00:00.000001Z", points: [point("11")] };
  assert.equal(queryStoreGroupTrendSeries(trend)[0].items[0].time, "2026-09-25T11:00:00.123456Z");
  assert.equal(queryStoreGroupWaitTrend(trend).values.length, 4);
  assert.equal(queryStoreGroupTrendSummary(trend).missingBuckets, 3);
  const beforeEpoch = { fromUtc: "1969-12-31T23:30:00Z", toUtc: "1970-01-01T00:00:00.000001Z", points: [] };
  assert.equal(queryStoreGroupWaitTrend(beforeEpoch).values.length, 2);
});

test("keyboard plan inspection validates exact half-open UTC bounds without rounding or accepting invalid calendars", () => {
  const from = "2026-09-25T11:00:00.1234560Z", to = "2026-09-25T11:00:00.1234570Z";
  assert.equal(queryPlanInspectionUtc("2026-09-25T11:00:00.123456", from, to), "2026-09-25T11:00:00.123456Z");
  assert.throws(() => queryPlanInspectionUtc("2026-09-25T11:00:00.123457", from, to), /before its end/);
  assert.throws(() => queryPlanInspectionUtc("2026-09-25T11:00:00.123455", from, to), /inside the shared window/);
  assert.throws(() => queryPlanInspectionUtc("2026-02-30T11:00", from, to), /valid UTC/);
  assert.throws(() => queryPlanInspectionUtc("", from, to), /inspection date/);
  assert.equal(queryPlanInspectionUtc("2026-09-25T11:00:00.123", from, to, "2026-09-25T11:00:00.123456Z"), "2026-09-25T11:00:00.123456Z");
  assert.throws(() => queryPlanInspectionUtc("2026-09-25T11:00:00.123", from, to, "2026-09-25T11:00:00.123456Z", true), /inside the shared window/);
});
