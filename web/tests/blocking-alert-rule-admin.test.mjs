import test from "node:test";
import assert from "node:assert/strict";
import { parseRuleCatalog, prepareRule } from "../src/features/alerts/alertRuleAdminActions.ts";
import { parseAlertRuleAdminPage } from "../src/features/alerts/alertRuleAdminApi.ts";
import { alertReasonSummary, ruleSignalLabel } from "../src/features/alerts/alertRuleSignals.ts";

const target = "11111111-1111-4111-8111-111111111111";
const template = { name: "blocking.duration", kind: "blocking_duration_v1", metricId: null, unit: "seconds",
  comparison: "greater_than_or_equal", threshold: 60, hysteresis: 10, confirmationCount: 2,
  clearConfirmationCount: 2, confirmationSeconds: 300, evaluationSeconds: 15 };
const catalog = { targetId: target, digest: "a".repeat(64), items: [template] };
const { unit: _unit, ...settings } = template;
const custom = { ...settings, name: "blocking.custom", comparison: "greater_than", threshold: 65.001,
  hysteresis: 5.001, confirmationCount: 3, clearConfirmationCount: 4, evaluationSeconds: 5, enabled: true };
const item = { ...custom, ruleId: "22222222-2222-4222-8222-222222222222", revision: 2,
  updatedAtUtc: "2026-09-30T12:00:00Z" };
const page = { targetId: target, listingStartedUtc: "2026-09-30T12:01:00Z", items: [item], hasMore: false, nextCursor: null };

test("Blocking catalog and admin page preserve seconds, separate confirmations and custom thresholds", () => {
  assert.deepEqual(parseRuleCatalog(catalog, target), [template]);
  assert.deepEqual(prepareRule(custom, template), custom);
  assert.equal(parseAlertRuleAdminPage(page, target).items[0].threshold, 65.001);
  assert.equal(ruleSignalLabel(template.kind, null), "Blocking duration");
  for (const evaluationSeconds of [5, 60]) {
    const boundary = { ...custom, evaluationSeconds, threshold: 86400, hysteresis: 86400 };
    assert.deepEqual(prepareRule(boundary, template), boundary);
  }
});

test("Blocking evidence gaps describe recovery actions without displaying internal source digests", () => {
  const reason = key => `blocking_duration_v1|target=2|reason=${key}|digest=${"a".repeat(64)}`;
  assert.match(alertReasonSummary(reason("current"), 65.001), /65.001 seconds/);
  for (const key of ["collector_disabled", "source_unavailable", "source_stale", "source_incomplete", "source_inconsistent"]) {
    assert.match(alertReasonSummary(reason(key)), /alert remains open/);
    assert.doesNotMatch(alertReasonSummary(reason(key)), /digest=|target=|blocking_duration_v1/);
  }
  assert.equal(alertReasonSummary("engine threshold"), "engine threshold");
  assert.equal(alertReasonSummary(reason("foreign")), reason("foreign"));
});

test("Blocking forms and response parsers reject incompatible signal and timing settings", () => {
  for (const change of [{ threshold: -1 }, { threshold: 86400.001 }, { threshold: NaN },
    { hysteresis: 66 }, { hysteresis: -1 }, { comparison: "less_than" },
    { metricId: "engine.user_connections" }, { evaluationSeconds: 4 }, { evaluationSeconds: 61 },
    { evaluationSeconds: 5.5 }, { confirmationSeconds: 10 }, { confirmationCount: 101 },
    { clearConfirmationCount: 101 }]) {
    assert.throws(() => prepareRule({ ...custom, ...change }, template));
    assert.throws(() => parseAlertRuleAdminPage({ ...page, items: [{ ...item, ...change }] }, target));
    assert.throws(() => parseRuleCatalog({ ...catalog, items: [{ ...template, ...custom, unit: "seconds", ...change }] }, target));
  }
});
