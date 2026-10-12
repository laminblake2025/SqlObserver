import test from "node:test";
import assert from "node:assert/strict";
import { parseRuleCatalog, prepareRule } from "../src/features/alerts/alertRuleAdminActions.ts";
import { parseAlertRuleAdminPage } from "../src/features/alerts/alertRuleAdminApi.ts";
import { alertReasonSummary, ruleSignalLabel } from "../src/features/alerts/alertRuleSignals.ts";

const target = "11111111-1111-4111-8111-111111111111";
const template = { name: "deadlocks.count", kind: "deadlock_count_v1", metricId: null, unit: "count",
  comparison: "greater_than", threshold: 0, hysteresis: 0, confirmationCount: 2,
  clearConfirmationCount: 2, confirmationSeconds: 300, evaluationSeconds: 15 };
const catalog = { targetId: target, digest: "a".repeat(64), items: [template] };
const { unit: _unit, ...settings } = template;
const custom = { ...settings, name: "deadlocks.custom", threshold: 3, hysteresis: 1,
  confirmationCount: 3, clearConfirmationCount: 4, enabled: true };
const item = { ...custom, ruleId: "22222222-2222-4222-8222-222222222222", revision: 2,
  updatedAtUtc: "2026-10-01T12:00:00Z" };
const page = { targetId: target, listingStartedUtc: "2026-10-01T12:01:00Z", items: [item], hasMore: false, nextCursor: null };

test("Deadlock catalog, forms and rule pages preserve integer counts and distinct confirmation settings", () => {
  assert.deepEqual(parseRuleCatalog(catalog, target), [template]);
  assert.deepEqual(prepareRule(custom, template), custom);
  assert.equal(parseAlertRuleAdminPage(page, target).items[0].threshold, 3);
  assert.equal(ruleSignalLabel(template.kind, null), "Deadlock count");
  for (const evaluationSeconds of [15, 60]) {
    const boundary = { ...custom, evaluationSeconds, threshold: 256, hysteresis: 256 };
    assert.deepEqual(prepareRule(boundary, template), boundary);
  }
});

test("Deadlock evidence gaps describe recovery actions and complete zero remains visible", () => {
  const reason = key => `deadlock_count_v1|target=2|reason=${key}|digest=${"a".repeat(64)}`;
  assert.match(alertReasonSummary(reason("current"), 0), /five-minute window: 0/);
  for (const key of ["collector_disabled", "source_unavailable", "source_stale", "source_incomplete", "source_inconsistent"]) {
    assert.match(alertReasonSummary(reason(key)), /alert remains open/);
    assert.doesNotMatch(alertReasonSummary(reason(key)), /digest=|target=|deadlock_count_v1/);
  }
});

test("Deadlock mutations and parsers reject fractional counts, incompatible bindings and invalid timing", () => {
  for (const change of [{ threshold: -1 }, { threshold: 257 }, { threshold: 0.5 }, { threshold: NaN },
    { hysteresis: 4 }, { hysteresis: 0.5 }, { comparison: "less_than" },
    { metricId: "engine.user_connections" }, { evaluationSeconds: 14 }, { evaluationSeconds: 61 },
    { confirmationSeconds: 10 }, { confirmationCount: 101 }, { clearConfirmationCount: 101 }]) {
    assert.throws(() => prepareRule({ ...custom, ...change }, template));
    assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [{ ...item, ...change }] }, target));
    assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, ...custom, unit: "count", ...change }] }, target));
  }
  assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, unit: "seconds" }] }, target));
});
