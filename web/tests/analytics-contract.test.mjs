import assert from "node:assert/strict";
import { test } from "node:test";
import { parseMetricSeries, parseRetentionPolicy, parseRetentionPreview, parseRollupPage } from "../src/features/analytics/analyticsParser.ts";

test("analytics parser preserves bounded series and rejects oversized pages", () => {
  const value = parseMetricSeries({ targetId: "target", metricKey: "host.cpu.percent", state: "partial", items: [{ observedAtUtc: "2026-08-25T12:00:00Z", value: 42, dimensions: { scope: "aggregate" } }] });
  assert.equal(value.items.length, 1);
  assert.throws(() => parseMetricSeries({ targetId: "target", metricKey: "host.cpu.percent", items: Array.from({ length: 1001 }, () => ({ observedAtUtc: "x", value: 1, dimensions: {} })) }));
});

test("analytics rollup parser rejects non-finite values", () => {
  assert.throws(() => parseRollupPage({ targetId: "target", metricKey: "host.cpu.percent", items: [{ bucketStartUtc: "x", bucketEndUtc: "y", metricKey: "host.cpu.percent", count: 1, mean: "NaN" }], hasMore: false }));
});

test("retention preview enforces the eight-column page and consumable cursor contract", () => {
  const base = { dataClass: "m10_host_metrics", parentSchema: "telemetry", parentTable: "host_metric_snapshot_v2", partitionName: "host_metric_snapshot_v2_20260825", rangeStartUtc: "2026-08-25T00:00:00Z", rangeEndUtc: "2026-08-26T00:00:00Z" };
  const first = parseRetentionPreview({ entries: [{ ...base, eligible: true, reason: "eligible" }], truncated: true, nextCursor: "abc", evaluatedAtUtc: "2026-08-26T01:00:00Z" });
  assert.equal(first.entries[0].reason, "eligible");
  assert.equal(first.nextCursor, "abc");
  assert.throws(() => parseRetentionPreview({ entries: [{ ...base, eligible: true, reason: "retention_disabled" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-08-26T01:00:00Z" }));
  assert.throws(() => parseRetentionPreview({ entries: [{ ...base, eligible: false, reason: "made_up" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-08-26T01:00:00Z" }));
  assert.throws(() => parseRetentionPreview({ entries: [{ ...base, eligible: false, reason: "retention_disabled" }, { ...base, eligible: false, reason: "retention_disabled" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-08-26T01:00:00Z" }));
});

test("retention policy enforces bounded server TimeSpan and floor/revision", () => {
  const base = { policy: { dataClass: "m10_host_metrics", enabled: false, retainFor: null, minimumPartitionsToKeep: 3 }, revision: 1, readAtUtc: "2026-08-26T01:00:00Z" };
  assert.equal(parseRetentionPolicy(base).policy.retainFor, null);
  assert.throws(() => parseRetentionPolicy({ ...base, policy: { ...base.policy, retainFor: "1.00:00:00" } }));
  assert.throws(() => parseRetentionPolicy({ ...base, policy: { ...base.policy, enabled: true } }));
  assert.equal(parseRetentionPolicy({ ...base, policy: { ...base.policy, enabled: true, retainFor: "1.00:00:00" } }).policy.enabled, true);
  assert.throws(() => parseRetentionPolicy({ ...base, policy: { ...base.policy, enabled: true, retainFor: "00:00:00" } }));
  assert.throws(() => parseRetentionPolicy({ ...base, revision: 0 }));
  assert.throws(() => parseRetentionPolicy({ ...base, policy: { ...base.policy, minimumPartitionsToKeep: 0 } }));
});

test("retention parser accepts canonical M5 policies and preview rows", () => {
  const policy = parseRetentionPolicy({ policy: { dataClass: "m5_waits", enabled: false, retainFor: null, minimumPartitionsToKeep: 3 }, revision: 1, readAtUtc: "2026-08-26T01:00:00Z" });
  assert.equal(policy.policy.dataClass, "m5_waits");
  const preview = parseRetentionPreview({ entries: [{ dataClass: "m5_waits", parentSchema: "telemetry", parentTable: "server_wait_snapshot", partitionName: "server_wait_snapshot_20260825", rangeStartUtc: "2026-08-25T00:00:00Z", rangeEndUtc: "2026-08-26T00:00:00Z", eligible: false, reason: "retention_disabled" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-08-26T01:00:00Z" });
  assert.equal(preview.entries[0].parentTable, "server_wait_snapshot");
});
