import assert from "node:assert/strict";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { localDateTimeToUtc, utcDateTimeControlValue, createReport, validateReportWindow } from "../src/features/reports/reportsApi.ts";

test("reports workflow exposes four fixed report kinds and safe text-only rendering", () => {
  const panel = readFileSync(new URL("../src/features/reports/ReportsPanel.tsx", import.meta.url), "utf8");
  assert.match(panel, /instance-health/);
  assert.match(panel, /performance-window/);
  assert.match(panel, /incident-evidence/);
  assert.match(panel, /capacity-readiness/);
  assert.doesNotMatch(panel, /dangerouslySetInnerHTML/);
  assert.match(panel, /Open printable HTML/);
  assert.match(panel, /Download section CSV/);
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
  assert.equal(localDateTimeToUtc("2026-08-01T13:45:01"), "2026-08-01T13:45:01.000Z");
  assert.throws(() => localDateTimeToUtc("2026-08-01T13:45:60"));
  assert.throws(() => localDateTimeToUtc("2026-08-01T13:45:01.1234"));
});

test("report prefill and submission preserve an inherited subminute window exactly", async () => {
  const from = "2026-08-01T13:45:01.125Z";
  const to = "2026-08-01T13:45:01.875Z";
  assert.equal(utcDateTimeControlValue(from), "2026-08-01T13:45:01.125");
  const fromUtc = localDateTimeToUtc(utcDateTimeControlValue(from));
  const toUtc = localDateTimeToUtc(utcDateTimeControlValue(to));
  assert.equal(fromUtc, from);
  assert.equal(toUtc, to);
  const original = globalThis.fetch;
  let body;
  globalThis.fetch = async (_url, request) => {
    body = JSON.parse(request.body);
    return new Response(JSON.stringify({runId: "report-run"}));
  };
  try {
    await createReport("target", "performance-window", new AbortController().signal, fromUtc, toUtc);
    assert.equal(body.fromUtc, from);
    assert.equal(body.toUtc, to);
  } finally { globalThis.fetch = original; }
});
