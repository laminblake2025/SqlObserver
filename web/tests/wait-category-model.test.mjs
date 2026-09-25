import assert from "node:assert/strict";
import test from "node:test";
import { groupWaitDeltas, waitCategory } from "../src/features/activity/waitCategoryModel.ts";

function wait(waitType, delta, overrides = {}) {
  return { waitType, waitTimeMillisecondsDelta: delta, baselineAvailable: true, resetDetected: false, ...overrides };
}

test("wait categories separate actionable causes and keep unknown types visible", () => {
  assert.equal(waitCategory("LCK_M_X"), "Lock");
  assert.equal(waitCategory("PAGEIOLATCH_SH"), "I/O");
  assert.equal(waitCategory("WRITELOG"), "Log");
  assert.equal(waitCategory("RESOURCE_SEMAPHORE"), "Memory");
  assert.equal(waitCategory("CXPACKET"), "Parallelism");
  assert.equal(waitCategory("SOS_SCHEDULER_YIELD"), "CPU/signal");
  assert.equal(waitCategory("HADR_SYNC_COMMIT"), "Other");
  assert.equal(waitCategory("THREADPOOL"), "Other");
  assert.equal(waitCategory("LOGMGR_QUEUE"), "Idle");
});

test("wait totals use exact integers and omit only known idle types", () => {
  const summary = groupWaitDeltas([
    wait("LCK_M_X", "9007199254740993"),
    wait("LCK_M_S", "7"),
    wait("SLEEP_TASK", "999999999999999999"),
    wait("HADR_SYNC_COMMIT", "4"),
  ]);
  assert.deepEqual(summary.categories, [
    { category: "Lock", waitMilliseconds: "9007199254741000", waitTypes: 2 },
    { category: "Other", waitMilliseconds: "4", waitTypes: 1 },
  ]);
  assert.equal(summary.idleTypesOmitted, 1);
  assert.equal(summary.incomparableTypes, 0);
});

test("missing baselines and resets cannot contribute zero or positive deltas", () => {
  const summary = groupWaitDeltas([
    wait("PAGEIOLATCH_SH", "500", { baselineAvailable: false }),
    wait("CXPACKET", "300", { resetDetected: true }),
    wait("WRITELOG", undefined),
    wait("LCK_M_X", "0"),
  ]);
  assert.deepEqual(summary.categories, []);
  assert.equal(summary.incomparableTypes, 3);
});
