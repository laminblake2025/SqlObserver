import test from "node:test";
import assert from "node:assert/strict";
import { apiFetch, clearApiCache } from "../src/apiClient.ts";
import { getAlertRulesForAdmin } from "../src/features/alerts/alertRuleAdminApi.ts";

const path = "/api/v1/observation-targets/server/health";
const json = (value, headers = {}) => new Response(JSON.stringify(value), {
  headers: { "content-type": "application/json", ...headers },
});
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};
const tick = () => new Promise(resolve => setImmediate(resolve));

async function withFetch(handler, run) {
  const previous = globalThis.fetch;
  clearApiCache();
  globalThis.fetch = handler;
  try { await run(); }
  finally { clearApiCache(); globalThis.fetch = previous; }
}

test("matching reads share a request and give each subscriber an independent body", async () => {
  const waiting = deferred(); let calls = 0; let request;
  await withFetch(async (url, options) => { calls++; request = { url, options }; return waiting.promise; }, async () => {
    const first = apiFetch(path); const second = apiFetch(path);
    await tick(); assert.equal(calls,1);
    waiting.resolve(json({ samples: [1,2] }, { "cache-control": "no-store" }));
    const [a,b] = await Promise.all([first,second]);
    assert.deepEqual(await a.json(),{ samples: [1,2] });
    assert.deepEqual(await b.json(),{ samples: [1,2] });
    assert.equal(request.options.credentials,"same-origin");
    assert.equal(request.options.mode,"same-origin"); assert.equal(request.options.redirect,"error");
    await apiFetch(path); assert.equal(calls,2);
  });
});

test("cancelling one panel keeps a shared read alive for another panel", async () => {
  const waiting = deferred(); let networkSignal;
  await withFetch(async (_url, options) => { networkSignal = options.signal; return waiting.promise; }, async () => {
    const a = new AbortController(); const b = new AbortController();
    const first = apiFetch(path,{ signal: a.signal }); const second = apiFetch(path,{ signal: b.signal });
    const rejected = assert.rejects(first,{ name: "AbortError" });
    await tick(); a.abort(); await rejected;
    assert.equal(networkSignal.aborted,false);
    waiting.resolve(json({ okay: true })); assert.deepEqual(await (await second).json(),{ okay: true });
  });
});

test("production rule readers share a request and cancel independently through schema validation", async () => {
  const target = "11111111-1111-4111-8111-111111111111";
  const waiting = deferred(); let calls = 0;
  await withFetch(async () => { calls++; return waiting.promise; },async () => {
    const a = new AbortController(); const b = new AbortController();
    const first = getAlertRulesForAdmin(target,a.signal); const second = getAlertRulesForAdmin(target,b.signal);
    const rejected = assert.rejects(first,{ name: "AbortError" }); await tick(); a.abort(); await rejected;
    waiting.resolve(json({ targetId: target, listingStartedUtc: "2026-09-30T12:00:00Z", items: [], hasMore: false, nextCursor: null }));
    assert.deepEqual((await second).items,[]); assert.equal(calls,1);
  });
});

test("last subscriber cancellation aborts the network and a later read starts fresh", async () => {
  let calls = 0; const signals = [];
  await withFetch(async (_url,options) => {
    calls++; signals.push(options.signal);
    return calls === 1 ? new Promise(() => {}) : json({ okay: true });
  }, async () => {
    const controller = new AbortController();
    const request = apiFetch(path,{ signal: controller.signal });
    const rejected = assert.rejects(request,{ name: "AbortError" });
    await tick(); controller.abort(); await rejected; assert.equal(signals[0].aborted,true);
    assert.deepEqual(await (await apiFetch(path)).json(),{ okay: true }); assert.equal(calls,2);
  });
});

test("request keys separate windows, headers and response limits", async () => {
  let calls = 0;
  await withFetch(async () => { calls++; await tick(); return json({ okay: true }); }, async () => {
    await Promise.all([
      apiFetch(`${path}?at=one`),apiFetch(`${path}?at=two`),
      apiFetch(path,{ headers: { Authorization: "one" } }),apiFetch(path,{ headers: { Authorization: "two" } }),
      apiFetch(path,{}, { maximumResponseBytes: 100 }),apiFetch(path,{}, { maximumResponseBytes: 200 }),
    ]); assert.equal(calls,6);
  });
});

test("public JSON caching expires, honors Age, and evicts oldest entries", async () => {
  let calls = 0; const clock = Date.now; let now = clock(); Date.now = () => now;
  try {
    await withFetch(async () => json({ calls: ++calls },{ "cache-control": "public, max-age=10", age: "8" }), async () => {
      assert.deepEqual(await (await apiFetch(path)).json(),{ calls: 1 });
      assert.deepEqual(await (await apiFetch(path)).json(),{ calls: 1 });
      now += 2001; assert.deepEqual(await (await apiFetch(path)).json(),{ calls: 2 });
      for (let i=0;i<32;i++) await apiFetch(`${path}?page=${i}`);
      await apiFetch(path); assert.equal(calls,35);
    });
  } finally { Date.now = clock; }
});

test("private, no-store, no-cache and identity-varying responses are never cached", async () => {
  for (const headers of [
    { "cache-control": "private, max-age=60" },{ "cache-control": "public, no-store, max-age=60" },
    { "cache-control": "public, no-cache, max-age=60" },{ "cache-control": "public, max-age=60", vary: "Cookie" },
    { "cache-control": 'public, private="set-cookie", max-age=60' },
    { "cache-control": 'public, no-cache="authorization", max-age=60' },
    { "cache-control": "public, max-age=60", vary: "Authorization" },{ "cache-control": "public, max-age=60", vary: "*" },
  ]) {
    let calls = 0;
    await withFetch(async () => { calls++; return json({},headers); },async () => {
      await apiFetch(path); await apiFetch(path); assert.equal(calls,2);
    });
  }
});

test("outstanding request bounds survive invalidation and release slots after cancellation", async () => {
  let calls = 0;
  await withFetch(async () => { calls++; return new Promise(() => {}); },async () => {
    const controllers = Array.from({ length: 64 }, () => new AbortController());
    const requests = controllers.map((controller, i) => apiFetch(`${path}?slot=${i}`, { signal: controller.signal }));
    const cancelled = requests.map(request => assert.rejects(request, { name: "AbortError" }));
    await tick(); assert.equal(calls,64);
    clearApiCache();
    await assert.rejects(apiFetch(`${path}?extra=true`), /Too many API requests/);
    assert.equal(calls,64);
    controllers.forEach(controller => controller.abort()); await Promise.all(cancelled); await tick();
    globalThis.fetch = async () => { calls++; return json({ recovered: true }); };
    assert.deepEqual(await (await apiFetch(path)).json(), { recovered: true }); assert.equal(calls,65);
  });
});

test("writes invalidate reads on start and on completion including uncertain network failures", async () => {
  let reads = 0; const mutation = deferred();
  await withFetch(async (_url,options) => options.method === "GET"
    ? json({ reads: ++reads },{ "cache-control": "public, max-age=60" }) : mutation.promise,
  async () => {
    await apiFetch(path);
    const write = apiFetch(path,{ method: "POST",body: "{}" });
    const rejected = assert.rejects(write,/network/);
    await apiFetch(path); assert.equal(reads,2);
    mutation.resolve(Promise.reject(new Error("network"))); await rejected;
    await apiFetch(path); assert.equal(reads,3);
  });
});

test("in-flight reads started before a write cannot populate the new cache", async () => {
  const old = deferred(); let reads = 0;
  await withFetch(async (_url,options) => options.method === "GET" && ++reads === 1
    ? old.promise : json({ reads },{ "cache-control": "public, max-age=60" }),async () => {
    const before = apiFetch(path); await tick();
    await apiFetch(path,{ method: "POST",body: "{}" });
    old.resolve(json({ old: true },{ "cache-control": "public, max-age=60" })); await before;
    await apiFetch(path); assert.equal(reads,2);
  });
});

test("denied responses invalidate cached data and failures are not cached", async () => {
  for (const status of [401,403,500]) {
    let calls = 0;
    await withFetch(async () => ++calls === 1 ? json({}, { "cache-control": "public, max-age=60" })
      : new Response(null,{ status }),async () => {
      await apiFetch(path);
      await apiFetch(`${path}?denied=true`); await apiFetch(`${path}?denied=true`);
      await apiFetch(path); assert.equal(calls,status === 500 ? 3 : 4);
    });
  }
});

test("declared and streamed response limits cancel unread data", async () => {
  for (const declared of ["1000","invalid",null]) {
    let cancelled = false;
    await withFetch(async () => new Response(new ReadableStream({
      start(controller) { controller.enqueue(new Uint8Array(8)); controller.enqueue(new Uint8Array(8)); },
      cancel() { cancelled = true; },
    }), { headers: declared === null ? {} : { "content-length": declared } }),async () => {
      await assert.rejects(apiFetch(path,{}, { maximumResponseBytes: 10 }),/too large/);
      assert.equal(cancelled,true);
    });
  }
});

test("timeouts cover stalled fetch and stalled response body without poisoning later reads", async () => {
  for (const stalled of ["fetch","body"]) {
    let calls = 0;
    await withFetch(async () => ++calls > 1 ? json({ recovered: true }) : stalled === "fetch"
      ? new Promise(() => {}) : new Response(new ReadableStream({ start() {} })),async () => {
      await assert.rejects(apiFetch(path,{}, { timeoutMilliseconds: 5 }),{ name: "TimeoutError" });
      assert.deepEqual(await (await apiFetch(path)).json(),{ recovered: true });
    });
  }
});

test("pre-aborted requests, external paths and invalid bounds perform no network request", async () => {
  let calls = 0;
  await withFetch(async () => { calls++; return json({}); },async () => {
    const controller = new AbortController(); controller.abort();
    await assert.rejects(apiFetch(path,{ signal: controller.signal }),{ name: "AbortError" });
    for (const invalid of ["https://example.com/api/v1/health","//example.com/api/v1/health","/api/v1/health#part","/api/v1/\\host","/other","/api/v9/health"])
      await assert.rejects(apiFetch(invalid),/local API path/);
    await assert.rejects(apiFetch(path,{}, { maximumResponseBytes: 0 }),/bounds/);
    await assert.rejects(apiFetch(path,{}, { timeoutMilliseconds: Infinity }),/bounds/);
    assert.equal(calls,0);
  });
});
