import assert from "node:assert/strict";
import test from "node:test";
import { getQueryStorePlanRegressions } from "../src/features/queries/queryPerformanceApi.ts";

const targetId = "11111111-1111-4111-8111-111111111111";
const fromUtc = "2026-09-28T12:00:00Z";
const toUtc = "2026-09-29T12:00:00Z";
const comparisonFromUtc = "2026-09-27T12:00:00Z";
const row = {
  databaseId: 6, queryFingerprint: "a".repeat(64),
  previousPlanFingerprint: "b".repeat(64), currentPlanFingerprint: "c".repeat(64),
  previousAverageMilliseconds: "20", currentAverageMilliseconds: "120",
  slowdownRatio: "6", previousExecutions: "20", currentExecutions: "20",
  previousComparableRows: 2, previousSourceRows: 2,
  currentComparableRows: 2, currentSourceRows: 3,
  lastObservedAtUtc: "2026-09-29T11:00:00Z",
};

async function withResponse(body, action) {
  const previous = globalThis.fetch;
  try {
    globalThis.fetch = async () => new Response(JSON.stringify(body),
      { status: 200, headers: { "content-type": "application/json" } });
    return await action();
  } finally {
    globalThis.fetch = previous;
  }
}

test("plan comparison retains equal windows and partial group coverage", async () => {
  const body = { targetId, fromUtc, toUtc, comparisonFromUtc,
    semantics: "query_store_group_plan_comparison", coverage: "observed_groups_only",
    items: [row] };
  const result = await withResponse(body, () => getQueryStorePlanRegressions(targetId,
    fromUtc, toUtc, new AbortController().signal));
  assert.equal(result.items[0].slowdownRatio, "6");
  assert.equal(result.items[0].currentComparableRows, 2);
  assert.equal(result.items[0].currentSourceRows, 3);
});

test("plan comparison rejects mismatched windows and unchanged plans", async () => {
  const base = { targetId, fromUtc, toUtc, comparisonFromUtc,
    semantics: "query_store_group_plan_comparison", coverage: "observed_groups_only" };
  await assert.rejects(withResponse({ ...base, comparisonFromUtc: fromUtc, items: [row] },
    () => getQueryStorePlanRegressions(targetId, fromUtc, toUtc, new AbortController().signal)));
  await assert.rejects(withResponse({ ...base, items: [{ ...row,
    currentPlanFingerprint: row.previousPlanFingerprint }] },
    () => getQueryStorePlanRegressions(targetId, fromUtc, toUtc, new AbortController().signal)));
  await assert.rejects(withResponse({ ...base, coverage: "no_qualifying_changes", items: [row] },
    () => getQueryStorePlanRegressions(targetId, fromUtc, toUtc, new AbortController().signal)));
});
