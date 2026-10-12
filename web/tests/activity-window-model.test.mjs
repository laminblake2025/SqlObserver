import assert from "node:assert/strict";
import test from "node:test";
import { resolveActivityWindow } from "../src/features/activity/activityWindowModel.ts";

const now = Date.parse("2026-09-24T12:00:00.000Z");
const scope = (range, from, to) => ({target:"target",range,from,to,compare:false});

test("recent custom window retains exact UTC bounds for activity", () => {
 const result = resolveActivityWindow(scope("custom","2026-09-24T03:00:00.000Z","2026-09-24T05:00:00.000Z"),now);
 assert.deepEqual(result,{state:"available",window:{fromUtc:"2026-09-24T03:00:00.000Z",toUtc:"2026-09-24T05:00:00.000Z"},liveSnapshotsAvailable:true});
});

test("older window permits blocking history while identifying expired session snapshots", () => {
 const result = resolveActivityWindow(scope("custom","2026-09-20T03:00:00.000Z","2026-09-20T05:00:00.000Z"),now);
 assert.equal(result.state,"available");
 assert.equal(result.liveSnapshotsAvailable,false);
});

test("windows beyond the blocking-history cap and invalid UTC bounds report a reason", () => {
 assert.match(resolveActivityWindow(scope("7d"),now).message,/24 hours/);
 assert.match(resolveActivityWindow(scope("custom","bad","2026-09-24T05:00:00.000Z"),now).message,/valid UTC/);
});


test("activity history cap and session retention boundary compare exact microseconds", () => {
 const at = "2026-09-23T12:00:00.000000Z";
 assert.equal(resolveActivityWindow(scope("custom", at, "2026-09-24T12:00:00.000001Z"),now).state,"unavailable");
 assert.equal(resolveActivityWindow(scope("custom", at, "2026-09-24T12:00:00.000000Z"),now).state,"available");
 const retained = resolveActivityWindow(scope("custom", "2026-09-23T11:00:00Z", "2026-09-23T12:00:00.000001Z"),now);
 assert.equal(retained.state,"available"); assert.equal(retained.liveSnapshotsAvailable,true);
 const expired = resolveActivityWindow(scope("custom", "2026-09-23T11:00:00Z", at),now);
 assert.equal(expired.liveSnapshotsAvailable,false);
});


test("activity accepts native seventh zero without losing positive microsecond windows", () => {
 const result = resolveActivityWindow(scope("custom", "2026-09-24T11:00:00.0000010Z", "2026-09-24T11:00:00.0000020Z"),now);
 assert.equal(result.state,"available");
 assert.deepEqual(result.window,{fromUtc:"2026-09-24T11:00:00.000001Z",toUtc:"2026-09-24T11:00:00.000002Z"});
 assert.equal(resolveActivityWindow(scope("custom", "2026-09-24T11:00:00.0000011Z", "2026-09-24T11:00:00.0000020Z"),now).state,"unavailable");
});
