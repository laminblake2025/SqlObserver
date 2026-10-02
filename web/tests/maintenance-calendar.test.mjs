import test from "node:test";
import assert from "node:assert/strict";
import { maintenanceCalendarMonth, maintenanceMonthAt, shiftMaintenanceMonth } from "../src/features/alerts/maintenanceCalendarModel.ts";

const window = (windowId, startsAtUtc, endsAtUtc, cancelledAtUtc = null) => ({ windowId, startsAtUtc, endsAtUtc,
  cancelledAtUtc, reason: windowId, revision: 1 });
const ids = day => day.windows.map(item => item.windowId);

test("UTC agenda assigns exact half-open intervals across midnight, months and leap days", () => {
  const rows = [window("multi", "2028-02-28T23:30:00Z", "2028-03-01T00:00:00Z"),
    window("next", "2028-03-01T00:00:00Z", "2028-03-01T01:00:00Z")];
  const february = maintenanceCalendarMonth("2028-02", rows, "utc");
  assert.equal(february.days.length, 29);
  assert.deepEqual(ids(february.days[27]), ["multi"]);
  assert.deepEqual(ids(february.days[28]), ["multi"]);
  assert.equal(february.overlappingWindows, 1);
  assert.deepEqual(ids(maintenanceCalendarMonth("2028-03", rows, "utc").days[0]), ["next"]);
  assert.equal(shiftMaintenanceMonth("2028-12", 1), "2029-01");
  assert.equal(shiftMaintenanceMonth("2028-01", -1), "2027-12");
  assert.equal(shiftMaintenanceMonth("0001-01", -1), undefined);
  assert.equal(shiftMaintenanceMonth("9999-12", 1), undefined);
});

test("submillisecond and seven-digit native timestamps never round onto a neighboring day", () => {
  const rows = [window("before", "2026-10-01T23:59:59.9999999Z", "2026-10-02T00:00:00.0000000Z"),
    window("cross", "2026-10-01T23:59:59.9999990+00:00", "2026-10-02T00:00:00.0000001Z")];
  const days = maintenanceCalendarMonth("2026-10", rows, "utc").days;
  assert.deepEqual(ids(days[0]), ["cross", "before"]);
  assert.deepEqual(ids(days[1]), ["cross"]);
});

test("local civil days use actual DST boundaries instead of assuming 24-hour duration", () => {
  const previous = process.env.TZ;
  process.env.TZ = "America/New_York";
  try {
    const spring = maintenanceCalendarMonth("2026-03", [window("spring", "2026-03-08T04:59:59.9999999Z", "2026-03-08T05:00:00Z")], "local");
    assert.equal(spring.days[7].fromUtc, "2026-03-08T05:00:00.000Z");
    assert.equal(spring.days[7].toUtc, "2026-03-09T04:00:00.000Z");
    assert.deepEqual(ids(spring.days[6]), ["spring"]);
    assert.deepEqual(ids(spring.days[7]), []);
    const fall = maintenanceCalendarMonth("2026-11", [window("fall", "2026-11-01T05:30:00Z", "2026-11-02T05:00:00Z")], "local");
    assert.equal(fall.days[0].fromUtc, "2026-11-01T04:00:00.000Z");
    assert.equal(fall.days[0].toUtc, "2026-11-02T05:00:00.000Z");
    assert.deepEqual(ids(fall.days[0]), ["fall"]);
    assert.deepEqual(ids(fall.days[1]), []);
    assert.equal(maintenanceMonthAt("2026-11-01T03:59:59.9999999Z", "local"), "2026-10");
    assert.equal(maintenanceMonthAt("2026-11-01T03:59:59.9999999Z", "utc"), "2026-11");
  } finally {
    if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous;
  }
});

test("cancelled and off-month windows remain distinct; invalid intervals are explicit unplaced rows", () => {
  const rows = [window("cancelled", "2026-10-02T12:00:00Z", "2026-10-03T12:00:00Z", "2026-10-01T12:00:00Z"),
    window("future", "2026-12-02T12:00:00Z", "2026-12-03T12:00:00Z"),
    window("invalid", "2026-02-30T00:00:00Z", "2026-10-03T12:00:00Z"),
    window("backwards", "2026-10-04T00:00:00Z", "2026-10-03T00:00:00Z")];
  const month = maintenanceCalendarMonth("2026-10", rows, "utc");
  assert.equal(month.overlappingWindows, 1);
  assert.equal(month.days[1].cancelled, 1);
  assert.deepEqual(ids(month.days[2]), ["cancelled"]);
  assert.equal(month.unplaced, 2);
  assert.equal(maintenanceMonthAt("invalid", "utc"), undefined);
  assert.throws(() => maintenanceCalendarMonth("2026-13", [], "utc"));
});
