import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { createReport, localDateTimeToUtc, validateReportWindow } from "../src/features/reports/reportsApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const runId = "22222222-2222-4222-8222-222222222222";
const kinds = ["instance-health", "performance-window", "incident-evidence", "capacity-readiness"];
const digest = (kind, fromUtc, toUtc) => createHash("sha256").update(JSON.stringify(fromUtc === undefined ? { reportKind: kind }
  : { fromUtc, reportKind: kind, toUtc })).digest("hex");
const receipt = (overrides = {}) => ({ runId, targetId: target, targetRevision: 7, kind: 0,
  definitionVersion: 1, snapshotUtc: "2026-08-24T13:00:00.000001Z", expiresAtUtc: "2026-08-25T13:00:00.000001Z",
  parameterDigest: digest(kinds[overrides.kind ?? 0], ...(overrides.kind ? ["2026-08-01T00:00:00.0000000+00:00", "2026-08-02T00:00:00.0000000+00:00"] : [])), state: "finalized", ...overrides });
async function withFetch(handler, run) {
  const original = globalThis.fetch;
  globalThis.fetch = handler;
  try { await run(); } finally { globalThis.fetch = original; }
}

test("reports architecture security lint forbids raw HTML insertion", () => {
  const panel = readFileSync(new URL("../src/features/reports/ReportsPanel.tsx", import.meta.url), "utf8");
  assert.doesNotMatch(panel, /dangerouslySetInnerHTML/);
});

test("report windows enforce UTC half-open limits", () => {
  assert.doesNotThrow(() => validateReportWindow("instance-health"));
  assert.throws(() => validateReportWindow("instance-health", "2026-08-01T00:00:00Z", "2026-08-01T01:00:00Z"));
  assert.doesNotThrow(() => validateReportWindow("performance-window", "2026-08-01T00:00:00Z", "2026-08-08T00:00:00Z"));
  assert.throws(() => validateReportWindow("performance-window", "2026-08-01T00:00:00Z", "2026-08-08T00:00:01Z"));
  assert.doesNotThrow(() => validateReportWindow("capacity-readiness", "2026-08-01T00:00:00Z", "2026-09-01T00:00:00Z"));
});

test("datetime-local values use UTC independent of browser timezone", () => {
  assert.equal(localDateTimeToUtc("2026-08-01T13:45"), "2026-08-01T13:45:00.000Z");
  assert.throws(() => localDateTimeToUtc("2026-02-30T13:45"));
  assert.throws(() => localDateTimeToUtc("2026-08-01T13:45:01"));
});

test("report generation accepts native numeric kinds and bounded finalized receipts", async () => {
  for (const [kind, numericKind] of [["instance-health", 0], ["performance-window", 1], ["incident-evidence", 2], ["capacity-readiness", 3]]) {
    await withFetch(async (url, options) => {
      assert.equal(url, `/api/v1/observation-targets/${target}/reports`);
      assert.equal(options.method, "POST");
      assert.equal(options.credentials, "same-origin");
      assert.equal(new Headers(options.headers).get("content-type"), "application/json");
      const body = JSON.parse(options.body);
      assert.equal(body.reportKind, kind);
      assert.match(body.operationId, /^[0-9a-f-]{36}$/i);
      assert.deepEqual(Object.keys(body).sort(), kind === "instance-health" ? ["operationId", "reportKind"] : ["fromUtc", "operationId", "reportKind", "toUtc"]);
      return new Response(JSON.stringify(receipt({ kind: numericKind })), { headers: { "content-type": "application/json" } });
    }, async () => {
      const result = await createReport(target, kind, new AbortController().signal,
        ...(kind === "instance-health" ? [] : ["2026-08-01T00:00:00Z", "2026-08-02T00:00:00Z"]));
      assert.equal(result.kind, numericKind); assert.equal(result.runId, runId); assert.equal(result.state, "finalized");
    });
  }
});

test("report generation rejects malformed, cross-target, wrong-kind and unfinished receipts", async () => {
  const invalid = [null, [], "SECRET_BACKEND_DETAIL", {},
    receipt({ targetId: runId }), receipt({ runId: "../../html" }), receipt({ runId: "00000000-0000-0000-0000-000000000000" }),
    receipt({ kind: 1 }), receipt({ kind: "instance-health" }), receipt({ kind: undefined, reportKind: "instance-health" }),
    receipt({ state: "ready" }), receipt({ state: "failed" }), receipt({ state: "<img src=x onerror=alert(1)>" }),
    receipt({ targetRevision: 0 }), receipt({ targetRevision: Number.MAX_SAFE_INTEGER + 1 }), receipt({ definitionVersion: 2 }),
    receipt({ parameterDigest: "secret" }), receipt({ snapshotUtc: "2026-02-30T13:00:00Z" }),
    receipt({ snapshotUtc: "2026-08-24T13:00:00+01:00" }), receipt({ expiresAtUtc: "2026-08-24T12:00:00Z" }),
    receipt({ expiresAtUtc: "2026-08-25T13:00:00.000002Z" }), receipt({ snapshotUtc: "2026-08-24T13:00:00.0000001Z" })];
  for (const body of invalid) await withFetch(async () => new Response(JSON.stringify(body)), async () => {
    await assert.rejects(() => createReport(target, "instance-health", new AbortController().signal),
      error => error.message === "The report response could not be verified.");
  });
});

test("report generation bounds success bytes and hides invalid JSON or transport details", async () => {
  for (const response of [
    () => new Response(JSON.stringify(receipt()) + " ".repeat(16385)),
    () => new Response(JSON.stringify(receipt()), { headers: { "content-length": "16385" } }),
    () => new Response("<html>SECRET_BACKEND_DETAIL</html>"),
  ]) await withFetch(async () => response(), async () => {
    await assert.rejects(() => createReport(target, "instance-health", new AbortController().signal),
      error => error.message === "The report response could not be verified.");
  });
});

test("report requests reject invalid identities, kinds and impossible UTC windows before IO", async () => {
  let calls = 0;
  await withFetch(async () => { calls++; return new Response(JSON.stringify(receipt())); }, async () => {
    for (const id of ["../../other", "00000000-0000-0000-0000-000000000000"])
      await assert.rejects(() => createReport(id, "instance-health", new AbortController().signal));
    await assert.rejects(() => createReport(target, "arbitrary", new AbortController().signal));
    await assert.rejects(() => createReport(target, "performance-window", new AbortController().signal, "2026-02-30T00:00:00Z", "2026-03-03T00:00:00Z"));
    assert.equal(calls, 0);
  });
});

test("report receipt digest binds the exact UTC request window including microseconds", async () => {
  const fromUtc = "2026-08-01T00:00:00.000001Z", toUtc = "2026-08-02T00:00:00.000002Z";
  const expected = digest("performance-window", "2026-08-01T00:00:00.0000010+00:00", "2026-08-02T00:00:00.0000020+00:00");
  await withFetch(async () => new Response(JSON.stringify(receipt({ kind: 1, parameterDigest: expected }))), async () => {
    assert.equal((await createReport(target, "performance-window", new AbortController().signal, fromUtc, toUtc)).parameterDigest, expected);
  });
  await withFetch(async () => new Response(JSON.stringify(receipt({ kind: 1 }))), async () => {
    await assert.rejects(() => createReport(target, "performance-window", new AbortController().signal, fromUtc, toUtc),
      error => error.message === "The report response could not be verified.");
  });
});
