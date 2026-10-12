import assert from "node:assert/strict";
import test from "node:test";
import { parseRetentionPolicy, parseRetentionPreview } from "../src/features/analytics/analyticsParser.ts";
import { detachRetention, dropRetention, updateRetentionPolicy } from "../src/features/analytics/analyticsApi.ts";

test("receipt policy and checkpoint holds remain visible in retention administration", () => {
  const policy = { policy: { dataClass: "m6_deadlock_read_receipts", enabled: true, retainFor: "30.00:00:00", minimumPartitionsToKeep: 2 }, revision: 1, readAtUtc: "2026-10-01T12:00:00Z" };
  assert.equal(parseRetentionPolicy(policy).policy.dataClass, "m6_deadlock_read_receipts");
  const entry = { dataClass: "m6_deadlock_read_receipts", parentSchema: "events", parentTable: "deadlock_read_receipt", partitionName: "deadlock_read_receipt_p202606", rangeStartUtc: "2026-06-01T00:00:00Z", rangeEndUtc: "2026-07-01T00:00:00Z", eligible: false, reason: "checkpoint_active" };
  const preview = { entries: [entry], truncated: false, nextCursor: null, evaluatedAtUtc: policy.readAtUtc };
  assert.equal(parseRetentionPreview(preview).entries[0].reason, "checkpoint_active");
  assert.throws(() => parseRetentionPreview({ ...preview, entries: [{ ...entry, eligible: true }] }));
  assert.throws(() => parseRetentionPreview({ ...preview, entries: [{ ...entry, reason: "invented_hold" }] }));
});

for (const dataClass of ["m6_deadlock_read_receipts", "collection_run_canonical"]) {
  test(`${dataClass} policy uses PUT while manual cleanup is rejected before fetch`, async () => {
    const originalFetch = globalThis.fetch;
    const calls = [];
    const body = { dataClass, enabled: true, retainFor: dataClass === "collection_run_canonical" ? "90.00:00:00" : "30.00:00:00", minimumPartitionsToKeep: dataClass === "collection_run_canonical" ? 1 : 2, expectedRevision: 1, changeReason: "Retention policy test" };
    globalThis.fetch = async (url, options) => {
      calls.push({ url, options });
      return new Response(JSON.stringify({ policy: { dataClass: body.dataClass, enabled: body.enabled, retainFor: body.retainFor, minimumPartitionsToKeep: body.minimumPartitionsToKeep }, revision: 2, readAtUtc: "2026-10-01T12:00:00Z" }), { headers: { "Content-Type": "application/json" } });
    };
    try {
      const signal = new AbortController().signal;
      assert.equal((await updateRetentionPolicy(body.dataClass, body, signal)).revision, 2);
      assert.equal(calls.length, 1);
      assert.equal(calls[0].url, `/api/v1/retention/policies/${dataClass}`);
      assert.equal(calls[0].options.method, "PUT");
      const id = "11111111-1111-4111-8111-111111111111";
      const cleanup = { dataClass: body.dataClass, parentSchema: "events", parentTable: "deadlock_read_receipt", partitionName: "deadlock_read_receipt_p202606", operationId: id, executionId: id, expectedPolicyRevision: 1, changeReason: "Blocked manual route", correlationId: id, requestDigest: "a".repeat(64) };
      assert.throws(() => detachRetention(cleanup, signal), /Invalid dataClass/);
      assert.throws(() => dropRetention(cleanup, signal), /Invalid dataClass/);
      assert.equal(calls.length, 1);
    } finally {
      globalThis.fetch = originalFetch;
    }
  });
}
