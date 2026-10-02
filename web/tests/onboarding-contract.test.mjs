import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { getObservationTarget, listObservationTargets, registerObservationTarget, updateObservationTargetGrouping } from "../src/features/targets/targetApi.ts";
import { alpha, beta, registrationRequest, registeredTarget } from "../component-tests/onboardingFixture.mjs";

const invalidTargetResponse = "SqlObserver target response is invalid. Refresh and retry.";
const jsonResponse = value => new Response(JSON.stringify(value), { headers: { "content-type": "application/json", "cache-control": "no-store" } });

test("native targets accept nullable address/discovery fields and preserve every lifecycle and capability state", async () => {
  const original = globalThis.fetch;
  let value;
  globalThis.fetch = async () => jsonResponse(value);
  try {
    for (const status of ["pending", "supported", "degraded", "unsupported", "unreachable", "authentication_failed", "tls_validation_failed", "timed_out", "security_policy_rejected", "disabled", "retired"]) {
      value = registeredTarget(registrationRequest(), { namedInstance: null, certificateHostName: null,
        lastDiscoveryAtUtc: null, capabilityStatus: status, lifecycle: status === "disabled" || status === "retired" ? status : "active",
        discoveryRequestedAtUtc: "2026-09-30T13:00:00.1234560+00:00" });
      const parsed = await getObservationTarget(alpha.toUpperCase(), new AbortController().signal);
      assert.equal(parsed.capabilityStatus, status);
      assert.equal(parsed.lifecycle, value.lifecycle);
      assert.equal(parsed.namedInstance, undefined); assert.equal(parsed.lastDiscoveryAtUtc, undefined);
      assert.equal(parsed.discoveryRequestedAtUtc, value.discoveryRequestedAtUtc);
    }
    value = registeredTarget(registrationRequest("named"), { tcpPort: null, displayName: "é".repeat(256) });
    assert.equal((await registerObservationTarget(registrationRequest("named"), new AbortController().signal)).displayName, value.displayName);
  } finally { globalThis.fetch = original; }
});

test("native registration rejects malformed and wrong-target success DTOs with a stable safe error", async () => {
  const original = globalThis.fetch, body = registrationRequest();
  let value;
  globalThis.fetch = async () => jsonResponse(value);
  try {
    for (const overrides of [{ instanceId: beta }, { instanceId: "00000000-0000-0000-0000-000000000000" },
      { authenticationMode: "sql_password" }, { encryptionMode: "optional" }, { lifecycle: "healthy" },
      { capabilityStatus: "healthy" }, { namedInstance: "APP" }, { tcpPort: null }, { tcpPort: 65536 },
      { configurationRevision: Number.MAX_SAFE_INTEGER + 1 }, { configurationRevision: 0 },
      { discoveryRequestedAtUtc: "2026-02-30T13:00:00Z" }, { discoveryRequestedAtUtc: "2026-09-30T13:00:00-04:00" },
      { lastDiscoveryAtUtc: "provider-secret" }, { displayName: "é".repeat(257) }, { host: "provider;password=secret" },
      { capabilityReasons: [null] }, { instanceKey: "INVALID KEY" }]) {
      value = registeredTarget(body, overrides);
      await assert.rejects(registerObservationTarget(body, new AbortController().signal), { message: invalidTargetResponse });
    }
    for (const malformed of [null, [], {}, { ...registeredTarget(body), capabilityReasons: undefined }]) {
      value = malformed;
      await assert.rejects(registerObservationTarget(body, new AbortController().signal), { message: invalidTargetResponse });
    }
  } finally { globalThis.fetch = original; }
});

test("native target listing respects requested page bounds, duplicate identities and opaque native cursors", async () => {
  const original = globalThis.fetch, requests = [];
  let value = { items: [registeredTarget(registrationRequest()), registeredTarget({ ...registrationRequest("named"), instanceId: beta })], nextCursor: "dGFyZ2V0X2N1cnNvcg" };
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return jsonResponse(value); };
  try {
    const result = await listObservationTargets(new AbortController().signal, "dGFyZ2V0X2N1cnNvcg", 2);
    assert.equal(result.items.length, 2); assert.equal(result.nextCursor, value.nextCursor);
    assert.equal(requests[0].url, "/api/v1/observation-targets?limit=2&cursor=dGFyZ2V0X2N1cnNvcg");
    for (const malformed of [{ ...value, items: [...value.items, value.items[0]] },
      { ...value, items: [value.items[0], { ...value.items[0], instanceId: alpha.toUpperCase() }] },
      { ...value, nextCursor: "bad+/=cursor" }, { ...value, nextCursor: "a".repeat(513) }, { items: {} }]) {
      value = malformed;
      await assert.rejects(listObservationTargets(new AbortController().signal, undefined, 2), { message: invalidTargetResponse });
    }
    value = { items: [], nextCursor: null };
    assert.deepEqual(await listObservationTargets(new AbortController().signal), { items: [] });
    value = { items: Array.from({ length: 100 }, (_, index) => registeredTarget(registrationRequest(), {
      instanceId: `00000000-0000-0000-0000-${(index + 1).toString(16).padStart(12, "0")}`,
    })), nextCursor: null };
    assert.equal((await listObservationTargets(new AbortController().signal, undefined, 100)).items.length, 100);
    await assert.rejects(listObservationTargets(new AbortController().signal), { message: invalidTargetResponse });
  } finally { globalThis.fetch = original; }
});

test("native 100-target page accepts maximum display names escaped by the default ASP.NET JSON encoder", async t => {
  const original = globalThis.fetch;
  const displayName = "<".repeat(512);
  const value = { items: Array.from({ length: 100 }, (_, index) => registeredTarget(registrationRequest(), {
    instanceId: `00000000-0000-0000-0000-${(index + 1).toString(16).padStart(12, "0")}`, displayName,
    namedInstance: null, lastDiscoveryAtUtc: null, capabilityReasons: ["discovery_pending"],
  })), nextCursor: null };
  // System.Text.Json's default encoder escapes HTML-sensitive '<' as six ASCII
  // bytes. The domain permits it, with a 512-byte UTF-8 display-name bound.
  const body = JSON.stringify(value).replaceAll("<", "\\u003C");
  const encodedBytes = Buffer.byteLength(body);
  assert.ok(encodedBytes > 256 * 1024);
  t.diagnostic(`Native escaped 100-target response: ${encodedBytes} bytes; target response bound: 1048576 bytes.`);
  globalThis.fetch = async () => new Response(body, { headers: { "content-type": "application/json", "cache-control": "no-store" } });
  try {
    const parsed = await listObservationTargets(new AbortController().signal, undefined, 100);
    assert.equal(parsed.items.length, 100);
    assert.ok(parsed.items.every(item => item.displayName === displayName));
    await assert.rejects(listObservationTargets(new AbortController().signal), { message: invalidTargetResponse });
  } finally { globalThis.fetch = original; }
});

test("native get and grouping results fence exact target identity and normalized grouping contracts", async () => {
  const original = globalThis.fetch, requests = [];
  let value = { instanceId: alpha, environment: "production", tags: ["api", "east"], groupingRevision: 1 };
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return jsonResponse(value); };
  try {
    const result = await updateObservationTargetGrouping(alpha, 0, " Production ", ["East", "api"], new AbortController().signal);
    assert.deepEqual(result, value);
    assert.deepEqual(JSON.parse(requests[0].options.body), { expectedRevision: 0, environment: " Production ", tags: ["East", "api"] });
    for (const overrides of [{ instanceId: beta }, { tags: ["api", "api"] }, { tags: ["UPPER"] }, { tags: Array(17).fill("api") },
      { environment: " production " }, { groupingRevision: 0 }, { groupingRevision: Number.MAX_SAFE_INTEGER + 1 }, { tags: null }]) {
      value = { instanceId: alpha, environment: null, tags: [], groupingRevision: 1, ...overrides };
      await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], new AbortController().signal), error => error.outcomeUnknown === true);
    }
    value = registeredTarget(registrationRequest(), { instanceId: beta });
    await assert.rejects(getObservationTarget(alpha, new AbortController().signal), { message: invalidTargetResponse });
  } finally { globalThis.fetch = original; }
});

test("native target API rejects invalid JSON, content type, UTF-8 and bounded overflow without reflecting payloads", async () => {
  const original = globalThis.fetch;
  let response;
  globalThis.fetch = async () => response;
  try {
    for (const candidate of [new Response('{"provider-secret":', { headers: { "content-type": "application/json" } }),
      new Response(JSON.stringify(registeredTarget(registrationRequest())), { headers: { "content-type": "text/plain" } }),
      new Response(new Uint8Array([0xc3, 0x28]), { headers: { "content-type": "application/json" } }),
      new Response(JSON.stringify(registeredTarget(registrationRequest())), {
        headers: { "content-type": "application/json", "content-length": String(1024 * 1024 + 1) } }),
      new Response(JSON.stringify({ ...registeredTarget(registrationRequest()), padding: " ".repeat(1024 * 1024) }),
        { headers: { "content-type": "application/json" } })]) {
      response = candidate;
      await assert.rejects(registerObservationTarget(registrationRequest(), new AbortController().signal), { message: invalidTargetResponse });
    }
    globalThis.fetch = async () => { throw new Error("provider password=synthetic-secret"); };
    await assert.rejects(registerObservationTarget(registrationRequest(), new AbortController().signal), { message: "The target service is temporarily unavailable. Retry the request." });
    const controller = new AbortController(), reason = new Error("Caller cancelled"); controller.abort(reason);
    await assert.rejects(getObservationTarget(alpha, controller.signal), error => error === reason);
  } finally { globalThis.fetch = original; }
});

test("native registration API sends exclusive TCP and named-instance bodies with same-origin JSON transport", async () => {
  const original = globalThis.fetch, requests = [];
  globalThis.fetch = async (url, options) => {
    const body = JSON.parse(options.body); requests.push({ url, options, body });
    return new Response(JSON.stringify(registeredTarget(body)), { status: 201,
      headers: { "content-type": "application/json", "cache-control": "no-store" } });
  };
  try {
    for (const mode of ["tcp", "named"]) {
      const body = registrationRequest(mode), controller = new AbortController();
      const result = await registerObservationTarget(body, controller.signal);
      assert.equal(result.instanceId, body.instanceId);
      assert.equal(result.lifecycle, "pending_discovery");
      const sent = requests.at(-1);
      assert.equal(sent.url, "/api/v1/observation-targets");
      assert.deepEqual(sent.body, body);
      assert.equal(sent.options.method, "POST");
      assert.equal(sent.options.credentials, "same-origin");
      assert.equal(sent.options.mode, "same-origin");
      assert.equal(sent.options.redirect, "error");
      assert.equal(sent.options.headers.get("accept"), "application/json");
      assert.equal(sent.options.headers.get("content-type"), "application/json");
      assert.equal(Object.hasOwn(sent.body, "tcpPort"), mode === "tcp");
      assert.equal(Object.hasOwn(sent.body, "namedInstance"), mode === "named");
    }
  } finally { globalThis.fetch = original; }
});

test("native administrative rejection reports safe status messages without reflecting provider bodies", async () => {
  const original = globalThis.fetch;
  let status = 429;
  globalThis.fetch = async () => new Response('password=synthetic-secret <script>injected()</script>', {
    status, headers: { "content-type": "text/html", "cache-control": "no-store" },
  });
  try {
    for (const [code, message] of [[429, "Too many administrative requests. Wait briefly and retry."],
      [403, "SqlObserver request failed with status 403."], [500, "SqlObserver request failed with status 500."]]) {
      status = code;
      await assert.rejects(registerObservationTarget(registrationRequest(), new AbortController().signal),
        error => error instanceof Error && error.message === message && !error.message.includes("synthetic-secret"));
    }
  } finally { globalThis.fetch = original; }
});

// Declarative security lint, not an assertion of server authentication behavior.
test("onboarding security lint preserves integrated identity declarations and prohibits credential/raw-HTML fields", async () => {
  const files = ["../src/features/targets/TargetOnboarding.tsx", "../src/App.tsx", "../src/dashboardModel.ts", "../src/features/targets/targetTypes.ts"];
  const source = (await Promise.all(files.map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n");
  const types = await readFile(new URL(files[3], import.meta.url), "utf8");
  assert.match(types, /windows_integrated_service_identity/);
  assert.match(types, /mandatory_validated/);
  assert.doesNotMatch(source, /dangerouslySetInnerHTML/);
  assert.doesNotMatch(source, /(?:password|connectionString|trustServerCertificate)\s*[?:]/i);
});
