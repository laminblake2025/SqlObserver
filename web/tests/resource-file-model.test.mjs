import assert from "node:assert/strict";
import test from "node:test";
import { fileSizeGib, lifetimeAverageStallMilliseconds } from "../src/features/resources/resourceFileModel.ts";

test("cumulative per-file stall averages preserve large SQL counters and unknowns", () => {
  assert.equal(lifetimeAverageStallMilliseconds("305", "20"), "15.25");
  assert.equal(lifetimeAverageStallMilliseconds("1", "8"), "0.13");
  assert.equal(lifetimeAverageStallMilliseconds("9223372036854775807", "1"), "9223372036854775807.00");
  assert.equal(lifetimeAverageStallMilliseconds(null, "20"), null);
  assert.equal(lifetimeAverageStallMilliseconds("0", "0"), null);
  assert.equal(lifetimeAverageStallMilliseconds("bad", "20"), null);
  assert.equal(fileSizeGib("8589934592"), "8.00 GiB");
  assert.equal(fileSizeGib("536870912"), "0.50 GiB");
  assert.equal(fileSizeGib("bad"), null);
});
