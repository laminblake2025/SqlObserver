import assert from "node:assert/strict";
import test from "node:test";
import { formatBlockingTarget, isBlockingRelationship } from "../src/features/activity/blockingModel.ts";

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
