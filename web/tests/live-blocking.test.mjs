import assert from "node:assert/strict";
import test from "node:test";
import { buildBlockingTree, formatBlockingTarget, isBlockingRelationship, rowsForBlockingGroup } from "../src/features/activity/blockingModel.ts";

const row = (sessionId, blocker, requestId = 0) => ({ identity: `${sessionId}/${requestId}`, sessionId, requestId, blocker, waitType: "LCK_M_X" });

test("blocking relationships exclude zero and null while retaining positive and special blockers", () => {
  assert.equal(isBlockingRelationship(null), false);
  assert.equal(isBlockingRelationship(undefined), false);
  assert.equal(isBlockingRelationship(0), false);
  assert.equal(isBlockingRelationship(77), true);
  assert.equal(isBlockingRelationship(-2), true);

  assert.equal(formatBlockingTarget(null), "—");
  assert.equal(formatBlockingTarget(0), "—");
  assert.equal(formatBlockingTarget(77), "session 77");
  assert.equal(formatBlockingTarget(-2), "special SQL blocker (-2)");
});

test("loaded blocking chains form branches under a visible root without duplicating sessions", () => {
  const groups = buildBlockingTree([row(30, 20), row(40, 20), row(20, 10), row(50, 30), row(10, 0), row(30, 20, 1)]);
  assert.equal(groups.length, 1);
  assert.equal(groups[0].rootLabel, "Session 10");
  assert.equal(groups[0].rootLoaded, true);
  assert.deepEqual(groups[0].nodes.map(node => node.sessionId), [20]);
  assert.deepEqual(groups[0].nodes[0].children.map(node => node.sessionId), [30, 40]);
  assert.equal(groups[0].nodes[0].children[0].rows.length, 2);
  assert.deepEqual(groups[0].nodes[0].children[0].children.map(node => node.sessionId), [50]);
});

test("off-page and special blockers remain explicitly unresolved", () => {
  const groups = buildBlockingTree([row(30, 20), row(20, 10), row(60, -2)]);
  const outside = groups.find(group => group.key === "session:10");
  assert.equal(outside.rootLoaded, false);
  assert.deepEqual(outside.nodes.map(node => node.sessionId), [20]);
  const special = groups.find(group => group.key === "special:-2");
  assert.equal(special.rootLabel, "special SQL blocker (-2)");
  assert.deepEqual(special.nodes.map(node => node.sessionId), [60]);
});

test("conflicting request blockers and cycles retain evidence without inventing a root", () => {
  const ambiguous = buildBlockingTree([row(20, 10), row(20, 11, 1), row(30, 20)]);
  assert.equal(ambiguous.length, 1);
  assert.match(ambiguous[0].issue, /disagree/u);
  assert.equal(ambiguous[0].unresolvedRows.length, 3);
  assert.deepEqual(ambiguous[0].nodes, []);

  const cycle = buildBlockingTree([row(20, 30), row(30, 20), row(40, 20)]);
  assert.equal(cycle.length, 1);
  assert.match(cycle[0].issue, /Cycle/u);
  assert.equal(cycle[0].unresolvedRows.length, 3);
  assert.deepEqual(cycle[0].nodes, []);
});

test("sessions without a blocker do not create a blocking tree", () => {
  assert.deepEqual(buildBlockingTree([row(10, 0), row(20, null)]), []);
});


test("chain filtering includes loaded roots and every descendant request in original row order", () => {
  const rows = [row(30, 20, 1), row(90, 0), row(10, 0), row(20, 10), row(40, 30), row(30, 0, 2), row(10, null, 1), row(81, 80), row(80, 0)];
  const original = [...rows];
  const group = buildBlockingTree(rows).find(value => value.key === "session:10");
  const filtered = rowsForBlockingGroup(rows, group);
  assert.deepEqual(filtered, [rows[0], rows[2], rows[3], rows[4], rows[5], rows[6]]);
  assert.equal(filtered[0], rows[0]);
  assert.deepEqual(rows, original);
});

test("separate roots keep their loaded sessions without unrelated rows", () => {
  const rows = [row(51, null), row(72, 51), row(73, 72), row(81, 0), row(82, 81), row(83, 82), row(90, 0)];
  const groups = buildBlockingTree(rows);
  assert.deepEqual(rowsForBlockingGroup(rows, groups.find(group => group.key === "session:51")), rows.slice(0, 3));
  assert.deepEqual(rowsForBlockingGroup(rows, groups.find(group => group.key === "session:81")), rows.slice(3, 6));
});

test("off-page roots are never invented while loaded descendants remain inspectable", () => {
  const rows = [row(30, 20), row(20, 10), row(30, null, 1), row(90, 0)];
  const group = buildBlockingTree(rows).find(value => value.key === "session:10");
  assert.equal(group.rootLoaded, false);
  assert.deepEqual(rowsForBlockingGroup(rows, group), rows.slice(0, 3));
});

test("cycle filtering keeps unresolved member requests and downstream sessions", () => {
  const rows = [row(20, 30), row(90, 0), row(30, 20), row(40, 20), row(20, 0, 1), row(41, 40)];
  const group = buildBlockingTree(rows).find(value => value.key === "cycle:20");
  assert.match(group.issue, /Cycle/u);
  assert.deepEqual(rowsForBlockingGroup(rows, group), [rows[0], ...rows.slice(2)]);
});

test("ambiguous blockers filter their evidence without selecting a claimed root", () => {
  const rows = [row(10, 0), row(20, 10), row(11, 0), row(20, 11, 1), row(30, 20), row(20, null, 2), row(90, 0)];
  const group = buildBlockingTree(rows).find(value => value.key === "ambiguous:20");
  assert.match(group.issue, /disagree/u);
  assert.deepEqual(rowsForBlockingGroup(rows, group), [rows[1], rows[3], rows[4], rows[5]]);
});

test("special blockers include dependent session rows without a synthetic SQL root", () => {
  const rows = [row(60, -2), row(90, 0), row(61, 60), row(60, null, 1), row(80, -3)];
  const group = buildBlockingTree(rows).find(value => value.key === "special:-2");
  assert.equal(group.rootLoaded, false);
  assert.deepEqual(rowsForBlockingGroup(rows, group), [rows[0], rows[2], rows[3]]);
});

test("missing selected groups and empty pages give zero matches instead of all rows", () => {
  assert.deepEqual(rowsForBlockingGroup([row(10, 0), row(20, 10)], undefined), []);
  const group = buildBlockingTree([row(20, 10)])[0];
  assert.deepEqual(rowsForBlockingGroup([], group), []);
  assert.deepEqual(rowsForBlockingGroup([row(90, 0)], group), []);
});
