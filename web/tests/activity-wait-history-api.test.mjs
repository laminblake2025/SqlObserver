import assert from "node:assert/strict";
import test from "node:test";
import { getServerWaitHistoryPage } from "../src/features/activity/activityApi.ts";

const instanceId = "11111111-1111-4111-8111-111111111111";
const fromUtc = "2026-08-23T17:00:00Z";
const toUtc = "2026-08-23T18:00:00Z";

test("wait history uses the selected UTC window and forwards the page cursor", async () => {
  const originalFetch = globalThis.fetch;
  const requested = [];
  try {
    globalThis.fetch = async (url) => {
      requested.push(String(url));
      return new Response(JSON.stringify({ instanceId, fromUtc, toUtc,
        repositoryTimeUtc: toUtc, items: [], nextCursor: null }),
      { headers: { "content-type": "application/json" } });
    };
    const window = { fromUtc, toUtc };
    assert.equal((await getServerWaitHistoryPage(instanceId, window, new AbortController().signal)).items.length, 0);
    await getServerWaitHistoryPage(instanceId, window, new AbortController().signal, "next-page");
    assert.equal(requested.length, 2);
    const first = new URL(requested[0], "https://local.invalid");
    assert.equal(first.pathname, `/api/v1/observation-targets/${instanceId}/activity/waits/history`);
    assert.equal(first.searchParams.get("fromUtc"), fromUtc);
    assert.equal(first.searchParams.get("toUtc"), toUtc);
    assert.equal(first.searchParams.get("limit"), "25");
    assert.equal(new URL(requested[1], "https://local.invalid").searchParams.get("cursor"), "next-page");
    await assert.rejects(() => getServerWaitHistoryPage(instanceId,
      { fromUtc, toUtc: "2026-08-25T18:00:00Z" }, new AbortController().signal));
    assert.equal(requested.length, 2);
  } finally { globalThis.fetch = originalFetch; }
});
