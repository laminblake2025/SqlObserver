import test from "node:test";
import assert from "node:assert/strict";
import { getAlertRulesForAdmin, parseAlertRuleAdminPage } from "../src/features/alerts/alertRuleAdminApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const rule = {
  ruleId: "33333333-3333-4333-8333-333333333333", name: "sql.cpu.high", kind: "metric_threshold",
  metricId: "engine.cpu.percent", comparison: "greater_than", threshold: 80, hysteresis: 5,
  confirmationCount: 3, confirmationSeconds: 60, evaluationSeconds: 30,
  clearConfirmationCount: 2, enabled: false, revision: 4, updatedAtUtc: "2026-09-28T12:00:00Z",
};
const page = {
  targetId: target, listingStartedUtc: "2026-09-29T12:00:00Z", items: [rule],
  hasMore: true, nextCursor: "YWJjZA==",
};

test("admin rule pages retain disabled rules, revision, and bounded continuation", () => {
  assert.deepEqual(parseAlertRuleAdminPage(page, target), { ...page, items: [rule] });
  assert.deepEqual(parseAlertRuleAdminPage({ ...page, items: [{ ...rule, kind: "collector_health", metricId: null }], hasMore: false, nextCursor: null }, target).nextCursor, undefined);
  assert.throws(() => parseAlertRuleAdminPage(page, other), /invalid/);
  assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [rule, rule] }, target), /invalid/);
  assert.throws(() => parseAlertRuleAdminPage({ ...page, hasMore: false }, target), /invalid/);
  assert.throws(() => parseAlertRuleAdminPage({ ...page, nextCursor: "x".repeat(1025) }, target), /invalid/);
  assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [{ ...rule, hysteresis: -1 }] }, target), /invalid/);
  assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [{ ...rule, clearConfirmationCount: 0 }] }, target), /invalid/);
});

test("admin rule reads use a target-bound GET and surface missing role", async () => {
  const prior = globalThis.fetch;
  try {
    let request;
    globalThis.fetch = async (url, options) => {
      request = { url, options };
      return new Response(JSON.stringify(page), { status: 200, headers: { "content-type": "application/json" } });
    };
    const signal = new AbortController().signal;
    assert.equal((await getAlertRulesForAdmin(target, signal, page.nextCursor)).items[0].enabled, false);
    assert.match(request.url, new RegExp(`/observation-targets/${target}/alerts/rules\\?limit=100&cursor=`));
    assert.equal(request.options.credentials, "same-origin");
    assert.ok(request.options.signal instanceof AbortSignal); assert.equal(request.options.signal.aborted, signal.aborted);
    globalThis.fetch = async () => new Response(null, { status: 403 });
    await assert.rejects(() => getAlertRulesForAdmin(target, signal), /Target Administrator/);
  } finally { globalThis.fetch = prior; }
});
