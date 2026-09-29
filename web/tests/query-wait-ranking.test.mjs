import assert from "node:assert/strict";
import test from "node:test";
import { getQueryStoreTopWaits } from "../src/features/queries/queryPerformanceApi.ts";

const targetId = "11111111-1111-4111-8111-111111111111";
const fromUtc = "2026-09-28T00:00:00Z";
const toUtc = "2026-09-29T00:00:00Z";
const row = {
  databaseId: 6, queryFingerprint: "a".repeat(64), planFingerprint: "b".repeat(64),
  waitMilliseconds: "9007199254740993", comparableRows: 1, sourceRows: 2,
  lastObservedAtUtc: "2026-09-28T12:00:00Z",
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

test("wait ranking retains exact large totals and missing-group coverage", async () => {
  const body = { targetId, fromUtc, toUtc, semantics: "query_store_group_wait_delta",
    coverage: "observed_groups_only", items: [row] };
  const result = await withResponse(body, () => getQueryStoreTopWaits(targetId,
    fromUtc, toUtc, new AbortController().signal));
  assert.equal(result.items[0].waitMilliseconds, row.waitMilliseconds);
  assert.equal(result.items[0].comparableRows, 1);
  assert.equal(result.items[0].sourceRows, 2);
});

test("wait ranking rejects a false complete or out-of-order response", async () => {
  const base = { targetId, fromUtc, toUtc, semantics: "query_store_group_wait_delta",
    coverage: "observed_groups_only" };
  await assert.rejects(withResponse({ ...base, coverage: "no_comparable_groups", items: [row] },
    () => getQueryStoreTopWaits(targetId, fromUtc, toUtc, new AbortController().signal)));
  await assert.rejects(withResponse({ ...base, items: [row, { ...row,
    planFingerprint: "c".repeat(64), waitMilliseconds: "9007199254740994" }] },
    () => getQueryStoreTopWaits(targetId, fromUtc, toUtc, new AbortController().signal)));
});
