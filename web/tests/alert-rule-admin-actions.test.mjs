import test from "node:test";
import assert from "node:assert/strict";
import {
  RuleAttempts, parseRuleCatalog, prepareRule, createRule, updateRule, disableRule, getRuleCatalog,
} from "../src/features/alerts/alertRuleAdminActions.ts";

const target = "11111111-1111-4111-8111-111111111111";
const ruleId = "33333333-3333-4333-8333-333333333333";
const template = {
  name: "metric.threshold", kind: "metric_threshold", metricId: "engine.user_connections",
  unit: "count", comparison: "greater_than_or_equal", threshold: 1, hysteresis: 0,
  confirmationCount: 1, confirmationSeconds: 0, evaluationSeconds: 15, clearConfirmationCount: 2,
};
const values = { ...template, enabled: true };
delete values.unit;

test("catalog parser binds templates to target and enforces bounds", () => {
  const catalog = { targetId: target, digest: "a".repeat(64), items: [template] };
  assert.deepEqual(parseRuleCatalog(catalog, target), [template]);
  assert.throws(() => parseRuleCatalog({ ...catalog, targetId: ruleId }, target), /invalid/);
  assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, metricId: null }] }, target), /invalid/);
  assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, evaluationSeconds: -1 }] }, target), /invalid/);
});

test("catalog read is target-bound and reports missing admin role", async () => {
  const previous = globalThis.fetch;
  try {
    let request;
    globalThis.fetch = async (url, options) => {
      request = { url, options };
      return new Response(JSON.stringify({ targetId: target, digest: "a".repeat(64), items: [template] }),
        { status: 200, headers: { "content-type": "application/json" } });
    };
    const signal = new AbortController().signal;
    assert.deepEqual(await getRuleCatalog(target, signal), [template]);
    assert.match(request.url, new RegExp(`/observation-targets/${target}/alerts/rules/catalog$`));
    assert.ok(request.options.signal instanceof AbortSignal); assert.equal(request.options.signal.aborted, signal.aborted);
    assert.equal(request.options.credentials, "same-origin");
    globalThis.fetch = async () => new Response(null, { status: 403 });
    await assert.rejects(() => getRuleCatalog(target, signal), /Target Administrator/);
  } finally { globalThis.fetch = previous; }
});

test("rule preparation enforces catalog identity and approved threshold semantics", () => {
  assert.deepEqual(prepareRule(values, template), values);
  assert.throws(() => prepareRule({ ...values, metricId: "other.metric" }, template), /approved/);
  assert.throws(() => prepareRule({ ...values, hysteresis: 2 }, template), /threshold/);
  assert.throws(() => prepareRule({ ...values, evaluationSeconds: 14 }, template), /threshold/);
  const health = { ...template, name: "collector.health", kind: "collector_health", metricId: null,
    comparison: "less_than", confirmationCount: 2, confirmationSeconds: 300, evaluationSeconds: 30 };
  const healthValues = { ...health, enabled: true }; delete healthValues.unit;
  assert.deepEqual(prepareRule(healthValues, health), healthValues);
  assert.throws(() => prepareRule({ ...healthValues, threshold: 2 }, health), /template/);
});

test("rule mutations reuse identities and send revision-bound disable", async () => {
  const attempts = new RuleAttempts();
  const first = attempts.createFor(target, values);
  assert.deepEqual(attempts.createFor(target, values), first);
  assert.notDeepEqual(attempts.createFor(target, { ...values, threshold: 2 }), first);
  const operationId = attempts.disableFor(target, ruleId, 4);
  assert.equal(attempts.disableFor(target, ruleId, 4), operationId);
  assert.notEqual(attempts.disableFor(target, ruleId, 5), operationId);
  const updateOperation = attempts.updateFor(target, ruleId, 4, values);
  assert.equal(attempts.updateFor(target, ruleId, 4, values), updateOperation);
  assert.notEqual(attempts.updateFor(target, ruleId, 5, values), updateOperation);
  const prior = globalThis.fetch;
  try {
    const requests = [];
    globalThis.fetch = async (url, options) => {
      requests.push({ url, options, body: JSON.parse(options.body) });
      return new Response(null, { status: 200 });
    };
    const signal = new AbortController().signal;
    await createRule(target, values, first, signal);
    await updateRule(target, ruleId, 4, values, updateOperation, signal);
    await disableRule(target, { ...values, ruleId, revision: 4 }, operationId, signal);
    assert.equal(requests[0].options.method, "POST");
    assert.deepEqual(requests[0].body, { ...values, ...first });
    assert.match(requests[0].url, /\/alerts\/rules$/);
    assert.equal(requests[1].options.method, "PUT");
    assert.deepEqual(requests[1].body, { ...values, ruleId, operationId: updateOperation, expectedRevision: 4 });
    assert.match(requests[1].url, /\/alerts\/rules\/33333333-3333-4333-8333-333333333333$/);
    assert.deepEqual(requests[2].body, { ...values, ruleId, revision: 4,
      operationId, expectedRevision: 4, enabled: false });
    assert.match(requests[2].url, /\/alerts\/rules\/33333333-3333-4333-8333-333333333333\/disable$/);
    globalThis.fetch = async () => new Response(null, { status: 409 });
    await assert.rejects(() => disableRule(target, { ...values, ruleId, revision: 4 }, operationId, signal), /changed/);
  } finally { globalThis.fetch = prior; }
  attempts.completeCreate(target, values);
  assert.notDeepEqual(attempts.createFor(target, values), first);
  attempts.completeUpdate(target, ruleId, 4, values);
  assert.notEqual(attempts.updateFor(target, ruleId, 4, values), updateOperation);
  attempts.completeDisable(target, ruleId, 4);
  assert.notEqual(attempts.disableFor(target, ruleId, 4), operationId);
});
