import assert from "node:assert/strict";
import test from "node:test";
import { responseFailure, failureReference } from "../src/api/responseFailure.ts";
import { investigationWindow, windowLimitMessage } from "../src/investigationWindow.ts";
import { activityHistoryHref, readRoute } from "../src/dashboardModel.ts";
import { readOverviewScope } from "../src/features/overview/overviewModel.ts";
import { getDeadlocks } from "../src/features/deadlocks/deadlockApi.ts";
import { parseActiveAlerts } from "../src/features/alerts/alertRuntimeParser.ts";

const target = "11111111-1111-4111-8111-111111111111";
const window = { fromUtc: "2026-09-20T01:00:00.000Z", toUtc: "2026-09-21T01:00:00.000Z" };

test("deadlock continuation sends the same explicit UTC bounds as its first page", async () => {
  const original = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async url => {
    requests.push(new URL(url, "http://localhost"));
    return new Response(JSON.stringify({ targetId: target, repositoryTimeUtc: window.toUtc, items: [], nextCursor: requests.length === 1 ? "Y3Vyc29y" : null }));
  };
  try {
    const first = await getDeadlocks(target, new AbortController().signal, undefined, window);
    await getDeadlocks(target, new AbortController().signal, first.nextCursor, window);
    for (const request of requests) {
      assert.equal(request.searchParams.get("fromUtc"), window.fromUtc);
      assert.equal(request.searchParams.get("toUtc"), window.toUtc);
    }
    assert.equal(requests[1].searchParams.get("cursor"), "Y3Vyc29y");
  } finally { globalThis.fetch = original; }
});

test("time context survives a deadlock-to-activity link including its exact event selection", () => {
  const scopeHash = `#/deadlocks?target=${target}&range=custom&from=${window.fromUtc}&to=${window.toUtc}&compare=1`;
  const eventId = "22222222-2222-4222-8222-222222222222";
  const href = activityHistoryHref(target, window.toUtc, eventId, scopeHash);
  assert.deepEqual(readOverviewScope(href), readOverviewScope(scopeHash));
  assert.equal(readRoute(href).activityEventId, eventId);
  assert.equal(readRoute(href).activityAtUtc, window.toUtc);
});

test("feature limits reject overlong history without silently narrowing the investigation", () => {
  const result = investigationWindow({target, range: "7d", compare: false}, Date.parse(window.toUtc));
  assert.equal(Date.parse(result.window.toUtc) - Date.parse(result.window.fromUtc), 7 * 86400000);
  assert.match(windowLimitMessage(result.window, 1, "Activity history"), /24 hours/);
  assert.equal(windowLimitMessage(result.window, 7, "Report"), undefined);
  assert.match(investigationWindow({target, range: "custom", from: "invalid", to: window.toUtc, compare: false}, Date.parse(window.toUtc)).error, /valid UTC range/);
});

test("safe request references are copyable without reading provider failure content", () => {
  const response = new Response("provider-secret and unrestricted SQL", {status: 500, headers: {"X-Correlation-ID": "trace-123"}});
  const failure = responseFailure(response, "Evidence unavailable.");
  assert.equal(failureReference(failure.message), "trace-123");
  assert.equal(response.bodyUsed, false);
  assert.doesNotMatch(failure.message, /provider-secret|SQL/);
  for (const reference of ["<script>", "a".repeat(129), "secret=value"]) {
    assert.equal(responseFailure(new Response(null, {headers: {"X-Correlation-ID": reference}}), "Unavailable.").message, "Unavailable.");
  }
});

test("alert names remain optional, bounded, and inert diagnostic strings", () => {
  const item = { alertId: "22222222-2222-4222-8222-222222222222", ruleId: "33333333-3333-4333-8333-333333333333", state: "firing", firstObservedUtc: window.fromUtc, deliverySuppressed: false };
  const parse = value => parseActiveAlerts({targetId: target, items: [{...item,...value}]}, target).items[0];
  assert.equal(parse({}).ruleName, undefined);
  assert.equal(parse({ruleName: "<script>diagnostic text</script>"}).ruleName, "<script>diagnostic text</script>");
  assert.throws(() => parse({ruleName: "x".repeat(201)}), /invalid/);
  assert.throws(() => parse({ruleName: {text: "bad"}}), /invalid/);
});
