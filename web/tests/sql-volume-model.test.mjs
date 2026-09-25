import assert from "node:assert/strict";
import test from "node:test";
import { volumeEvidenceMessage, volumeUsedPercent } from "../src/features/resources/sqlVolumeModel.ts";

test("volume percentages retain exact 64-bit capacity before rounding", () => {
  assert.equal(volumeUsedPercent("9223372036854775807", "4611686018427387903"), "50.00%");
  assert.equal(volumeUsedPercent("3", "2"), "33.33%");
  assert.equal(volumeUsedPercent("3", "0"), "100.00%");
  assert.equal(volumeUsedPercent("0", "0"), null);
  assert.equal(volumeUsedPercent("1", "2"), null);
  assert.equal(volumeUsedPercent(null, "1"), null);
});

test("volume evidence names gaps without inventing capacity", () => {
  assert.match(volumeEvidenceMessage("unavailable", "not_collected"), /has not produced a snapshot/);
  assert.match(volumeEvidenceMessage("unavailable", "evidence_expired"), /expired/);
  assert.match(volumeEvidenceMessage("stale", "completed"), /overdue/);
});
