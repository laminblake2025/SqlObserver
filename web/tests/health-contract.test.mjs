import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { clearApiCache } from "../src/apiClient.ts";
import { getTargetHealth, getTargetHealthEvidence, getDatabaseHealth, HealthRequestError } from "../src/features/health/healthApi.ts";
import { syntheticCollector } from "../tools/health-contract-fixture.mjs";
import { alpha, evidence, logicalFile } from "../component-tests/healthFixture.mjs";

test("native health aggregation binds bounded same-origin reads and preserves exact counters and collector evidence", async () => {
  const previous = globalThis.fetch, source = evidence(), calls = []; source.files.items = [logicalFile()]; clearApiCache();
  globalThis.fetch = async (path, options) => {
    const url = new URL(String(path), "https://fixture.invalid"); calls.push({ url, options });
    const value = url.pathname.endsWith("/files") ? source.files : url.pathname.endsWith("/databases") ? source.databases : source.target;
    return new Response(JSON.stringify(value), { headers: { "content-type": "application/json", "cache-control": "no-store" } });
  };
  try {
    const result = await getTargetHealthEvidence(alpha, new AbortController().signal);
    assert.deepEqual(calls.map(call => call.url.pathname).sort(), ["", "/databases", "/files"].map(suffix => `/api/v1/observation-targets/${alpha}/health${suffix}`));
    for (const { url, options } of calls) {
      assert.equal(options.credentials, "same-origin"); assert.ok(options.signal instanceof AbortSignal);
      assert.equal(new Headers(options.headers).get("accept"), "application/json");
      if (url.pathname.endsWith("/files") || url.pathname.endsWith("/databases")) assert.equal(url.searchParams.get("limit"), "25");
    }
    assert.deepEqual(result.target.collectors, source.target.collectors);
    assert.deepEqual(result.databases.collector, source.databases.collector); assert.deepEqual(result.files.collector, source.files.collector);
    for (const field of ["sizeBytes", "bytesRead", "ioStallMilliseconds"]) assert.equal(result.files.items[0][field], source.files.items[0][field]);
    assert.equal(result.files.nextCursor, null);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("native health client sanitizes HTTP and malformed target responses", async () => {
  const previous = globalThis.fetch; clearApiCache();
  try {
    for (const [status, message] of [[403, "You are not authorized to view health for this target."], [504, "The health request exceeded its execution limit."], [500, "The health request failed safely."]]) {
      globalThis.fetch = async () => new Response("password=synthetic-secret; provider stack", { status });
      await assert.rejects(getTargetHealth(alpha, new AbortController().signal), error => error instanceof HealthRequestError && error.message === message && error.status === status);
    }
    for (const body of ["not JSON", JSON.stringify({ ...evidence().target, instanceId: "22222222-2222-4222-8222-222222222222" })]) {
      globalThis.fetch = async () => new Response(body, { headers: { "content-type": "application/json" } });
      await assert.rejects(getTargetHealth(alpha, new AbortController().signal), error => error instanceof HealthRequestError && error.message === "Health evidence returned an invalid or mismatched response. Refresh to retry.");
    }
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("already-cancelled health requests perform no provider I/O and preserve cancellation", async () => {
  const previous = globalThis.fetch; let calls = 0; clearApiCache();
  globalThis.fetch = async () => { calls++; throw new Error("Provider must not be reached"); };
  try {
    const controller = new AbortController(), reason = new Error("Workspace changed"); controller.abort(reason);
    await assert.rejects(getTargetHealth(alpha, controller.signal), error => error === reason); assert.equal(calls, 0);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("architecture/security lint: health route wiring and prohibited plaintext/rendering constructs", async () => {
  // Source constraints are lint; native API and mounted panel cases prove behavior.
  const panel = await readFile(new URL("../src/features/health/TargetHealthPanel.tsx", import.meta.url), "utf8");
  const types = await readFile(new URL("../src/features/health/healthTypes.ts", import.meta.url), "utf8");
  const app = await readFile(new URL("../src/App.tsx", import.meta.url), "utf8");
  const servers = await readFile(new URL("../src/features/targets/ServersPage.tsx", import.meta.url), "utf8");
  assert.match(servers, /routeHref\("health", target.instanceId\)/); assert.match(app, /<TargetHealthPanel/);
  assert.match(app, /startWorkspaceClock/); assert.doesNotMatch(panel, /setTimeout\(/);
  assert.doesNotMatch(`${panel}\n${types}`, /physicalPath/i); assert.doesNotMatch(`${panel}\n${types}\n${app}`, /dangerouslySetInnerHTML/);
  assert.doesNotMatch(types, /(?:password|connectionString|trustServerCertificate)\s*[?:]/i);
});

test("database health paging encodes the target and continuation cursor", async () => {
  const originalFetch = globalThis.fetch;
  const paths = [];
  const targetId = "11111111-1111-4111-8111-111111111111";
  const repositoryTimeUtc = "2026-09-26T12:00:00.000Z";
  globalThis.fetch = async (path) => {
    paths.push(String(path));
    return new Response(JSON.stringify({ instanceId: targetId, repositoryTimeUtc,
      collector: syntheticCollector(repositoryTimeUtc, "inventory.databases"), items: [], nextCursor: null }), {
      headers: { "Content-Type": "application/json" },
    });
  };
  try {
    await getDatabaseHealth(targetId, new AbortController().signal);
    await getDatabaseHealth(targetId, new AbortController().signal, "page+/=two");
    assert.equal(paths[0], `/api/v1/observation-targets/${targetId}/health/databases?limit=25`);
    assert.equal(paths[1], `/api/v1/observation-targets/${targetId}/health/databases?limit=25&cursor=page%2B%2F%3Dtwo`);
    // Even rejected target input remains escaped into one local path segment.
    await assert.rejects(getDatabaseHealth("server / one", new AbortController().signal), HealthRequestError);
    await assert.rejects(getDatabaseHealth("server / one", new AbortController().signal, "page+/=two"), HealthRequestError);
    assert.equal(paths[2], "/api/v1/observation-targets/server%20%2F%20one/health/databases?limit=25");
    assert.equal(paths[3], "/api/v1/observation-targets/server%20%2F%20one/health/databases?limit=25&cursor=page%2B%2F%3Dtwo");
  } finally {
    globalThis.fetch = originalFetch;
  }
});
