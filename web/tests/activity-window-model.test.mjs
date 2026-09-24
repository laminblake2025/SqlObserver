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
