import assert from "node:assert/strict";
import test from "node:test";
import { getDatabaseFileHealth, HealthRequestError } from "../src/features/health/healthApi.ts";
import { syntheticCollector } from "../tools/health-contract-fixture.mjs";

test("database file requests keep the target and opaque cursor in a bounded URL", async () => {
  const originalFetch = globalThis.fetch;
  const paths = [];
  const targetId = "11111111-1111-4111-8111-111111111111";
  const repositoryTimeUtc = "2026-09-26T12:00:00.000Z";
  globalThis.fetch = async (path, options) => {
    paths.push({ path, options });
    return new Response(JSON.stringify({ instanceId: targetId, repositoryTimeUtc,
      collector: syntheticCollector(repositoryTimeUtc), items: [], nextCursor: null }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };
  try {
    const signal = new AbortController().signal;
    await getDatabaseFileHealth(targetId, signal);
    await getDatabaseFileHealth(targetId, signal, "cursor+with/slashes==");
    assert.equal(paths.length, 2);
    const first = new URL(paths[0].path, "https://local.test");
    const next = new URL(paths[1].path, "https://local.test");
    assert.equal(first.pathname, `/api/v1/observation-targets/${targetId}/health/files`);
    assert.equal(first.searchParams.get("limit"), "25");
    assert.equal(first.searchParams.has("cursor"), false);
    assert.equal(next.searchParams.get("limit"), "25");
    assert.equal(next.searchParams.get("cursor"), "cursor+with/slashes==");
    assert.ok(paths[1].options.signal instanceof AbortSignal); assert.equal(paths[1].options.signal.aborted, signal.aborted);
    assert.equal(paths[1].options.credentials, "same-origin");
    // Malformed target input cannot escape its encoded route segment or pass the response binding.
    await assert.rejects(getDatabaseFileHealth("target/with space", signal), HealthRequestError);
    assert.equal(new URL(paths[2].path, "https://local.test").pathname,
      "/api/v1/observation-targets/target%2Fwith%20space/health/files");
  } finally {
    globalThis.fetch = originalFetch;
  }
});
