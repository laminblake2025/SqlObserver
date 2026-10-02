import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { registerObservationTarget } from "../src/features/targets/targetApi.ts";
import { registrationRequest, registeredTarget } from "../component-tests/onboardingFixture.mjs";

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

// Composition lint: isolated component mounting does not exercise App's update
// callback. Preserve its candidate identity de-duplication wiring explicitly.
test("onboarding composition lint preserves App candidate identity wiring", async () => {
  const source = await readFile(new URL("../src/App.tsx", import.meta.url), "utf8");
  assert.match(source, /candidate\.instanceId !== target\.instanceId/);
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
