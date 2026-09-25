import assert from "node:assert/strict";
import test from "node:test";
import { getDatabaseFileHealth } from "../src/features/health/healthApi.ts";

test("database file requests keep the target and opaque cursor in a bounded URL", async () => {
  const originalFetch = globalThis.fetch;
  const paths = [];
  globalThis.fetch = async (path, options) => {
    paths.push({ path, options });
    return new Response(JSON.stringify({ instanceId: "target", items: [], nextCursor: null }), {
      status: 200,
      headers: { "content-type": "application/json" },
    });
  };
  try {
    const signal = new AbortController().signal;
    await getDatabaseFileHealth("target/with space", signal);
    await getDatabaseFileHealth("target/with space", signal, "cursor+with/slashes==");
    assert.equal(paths.length, 2);
    const first = new URL(paths[0].path, "https://local.test");
    const next = new URL(paths[1].path, "https://local.test");
    assert.equal(first.pathname, "/api/v1/observation-targets/target%2Fwith%20space/health/files");
    assert.equal(first.searchParams.get("limit"), "25");
    assert.equal(first.searchParams.has("cursor"), false);
    assert.equal(next.searchParams.get("limit"), "25");
    assert.equal(next.searchParams.get("cursor"), "cursor+with/slashes==");
    assert.equal(paths[1].options.signal, signal);
    assert.equal(paths[1].options.credentials, "same-origin");
  } finally {
    globalThis.fetch = originalFetch;
  }
});
