import assert from "node:assert/strict";
import test from "node:test";
import { buildBlockingTree, formatBlockingTarget, isBlockingRelationship } from "../src/features/activity/blockingModel.ts";

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
