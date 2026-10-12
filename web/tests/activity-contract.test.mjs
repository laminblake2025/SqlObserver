import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { getActivitySnapshot } from "../src/features/activity/activityApi.ts";
import { parseHistory } from "../src/features/activity/activityParser.mjs";
import { blockingHistoryItem } from "../component-tests/activityFixture.mjs";

const panelUrl = new URL("../src/features/activity/TargetActivityPanel.tsx", import.meta.url);

test("activity client requests all bounded target routes and validates untrusted pages", async () => {
  const target = "11111111-1111-4111-8111-111111111111";
  const original = globalThis.fetch; const requests = [];
  let wrongTarget = false;
  globalThis.fetch = async (url, options) => {
    const path = new URL(url,"https://local.test"); requests.push({ path, options });
    return new Response(JSON.stringify({ instanceId: wrongTarget && path.pathname.endsWith("/sessions")
      ? "22222222-2222-4222-8222-222222222222" : target,
      repositoryTimeUtc: "2026-09-30T12:00:00Z", evidence: null, items: [], maximumGraphNodes: 256 }),
    { headers: { "content-type": "application/json", "cache-control": "no-store" } });
  };
  try {
    const result = await getActivitySnapshot(target,new AbortController().signal,null);
    assert.deepEqual(result.errors,[]); assert.equal(requests.length,4);
    for (const [route,limit] of [["sessions",25],["requests",25],["waits",25],["blocking/current",256]]) {
      const request = requests.find(item => item.path.pathname.endsWith(`/${route}`));
      assert.equal(request.path.pathname,`/api/v1/observation-targets/${target}/activity/${route}`);
      assert.equal(request.path.searchParams.get("limit"),String(limit));
      assert.equal(request.options.credentials,"same-origin");
    }
    wrongTarget = true;
    const rejected = await getActivitySnapshot(target,new AbortController().signal,null);
    assert.equal(rejected.sessions,undefined); assert.equal(rejected.errors.length,1);
    assert.deepEqual(rejected.requests.items,[]); assert.deepEqual(rejected.waits.items,[]);
  } finally { globalThis.fetch = original; }
});

test("blocking history parser requires both native edge and evidence", () => {
  const item = blockingHistoryItem();
  assert.equal(parseHistory(item).edge.blockedSessionId, 71);
  assert.equal(parseHistory(item).evidence.freshness, "stale");
  for (const malformed of [{ evidence: item.evidence }, { edge: item.edge },
    { ...item, edge: { ...item.edge, blockedSessionId: "71" } },
    { ...item, evidence: { ...item.evidence, freshness: null } }]) {
    assert.throws(() => parseHistory(malformed), /Activity evidence returned an invalid response/);
  }
});

// Architecture/security lint: mounted inert-text checks cover fixture values,
// while this ban prevents a raw-HTML rendering escape hatch in this module.
test("activity panel security lint forbids raw HTML injection", async () => {
  const panel = await readFile(panelUrl, "utf8");
  assert.doesNotMatch(panel, /dangerouslySetInnerHTML/);
});
