import assert from "node:assert/strict";
import test from "node:test";
import { chartObservedPoints, chartSelection, chartUtcAtX, chartX } from "../src/features/overview/chartTimeModel.ts";

const from = "2026-09-24T10:00:00Z";
const to = "2026-09-24T12:00:00Z";

test("chart pointer positions map to bounded UTC times", () => {
  assert.ok(Math.abs(chartX(48, 0, 650) - 48) < 1e-9);
  assert.ok(Math.abs(chartX(620, 0, 650) - 620) < 1e-9);
  assert.equal(chartUtcAtX(48, from, to), "2026-09-24T10:00:00.000Z");
  assert.equal(chartUtcAtX(334, from, to), "2026-09-24T11:00:00.000Z");
  assert.equal(chartUtcAtX(620, from, to), "2026-09-24T12:00:00.000Z");
  assert.equal(chartUtcAtX(-500, from, to), "2026-09-24T10:00:00.000Z");
  assert.throws(() => chartX(5, 0, 0));
});

test("dragging either direction selects one shared UTC window and ignores tiny clicks", () => {
  const selected = chartSelection(191, 477, from, to);
  assert.deepEqual(selected, { fromUtc: "2026-09-24T10:30:00.000Z", toUtc: "2026-09-24T11:30:00.000Z" });
  assert.deepEqual(chartSelection(477, 191, from, to), selected);
  assert.equal(chartSelection(191, 192, from, to), undefined);
  assert.throws(() => chartSelection(191, 477, to, from));
});

test("missing buckets and null samples break chart lines", () => {
  const points = [
    { timeUtc: "2026-09-24T10:00:00Z", value: 10 },
    { timeUtc: "2026-09-24T10:05:00Z", value: 11 },
    { timeUtc: "2026-09-24T10:15:00Z", value: 12 },
    { timeUtc: "2026-09-24T10:20:00Z", value: null },
    { timeUtc: "2026-09-24T10:25:00Z", value: 13 },
  ];
  assert.deepEqual(chartObservedPoints(points, "2026-09-24T10:00:00Z", "2026-09-24T11:00:00Z"),
    [points[0], points[1], null, points[2], null, points[4]]);
});

test("chart gap threshold follows the server's 15-minute and hourly buckets", () => {
  const points = [
    { timeUtc: "2026-09-24T00:00:00Z", value: 1 },
    { timeUtc: "2026-09-24T00:15:00Z", value: 2 },
    { timeUtc: "2026-09-24T01:15:00Z", value: 3 },
  ];
  assert.deepEqual(chartObservedPoints(points, "2026-09-24T00:00:00Z", "2026-09-24T12:00:00Z"),
    [points[0], points[1], null, points[2]]);
  assert.deepEqual(chartObservedPoints(points, "2026-09-24T00:00:00Z", "2026-09-26T00:00:00Z"),
    [points[0], points[1], points[2]]);
});
