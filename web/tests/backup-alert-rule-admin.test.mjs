import test from "node:test";
import assert from "node:assert/strict";
import { parseRuleCatalog, prepareRule } from "../src/features/alerts/alertRuleAdminActions.ts";
import { parseAlertRuleAdminPage } from "../src/features/alerts/alertRuleAdminApi.ts";
import { ruleSignalLabel, ruleExplanation } from "../src/features/alerts/alertRuleSignals.ts";

const target = "11111111-1111-4111-8111-111111111111";
for (const kind of ["backup_full_policy_v2", "backup_log_policy_v2", "backup_observed_faults_v2"]) {
  test(`${kind} parses and preserves configurable confirmation with fixed count semantics`, () => {
    const template = { name: "backup.policy", kind, metricId: null, unit: "affected databases",
      comparison: "greater_than", threshold: 0, hysteresis: 0, confirmationCount: 2, clearConfirmationCount: 2,
      confirmationSeconds: 300, evaluationSeconds: 30 };
    const catalog = { targetId: target, digest: "a".repeat(64), items: [template] };
    assert.deepEqual(parseRuleCatalog(catalog,target),[template]);
    const { unit: _unit, ...settings } = template;
    const custom = { ...settings, name: "backup.custom", confirmationCount: 3, clearConfirmationCount: 4,
      confirmationSeconds: 600, evaluationSeconds: 60, enabled: true };
    assert.deepEqual(prepareRule(custom,template),custom);
    for (const change of [{ threshold: 1 }, { metricId: "engine.user_connections" }, { comparison: "greater_than_or_equal" },
      { evaluationSeconds: 14 }, { confirmationSeconds: 60 }, { clearConfirmationCount: 12 }])
      assert.throws(() => prepareRule({ ...custom, ...change },template));
    const item = { ...custom, ruleId: "22222222-2222-4222-8222-222222222222", revision: 4, updatedAtUtc: "2026-09-30T12:00:00Z" };
    const page = { targetId: target, listingStartedUtc: "2026-09-30T12:01:00Z", items: [item], hasMore: false, nextCursor: null };
    assert.equal(parseAlertRuleAdminPage(page,target).items[0].kind,kind);
    assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [{ ...item, threshold: 1 }] },target),/invalid/);
    assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, metricId: "engine.user_connections" }] },target),/invalid/);
    assert.doesNotMatch(ruleSignalLabel(kind,null),/Collector health|null/);
    assert.match(ruleExplanation(kind),kind === "backup_observed_faults_v2" ? /cannot prove recovery/ : /unknown evidence/);
  });
}
