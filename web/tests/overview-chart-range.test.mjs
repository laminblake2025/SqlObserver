import assert from "node:assert/strict";
import test from "node:test";
import { formatOverviewChartValue, overviewChartRange } from "../src/features/overview/overviewChartRange.ts";

const from = "2026-09-24T00:00:00Z", to = "2026-09-24T01:00:00Z";
const series = (points) => [{ targetId: "server", label: "Memory", metric: "memory", unit: "GiB", state: "current", dimension: null, points }];

test("nonzero gauges use their observed range without an artificial zero baseline", () => {
  const range = overviewChartRange(series([
    { timeUtc: from, value: 10 }, { timeUtc: "2026-09-24T00:30:00Z", value: 11 },
    { timeUtc: to, value: 100_000 }, { timeUtc: "2026-09-23T23:00:00Z", value: 0 },
  ]), undefined, from, to);
  assert.ok(range);
  assert.ok(range.minimum > 9 && range.minimum < 10);
  assert.ok(range.maximum > 11 && range.maximum < 12);
});

test("comparison values share the vertical scale but cannot create a chart alone", () => {
  const prior = series([{ timeUtc: "2026-09-23T23:30:00Z", value: 20 }]);
  assert.equal(overviewChartRange(series([]), prior, from, to), null);
  const range = overviewChartRange(series([{ timeUtc: from, value: 10 }]), prior, from, to);
  assert.ok(range);
  assert.ok(range.minimum < 10 && range.maximum > 20);
});

test("zero, negative and missing values produce finite honest ranges", () => {
  assert.deepEqual(overviewChartRange(series([{ timeUtc: from, value: 0 }]), undefined, from, to), { minimum: 0, maximum: 1 });
  const negative = overviewChartRange(series([{ timeUtc: from, value: -20 }, { timeUtc: "2026-09-24T00:30:00Z", value: -10 }]), undefined, from, to);
  assert.ok(negative && negative.minimum < -20 && negative.maximum < 0);
  assert.equal(overviewChartRange(series([{ timeUtc: from, value: null }, { timeUtc: to, value: 1 }]), undefined, from, to), null);
});

test("narrow ranges keep distinct axis labels and readable values", () => {
  const range = overviewChartRange(series([
    { timeUtc: from, value: 10.001 }, { timeUtc: "2026-09-24T00:30:00Z", value: 10.002 },
  ]), undefined, from, to);
  assert.ok(range);
  assert.notEqual(formatOverviewChartValue(10.001, range), formatOverviewChartValue(10.002, range));
  assert.equal(formatOverviewChartValue(null, range), "—");
});
