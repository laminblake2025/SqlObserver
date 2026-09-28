import assert from "node:assert/strict";
import { test } from "node:test";
import { parseMetricSeries, parseRetentionPolicy, parseRetentionPreview, parseRollupPage } from "../src/features/analytics/analyticsParser.ts";
import { detachRetention, updateRetentionPolicy } from "../src/features/analytics/analyticsApi.ts";

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

test("diagnostic-event grace preview and detach client share the active data class", async () => {
  const base = { dataClass: "diagnostic_events", parentSchema: "events",
    parentTable: "diagnostic_event", partitionName: "diagnostic_event_p202608",
    rangeStartUtc: "2026-08-01T00:00:00Z", rangeEndUtc: "2026-09-01T00:00:00Z" };
  for (const reason of ["grace_in_progress", "retention_failed"]) {
    const preview = parseRetentionPreview({ entries: [{ ...base, eligible: false, reason }],
      truncated: false, nextCursor: null, evaluatedAtUtc: "2026-09-28T01:00:00Z" });
    assert.equal(preview.entries[0].reason, reason);
  }
  const executionId = "22222222-2222-4222-8222-222222222222";
  const body = { operation: "detach", operationId: "11111111-1111-4111-8111-111111111111",
    executionId, dataClass: base.dataClass, parentSchema: base.parentSchema,
    parentTable: base.parentTable, partitionName: base.partitionName,
    expectedPolicyRevision: 2, changeReason: "Reviewed event retention",
    correlationId: "33333333-3333-4333-8333-333333333333", requestDigest: "a".repeat(64) };
  const originalFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (path, options) => {
    calls.push({ path, options });
    return Response.json({ executionId, state: 4, blockReason: null,
      dropAfterUtc: "2026-09-29T01:00:00Z", failureCode: null });
  };
  try {
    const result = await detachRetention(body, new AbortController().signal);
    assert.equal(result.state, 4);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].path, "/api/v1/retention/detach");
    assert.equal(calls[0].options.credentials, "same-origin");
  } finally {
    globalThis.fetch = originalFetch;
  }
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

test("core evidence policies can be updated through the bounded client", async () => {
  const originalFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (path, options) => {
    calls.push({ path, options });
    const request = JSON.parse(options.body);
    return new Response(JSON.stringify({
      policy: { dataClass: request.dataClass, enabled: true,
        retainFor: request.retainFor, minimumPartitionsToKeep: 2 },
      revision: 3, readAtUtc: "2026-09-28T01:00:00Z",
    }), { status: 200, headers: { "Content-Type": "application/json" } });
  };
  try {
    for (const dataClass of ["raw_metric_samples", "diagnostic_events"]) {
      const result = await updateRetentionPolicy(dataClass, {
        dataClass, enabled: true, retainFor: "30.00:00:00",
        minimumPartitionsToKeep: 2, expectedRevision: 2,
        changeReason: "Capacity policy review",
      }, new AbortController().signal);
      assert.equal(result.policy.dataClass, dataClass);
    }
    assert.deepEqual(calls.map(call => call.path), [
      "/api/v1/retention/policies/raw_metric_samples",
      "/api/v1/retention/policies/diagnostic_events",
    ]);
    assert.ok(calls.every(call => call.options.method === "PUT"
      && call.options.credentials === "same-origin"));
    await assert.rejects(updateRetentionPolicy("unknown", {}, new AbortController().signal));
    assert.equal(calls.length, 2);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("retention parser accepts canonical M5 policies and preview rows", () => {
  const policy = parseRetentionPolicy({ policy: { dataClass: "m5_waits", enabled: false, retainFor: null, minimumPartitionsToKeep: 3 }, revision: 1, readAtUtc: "2026-08-26T01:00:00Z" });
  assert.equal(policy.policy.dataClass, "m5_waits");
  const preview = parseRetentionPreview({ entries: [{ dataClass: "m5_waits", parentSchema: "telemetry", parentTable: "server_wait_snapshot", partitionName: "server_wait_snapshot_20260825", rangeStartUtc: "2026-08-25T00:00:00Z", rangeEndUtc: "2026-08-26T00:00:00Z", eligible: false, reason: "retention_disabled" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-08-26T01:00:00Z" });
  assert.equal(preview.entries[0].parentTable, "server_wait_snapshot");
});

test("retention parser accepts Query Store group policies and registered partitions", () => {
  for (const [dataClass, parentTable, days] of [
    ["m7_group_watermarks", "query_store_group_watermark", 8],
    ["m7_group_deltas", "query_store_group_delta", 30],
  ]) {
    const policy = parseRetentionPolicy({ policy: { dataClass, enabled: true, retainFor: `${days}.00:00:00`, minimumPartitionsToKeep: 3 }, revision: 1, readAtUtc: "2026-09-28T01:00:00Z" });
    assert.equal(policy.policy.dataClass, dataClass);
    const preview = parseRetentionPreview({ entries: [{ dataClass, parentSchema: "events", parentTable, partitionName: `${parentTable}_p20260927`, rangeStartUtc: "2026-09-27T00:00:00Z", rangeEndUtc: "2026-09-28T00:00:00Z", eligible: false, reason: "within_retention_window" }], truncated: false, nextCursor: null, evaluatedAtUtc: "2026-09-28T01:00:00Z" });
    assert.equal(preview.entries[0].parentTable, parentTable);
  }
});
