import assert from "node:assert/strict";
import test from "node:test";
import { formatDisplayTime } from "../src/timeDisplay.ts";

test("time display preserves the UTC instant while presenting local DST offsets", () => {
  const before = "2026-11-01T05:30:00Z";
  const after = "2026-11-01T06:30:00Z";
  assert.equal(formatDisplayTime(before, "utc"), "11/01/2026, 05:30:00 UTC");
  assert.equal(formatDisplayTime(after, "utc"), "11/01/2026, 06:30:00 UTC");
  assert.equal(formatDisplayTime(before, "local", false, "America/New_York"), "11/01/2026, 01:30:00 EDT");
  assert.equal(formatDisplayTime(after, "local", false, "America/New_York"), "11/01/2026, 01:30:00 EST");
  assert.equal(formatDisplayTime("invalid", "local"), "Time unavailable");
});
