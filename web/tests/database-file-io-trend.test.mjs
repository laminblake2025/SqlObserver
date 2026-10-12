import assert from "node:assert/strict";
import test from "node:test";
import { fileIoTrendSeries } from "../src/features/resources/databaseFileIoTrendModel.ts";

test("file I/O trend keeps missing hours, resets, and zero-operation deltas unavailable", () => {
  const trend = {
    targetId: "target", databaseId: 6, fileId: 2,
    fromUtc: "2026-09-28T11:30:00Z", toUtc: "2026-09-28T15:00:00Z",
    semantics: "file_counter_delta", revisionScope: "current", bucketSeconds: 3600,
    points: [
      { bucketStartUtc: "2026-09-28T11:00:00Z", comparableRows: 2,
        readOperations: "3", readStallMilliseconds: "30",
        writeOperations: "0", writeStallMilliseconds: "0" },
      { bucketStartUtc: "2026-09-28T13:00:00Z", comparableRows: 0,
        readOperations: null, readStallMilliseconds: null,
        writeOperations: null, writeStallMilliseconds: null },
      { bucketStartUtc: "2026-09-28T14:00:00Z", comparableRows: 1,
        readOperations: "2", readStallMilliseconds: "15",
        writeOperations: "1", writeStallMilliseconds: "20" },
    ],
  };
  const [reads, writes] = fileIoTrendSeries(trend);
  assert.deepEqual(reads.points.map(point => point.value), [10, null, null, 7.5]);
  assert.deepEqual(writes.points.map(point => point.value), [null, null, null, 20]);
  assert.equal(reads.points[0].timeUtc, "2026-09-28T11:30:00.000Z");
  assert.equal(reads.points[1].samples, 0);
});
