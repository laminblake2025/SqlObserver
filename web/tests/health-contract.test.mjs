import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { clearApiCache } from "../src/apiClient.ts";
import { getTargetHealth, getTargetHealthEvidence, getDatabaseHealth, getServerConfigurationSnapshot,
  setServerConfigurationCollection, subscribeHealthReadDenials, HealthRequestError } from "../src/features/health/healthApi.ts";
import { syntheticCollector } from "../tools/health-contract-fixture.mjs";
import { alpha, beta, evidence, logicalFile } from "../component-tests/healthFixture.mjs";

const response = value => new Response(JSON.stringify(value), {
  headers: { "content-type": "application/json", "cache-control": "no-store" },
});
const sourceFor = path => String(path).split("?")[0].endsWith("/files") ? "files"
  : String(path).split("?")[0].endsWith("/databases") ? "databases" : "target";
function deferred() {
  let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve };
}

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

for (const failed of ["target", "databases", "files"]) {
  test(`latest health ${failed} failure preserves the independently validated sibling sources`, async () => {
    const previous = globalThis.fetch, source = evidence(), updates = []; clearApiCache();
    globalThis.fetch = async path => sourceFor(path) === failed
      ? new Response("private provider details", { status: 503 }) : response(source[sourceFor(path)]);
    try {
      const result = await getTargetHealthEvidence(alpha, new AbortController().signal, { onSource: value => updates.push(value) });
      assert.equal(result[failed], undefined);
      assert.deepEqual(result.errors, { [failed]: "The health request failed safely." });
      for (const sibling of ["target", "databases", "files"].filter(value => value !== failed)) {
        assert.equal(result[sibling].instanceId, alpha);
        assert.equal(result[sibling].repositoryTimeUtc, source[sibling].repositoryTimeUtc);
      }
      assert.equal(updates.length, 3);
      assert.equal(updates.find(value => value.source === failed).value, undefined);
      assert.equal(JSON.stringify(result).includes("private provider"), false);
    } finally { globalThis.fetch = previous; clearApiCache(); }
  });
}

test("invalid latest health source is isolated rather than replaced by a fabricated empty receipt", async () => {
  const previous = globalThis.fetch, source = evidence(); clearApiCache();
  globalThis.fetch = async path => response(sourceFor(path) === "files" ? { ...source.files, instanceId: beta } : source[sourceFor(path)]);
  try {
    const result = await getTargetHealthEvidence(alpha, new AbortController().signal);
    assert.equal(result.files, undefined);
    assert.match(result.errors.files, /invalid or mismatched/);
    assert.equal(result.target.instanceId, alpha); assert.equal(result.databases.instanceId, alpha);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("all unavailable latest sources return only their errors with no synthetic successful snapshot", async () => {
  const previous = globalThis.fetch; clearApiCache();
  globalThis.fetch = async () => new Response("unavailable", { status: 503 });
  try {
    const result = await getTargetHealthEvidence(alpha, new AbortController().signal);
    assert.deepEqual(Object.keys(result).sort(), ["errors"]);
    assert.deepEqual(Object.keys(result.errors).sort(), ["databases", "files", "target"]);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("validated latest source is delivered before held siblings finish, retaining source clocks", async () => {
  const previous = globalThis.fetch, source = evidence(), hold = deferred(), delivered = deferred(), updates = []; clearApiCache();
  globalThis.fetch = async path => sourceFor(path) === "files" ? hold.promise : response(source[sourceFor(path)]);
  try {
    const read = getTargetHealthEvidence(alpha, new AbortController().signal, { onSource: value => {
      updates.push(value); if (value.source === "target") delivered.resolve(value);
    } });
    const first = await delivered.promise;
    assert.equal(first.value.repositoryTimeUtc, source.target.repositoryTimeUtc);
    assert.equal(updates.some(value => value.source === "files"), false);
    hold.resolve(new Response("unavailable", { status: 503 }));
    const result = await read;
    assert.equal(result.target.repositoryTimeUtc, first.value.repositoryTimeUtc);
    assert.equal(result.files, undefined); assert.match(result.errors.files, /failed safely/);
  } finally { hold.resolve(response(source.files)); globalThis.fetch = previous; clearApiCache(); }
});

for (const denied of ["target", "databases", "files"]) for (const status of [401, 403]) {
  test(`actual ${status} from ${denied} takes priority over an earlier failure and aborts pending health peers`, async () => {
    const previous = globalThis.fetch, source = evidence(), hold = deferred(), failure = deferred(); clearApiCache();
    const siblings = ["target", "databases", "files"].filter(value => value !== denied);
    let peerSignal, denials = 0; const updates = [];
    globalThis.fetch = async (path, options) => {
      const key = sourceFor(path);
      if (key === siblings[0]) return new Response("ordinary failure", { status: 503 });
      if (key === denied) { await failure.promise; return new Response("private authorization details", { status }); }
      peerSignal = options.signal; return hold.promise;
    };
    try {
      const read = getTargetHealthEvidence(alpha, new AbortController().signal, {
        onSource: value => { updates.push(value); if (value.error) failure.resolve(); },
        onAccessDenied: error => { denials++; assert.equal(error.status, status); },
      });
      await assert.rejects(read, error => error instanceof HealthRequestError && error.status === status && !error.message.includes("private"));
      assert.equal(denials, 1); assert.equal(peerSignal.aborted, true);
      assert.deepEqual(updates.map(value => value.source), [siblings[0]]);
      hold.resolve(response(source[siblings[1]]));
      await Promise.resolve(); assert.equal(updates.length, 1);
    } finally { failure.resolve(); hold.resolve(response(source[siblings[1]])); globalThis.fetch = previous; clearApiCache(); }
  });
}

test("already-cancelled aggregate health read performs no I/O or callbacks", async () => {
  const previous = globalThis.fetch; let calls = 0, notifications = 0; clearApiCache();
  globalThis.fetch = async () => { calls++; return new Response("denied", { status: 403 }); };
  try {
    const controller = new AbortController(), reason = new Error("Context changed"); controller.abort(reason);
    await assert.rejects(getTargetHealthEvidence(alpha, controller.signal, {
      onSource: () => notifications++, onAccessDenied: () => notifications++,
    }), error => error === reason);
    assert.equal(calls, 0); assert.equal(notifications, 0);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test("health-only denial subscriptions isolate targets, survive listener errors and stop on unsubscribe", async () => {
  const previous = globalThis.fetch, alphaStatuses = [], betaStatuses = []; clearApiCache();
  const unsubscribe = subscribeHealthReadDenials(alpha, error => alphaStatuses.push(error.status));
  const removeThrowing = subscribeHealthReadDenials(alpha, () => { throw new Error("Subscriber failed"); });
  const removeBeta = subscribeHealthReadDenials(beta, error => betaStatuses.push(error.status));
  globalThis.fetch = async () => new Response("private details", { status: 403 });
  try {
    await assert.rejects(getServerConfigurationSnapshot(alpha, new AbortController().signal), error => error instanceof HealthRequestError && error.status === 403);
    assert.deepEqual(alphaStatuses, [403]); assert.deepEqual(betaStatuses, []);
    unsubscribe(); removeThrowing();
    await assert.rejects(getTargetHealth(alpha, new AbortController().signal), HealthRequestError);
    assert.deepEqual(alphaStatuses, [403]);
    await assert.rejects(getTargetHealth(beta, new AbortController().signal), HealthRequestError);
    assert.deepEqual(betaStatuses, [403]);
  } finally { unsubscribe(); removeThrowing(); removeBeta(); globalThis.fetch = previous; clearApiCache(); }
});

for (const status of [401, 403]) test(`aborted late ${status} health read cannot notify mounted listeners`, async () => {
  const previous = globalThis.fetch, started = deferred(), hold = deferred(); let notified = 0; clearApiCache();
  const unsubscribe = subscribeHealthReadDenials(alpha, () => notified++);
  globalThis.fetch = async () => { started.resolve(); return hold.promise; };
  try {
    const controller = new AbortController(), reason = new Error("Old window retired");
    const rejected = assert.rejects(getTargetHealth(alpha, controller.signal), error => error === reason);
    await started.promise; controller.abort(reason); hold.resolve(new Response("denied", { status }));
    await rejected; await Promise.resolve(); assert.equal(notified, 0);
  } finally { unsubscribe(); hold.resolve(new Response("denied", { status })); globalThis.fetch = previous; clearApiCache(); }
});

test("old target ABA request cannot deliver denial to a newly registered health owner", async () => {
  const previous = globalThis.fetch, started = deferred(), hold = deferred(); let oldCalls = 0, newCalls = 0; clearApiCache();
  const oldUnsubscribe = subscribeHealthReadDenials(alpha, () => oldCalls++); let newUnsubscribe = () => {};
  globalThis.fetch = async () => { started.resolve(); return hold.promise; };
  try {
    const rejected = assert.rejects(getTargetHealth(alpha, new AbortController().signal), HealthRequestError);
    await started.promise; oldUnsubscribe(); newUnsubscribe = subscribeHealthReadDenials(alpha, () => newCalls++);
    hold.resolve(new Response("denied", { status: 401 })); await rejected;
    assert.equal(oldCalls, 0); assert.equal(newCalls, 0);
    globalThis.fetch = async () => new Response("denied", { status: 403 });
    await assert.rejects(getTargetHealth(alpha, new AbortController().signal), HealthRequestError);
    assert.equal(newCalls, 1);
  } finally { oldUnsubscribe(); newUnsubscribe(); hold.resolve(new Response("denied", { status: 401 })); globalThis.fetch = previous; clearApiCache(); }
});

test("collector permission gaps and refused configuration mutations are not health read denials", async () => {
  const previous = globalThis.fetch; let calls = 0; clearApiCache();
  const unsubscribe = subscribeHealthReadDenials(alpha, () => calls++);
  const source = evidence(alpha, { state: "degraded", reason: "permission_denied", hasVisibilityGap: true, lastExecutionOutcome: "permission_denied" });
  globalThis.fetch = async (_path, options) => options.method === "PUT"
    ? new Response("mutation denied", { status: 403 }) : response(source.target);
  try {
    const result = await getTargetHealth(alpha, new AbortController().signal);
    assert.equal(result.collectors[0].reason, "permission_denied"); assert.equal(calls, 0);
    await assert.rejects(setServerConfigurationCollection(alpha, 1, false, true, new AbortController().signal), error => error.status === 403);
    assert.equal(calls, 0);
  } finally { unsubscribe(); globalThis.fetch = previous; clearApiCache(); }
});

test("caller cancellation carrying a status never becomes a native aggregate denial", async () => {
  const previous = globalThis.fetch, started = deferred(), hold = deferred(); let calls = 0, notifications = 0; clearApiCache();
  globalThis.fetch = async () => { if (++calls === 3) started.resolve(); return hold.promise; };
  try {
    const controller = new AbortController(), reason = new HealthRequestError("Context canceled", 403);
    const rejected = assert.rejects(getTargetHealthEvidence(alpha, controller.signal, {
      onSource: () => notifications++, onAccessDenied: () => notifications++,
    }), error => error === reason);
    await started.promise; controller.abort(reason); hold.resolve(new Response("denied", { status: 401 }));
    await rejected; assert.equal(notifications, 0);
  } finally { hold.resolve(new Response("denied", { status: 401 })); globalThis.fetch = previous; clearApiCache(); }
});

test("aggregate denial callback cannot replace the actual native error", async () => {
  const previous = globalThis.fetch; clearApiCache();
  globalThis.fetch = async () => new Response("denied", { status: 401 });
  try {
    await assert.rejects(getTargetHealthEvidence(alpha, new AbortController().signal, {
      onAccessDenied: () => { throw new Error("Callback failure"); },
    }), error => error instanceof HealthRequestError && error.status === 401);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});
