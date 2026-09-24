import assert from "node:assert/strict";
import test from "node:test";
import { blockingPageIsComplete, groupBlockingEdges } from "../src/features/activity/blockingProjectionModel.ts";

const edge = (blockedSessionId, blockerSessionId, rootBlockerSessionId, chainDepth) => ({
  blockedSessionId, blockerKind: "session", blockerSessionId, rootBlockerSessionId,
  waitType: "LCK_M_S", waitingTaskCount: "1", waitDurationMilliseconds: "100",
  chainDepth, chainState: "resolved", observedAtUtc: "2026-09-24T12:00:00Z",
});

test("blocking chains group by resolved head and order each edge by depth", () => {
  const groups = groupBlockingEdges([edge(13, 12, 11, 2), edge(21, 20, 20, 1), edge(12, 11, 11, 1)]);
  assert.deepEqual(groups.map(group => [group.label, group.edges.map(item => item.blockedSessionId)]), [
    ["Head blocker · session 11", [12, 13]],
    ["Head blocker · session 20", [21]],
  ]);
});

test("incomplete blocking evidence cannot be described as no blocking", () => {
  const evidence = { freshness: "current", outcome: "succeeded", isPartial: false };
  const page = { items: [], evidence };
  assert.equal(blockingPageIsComplete(page), true);
  assert.equal(blockingPageIsComplete({ ...page, nextCursor: "next" }), false);
  assert.equal(blockingPageIsComplete({ ...page, evidence: { ...evidence, isPartial: true } }), false);
  assert.equal(blockingPageIsComplete({ ...page, evidence: { ...evidence, freshness: "stale" } }), false);
  assert.equal(blockingPageIsComplete({ items: [] }), false);
});
