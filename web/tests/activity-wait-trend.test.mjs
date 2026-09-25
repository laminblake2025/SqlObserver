import assert from "node:assert/strict";
import test from "node:test";
import { getServerWaitTrend, parseWaitTrend } from "../src/features/activity/waitTrendParser.mjs";

const instanceId = "11111111-1111-4111-8111-111111111111";
const window = { fromUtc: "2026-09-25T07:55:00Z", toUtc: "2026-09-25T08:00:00Z" };
const labels = ["Lock", "I/O", "CPU/signal", "Memory", "Parallelism", "Log", "Other"];
const body = () => ({ instanceId, ...window, repositoryTimeUtc: "2026-09-25T08:00:01Z",
  points: labels.map(category => ({ bucketStartUtc: "2026-09-25T07:55:00Z", category,
    waitMilliseconds: category === "Lock" ? "25" : "0", runCount: 1,
    missingSummaryRuns: 0, partialRuns: 0, incomparableTypes: 0, comparableTypes: 1 })) });

test("wait trend validates target, window, complete categories and gap evidence", () => {
  assert.equal(parseWaitTrend(body(), instanceId, window).points.length, 7);
  assert.throws(() => parseWaitTrend({ ...body(), instanceId: "22222222-2222-4222-8222-222222222222" }, instanceId, window));
  assert.throws(() => parseWaitTrend({ ...body(), toUtc: "2026-09-25T09:00:00Z" }, instanceId, window));
  assert.throws(() => parseWaitTrend({ ...body(), points: body().points.slice(1) }, instanceId, window));
  const duplicated = body(); duplicated.points[1].category = "Lock";
  assert.throws(() => parseWaitTrend(duplicated, instanceId, window));
  const inconsistent = body(); inconsistent.points[1].runCount = 2;
  assert.throws(() => parseWaitTrend(inconsistent, instanceId, window));
  const incomplete = body(); incomplete.points[0].partialRuns = 1;
  assert.throws(() => parseWaitTrend(incomplete, instanceId, window));
  incomplete.points.forEach(point => { point.partialRuns = 1; point.waitMilliseconds = null; });
  assert.equal(parseWaitTrend(incomplete, instanceId, window).points[0].waitMilliseconds, null);
});

test("wait trend requests only the selected target and UTC window without caching", async () => {
  const originalFetch = globalThis.fetch;
  let requested;
  try {
    globalThis.fetch = async (url, options) => {
      requested = { url: String(url), options };
      return new Response(JSON.stringify(body()), { headers: { "content-type": "application/json" } });
    };
    const result = await getServerWaitTrend(instanceId, window, new AbortController().signal);
    assert.equal(result.points.length, 7);
    const url = new URL(requested.url, "https://local.invalid");
    assert.equal(url.pathname, `/api/v1/observation-targets/${instanceId}/activity/waits/trend`);
    assert.equal(url.searchParams.get("fromUtc"), window.fromUtc);
    assert.equal(url.searchParams.get("toUtc"), window.toUtc);
    assert.equal(requested.options.cache, "no-store");
    await assert.rejects(() => getServerWaitTrend(instanceId,
      { fromUtc: window.fromUtc, toUtc: "2026-09-27T08:00:00Z" }, new AbortController().signal));
  } finally { globalThis.fetch = originalFetch; }
});
