import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { analyticsSurfaceLimits, getAnalyticsSurface } from "../src/features/analytics/analyticsApi.ts";
import { analyticsEmptyStateText, analyticsScopeText, analyticsStatusText } from "../src/features/analytics/analyticsScope.ts";
import { panelStateForRequestError } from "../src/features/analytics/analyticsState.ts";

const target = "11111111-1111-4111-8111-111111111111";
const fromUtc = "2026-09-08T00:00:00Z";
const toUtc = "2026-09-09T00:00:00Z";
const at = "2026-09-09T00:00:00Z";
const surfaces = ["incidents", "jobs", "backfill", "host/status", "host/metrics", "replication/status", "replication/evidence", "diagnostics/search", "evidence-packets"];

function paged(surface) {
  return { targetId: target, surface: surface, fromUtc, toUtc, items: [], state: "no_data", nextCursor: null, cutoffUtc: at, snapshotUtc: at, generation: 1, targetRevision: 1 };
}

test("analytics surface defaults stay within every server route bound", async () => {
  const originalFetch = globalThis.fetch;
  const seen = [];
  try {
    globalThis.fetch = async request => {
      const url = new URL(String(request), "https://example.test");
      seen.push(url);
      if (url.pathname.endsWith("/incidents")) return new Response(JSON.stringify({ targetId: target, fromUtc, toUtc, items: [{ threadId: "22222222-2222-4222-8222-222222222222", openedAtUtc: at, closedAtUtc: null, currentGeneration: 1, packets: [{ providerSecret: "must not reach the UI" }], generations: [] }], state: "complete" }), { status: 200, headers: { "content-type": "application/json" } });
      const surface = decodeURIComponent(url.pathname.split("/analytics/")[1]);
      return new Response(JSON.stringify(paged(surface)), { status: 200, headers: { "content-type": "application/json" } });
    };
    for (const surface of surfaces) {
      const page = await getAnalyticsSurface(target, surface, new AbortController().signal);
      assert.equal(page.targetId, target);
      assert.equal(page.surface, surface);
      assert.equal(page.nextCursor, null);
      assert.equal(page.fromUtc, fromUtc);
      assert.equal(page.toUtc, toUtc);
      if (surface === "incidents") {
        assert.deepEqual(page.items[0], { threadId: "22222222-2222-4222-8222-222222222222", openedAtUtc: at, closedAtUtc: null, currentGeneration: 1 });
      }
    }
    assert.equal(seen.length, surfaces.length);
    for (const [index, surface] of surfaces.entries()) {
      const url = seen[index];
      assert.equal(url.searchParams.get("limit"), String(analyticsSurfaceLimits[surface]));
      assert.equal(url.searchParams.has("fromUtc"), false);
      assert.equal(url.searchParams.has("toUtc"), false);
      assert.equal(url.searchParams.has("cursor"), false);
    }
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("analytics paging preserves the server window and opaque cursor", async () => {
  const originalFetch = globalThis.fetch;
  let seen;
  try {
    globalThis.fetch = async request => {
      seen = new URL(String(request), "https://example.test");
      return new Response(JSON.stringify({ ...paged("host/metrics"), state: "complete" }), { status: 200, headers: { "content-type": "application/json" } });
    };
    await getAnalyticsSurface(target, "host/metrics", new AbortController().signal, { fromUtc, toUtc, cursor: "opaque/page|2" });
    assert.equal(seen.searchParams.get("fromUtc"), fromUtc);
    assert.equal(seen.searchParams.get("toUtc"), toUtc);
    assert.equal(seen.searchParams.get("cursor"), "opaque/page|2");
    assert.equal(seen.searchParams.get("limit"), "200");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("initial request failures remain actionable while cursor failures offer restart", () => {
  assert.equal(panelStateForRequestError(400, false), "degraded");
  assert.equal(panelStateForRequestError(400, true), "cursor-invalid");
  assert.equal(panelStateForRequestError(403, false), "permission-denied");
  assert.equal(panelStateForRequestError(404, false), "no-data");
  assert.equal(panelStateForRequestError(409, false), "stale");
});

test("analytics surface panel exposes retry and restart actions", async () => {
  const panel = await readFile(new URL("../src/features/analytics/AnalyticsSurfacePanel.tsx", import.meta.url), "utf8");
  assert.match(panel, /Retry/);
  assert.match(panel, /Restart paging/);
  assert.match(panel, /<RequestStatus/);
  const status = await readFile(new URL("../src/components/RequestStatus.tsx", import.meta.url), "utf8");
  assert.match(status, /role=\{error \? "alert" : "status"\}/);
});

test("jobs and backfill identify inventory scope without claiming UTC row filtering", () => {
  const page = { fromUtc, toUtc, snapshotUtc: at, cutoffUtc: at };
  for (const surface of ["jobs", "backfill"]) {
    const text = analyticsScopeText(surface, page);
    assert.match(text, surface === "backfill" ? /^Backfill job inventory\./ : /^Job inventory\./);
    assert.match(text, /not filtered/);
    assert.match(text, /Snapshot: 2026-09-09T00:00:00Z/);
    assert.doesNotMatch(text, /2026-09-08T00:00:00Z/);
    assert.equal(analyticsEmptyStateText(surface), surface === "backfill" ? "No backfill jobs are available in this response." : "No job inventory rows are available in this response.");
  }
  const metricText = analyticsScopeText("host/metrics", page);
  assert.match(metricText, /^UTC window:/);
  assert.doesNotMatch(metricText, /not filtered/);
});

test("job no-data status uses inventory wording before a page exists", () => {
  const messages = { "no-data": "No data is available for this window." };
  assert.equal(analyticsStatusText("jobs", "no-data", null, messages), "No job inventory rows are available in this response.");
  assert.equal(analyticsStatusText("backfill", "no-data", null, messages), "No backfill jobs are available in this response.");
  assert.equal(analyticsStatusText("host/metrics", "no-data", null, messages), "No data is available for this window.");
  assert.equal(analyticsStatusText("jobs", "no-data", "Request failed.", messages), "Request failed.");
});
