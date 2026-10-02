import assert from "node:assert/strict";
import test from "node:test";
import { clearApiCache } from "../src/apiClient.ts";
import { getSqlVolumePage } from "../src/features/resources/sqlVolumeApi.ts";
import { parseSqlVolumeResponse, isSqlVolumeCursor, invalidSqlVolumeResponseMessage,
  sqlVolumeMaximumResponseBytes } from "../src/features/resources/sqlVolumeResponseModel.ts";

const target = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const run = "33333333-3333-4333-8333-333333333333";
const at = "2026-10-02T12:00:00.123456Z";
const key = number => number.toString(16).padStart(64, "0");
const binding = { targetRevision: 3 };
const row = (number = 1) => ({ volumeKey: key(number), identityKind: "volume_id", mappedFileCount: 2,
  totalBytes: "9223372036854775807", availableBytes: "9007199254740993", observedAtUtc: at,
  trendState: "estimated", trendSampleDays: 15, daysUntilFull: 5.25 });
const page = () => ({ instanceId: target, targetRevision: 3, snapshotRunId: run, state: "current", reason: "completed",
  completedAtUtc: at, lossKind: "none", minimumLostItems: 0, hasVisibilityGap: false,
  repositoryTimeUtc: at, items: [row()], nextCursor: null });
const parse = payload => parseSqlVolumeResponse(payload, target, binding);
const rejects = payload => assert.throws(() => parse(payload), { message: invalidSqlVolumeResponseMessage });

test("native volume contracts retain exact Int64 capacity and unknown, full, history and unavailable evidence", () => {
  assert.equal(parse(page()).items[0].availableBytes, "9007199254740993");
  for (const trendState of ["capacity_changed", "insufficient_history", "not_depleting", "unstable_trend", "beyond_horizon", "evidence_unavailable"]) {
    const payload = page(); Object.assign(payload.items[0], { trendState, daysUntilFull: null,
      trendSampleDays: trendState === "evidence_unavailable" ? 0 : 5 });
    assert.equal(parse(payload).items[0].trendState, trendState);
  }
  const unknown = page(); Object.assign(unknown.items[0], { identityKind: "file_scoped_unknown", totalBytes: null,
    availableBytes: null, trendState: "capacity_unknown", trendSampleDays: 0, daysUntilFull: null });
  assert.equal(parse(unknown).items[0].totalBytes, null);
  const full = page(); Object.assign(full.items[0], { identityKind: "file_scoped_unknown", availableBytes: "0",
    trendState: "full", trendSampleDays: 0, daysUntilFull: 0 });
  assert.equal(parse(full).items[0].daysUntilFull, 0);
  const empty = page(); Object.assign(empty, { state: "unavailable", reason: "not_collected", snapshotRunId: null,
    completedAtUtc: null, lossKind: null, minimumLostItems: null, hasVisibilityGap: true, items: [] });
  assert.equal(parse(empty).snapshotRunId, null);
  const failed = { ...empty, snapshotRunId: run, completedAtUtc: at, reason: "read_failed" };
  assert.equal(parse(failed).snapshotRunId, run);
  for (const state of ["partial", "stale", "superseded"]) {
    const payload = page(); Object.assign(payload, { state, hasVisibilityGap: true });
    Object.assign(payload.items[0], { trendState: "evidence_unavailable", trendSampleDays: 0, daysUntilFull: null });
    assert.equal(parse(payload).state, state);
  }
});

test("volume pages bind exact server revision, pinned snapshot, ordered keys and opaque continuation", () => {
  const payload = page(); payload.items = [row(2), row(3)]; payload.nextCursor = btoa("next protected cursor");
  assert.equal(parseSqlVolumeResponse(payload, target, { ...binding, snapshotRunId: run,
    afterVolumeKey: key(1), beforeVolumeKey: key(4) }).items.length, 2);
  for (const requested of [{ targetRevision: 4 }, { ...binding, snapshotRunId: other },
    { ...binding, afterVolumeKey: key(2) }, { ...binding, beforeVolumeKey: key(3) }])
    assert.throws(() => parseSqlVolumeResponse(payload, target, requested));
  assert.throws(() => parseSqlVolumeResponse(payload, other, binding));
  assert.throws(() => parseSqlVolumeResponse(payload, target, binding, payload.nextCursor));
  for (const cursor of ["", "c", "YQ=", "YR==", "a".repeat(2049), 3]) assert.equal(isSqlVolumeCursor(cursor), false);
  assert.equal(isSqlVolumeCursor(btoa("opaque")), true);
  const duplicate = page(); duplicate.items = [row(), row()]; rejects(duplicate);
  const descending = page(); descending.items = [row(3), row(2)]; rejects(descending);
  const oversized = page(); oversized.items = Array.from({ length: 26 }, (_, index) => row(index + 1)); rejects(oversized);
});

test("volume parser rejects malformed types, invalid provenance and manufactured capacity estimates", () => {
  const mutations = [p => p.state = "healthy", p => p.reason = "<script>", p => p.instanceId = other,
    p => p.targetRevision = "3", p => p.hasVisibilityGap = true, p => p.lossKind = "future_loss",
    p => p.minimumLostItems = 1002, p => p.minimumLostItems = -1, p => p.items[0].identityKind = ["volume_id"],
    p => p.items[0].volumeKey = "A".repeat(64), p => p.items[0].mappedFileCount = 0,
    p => p.items[0].totalBytes = "9223372036854775808", p => p.items[0].availableBytes = "01",
    p => p.items[0].availableBytes = null, p => p.items[0].totalBytes = "0", p => p.items[0].availableBytes = "9223372036854775808",
    p => p.items[0].trendState = "forecast", p => p.items[0].daysUntilFull = -1,
    p => p.items[0].daysUntilFull = Infinity, p => p.items[0].trendSampleDays = 16,
    p => p.items[0].trendSampleDays = 4, p => p.items[0].daysUntilFull = null,
    p => p.items[0].trendState = "insufficient_history", p => p.items[0].availableBytes = "0",
    p => p.items[0].observedAtUtc = "2026-10-02T12:05:00.123457Z",
    p => p.completedAtUtc = "2026-10-02T12:00:00.1234561Z",
    p => p.repositoryTimeUtc = "2026-02-30T12:00:00Z", p => p.nextCursor = "not a cursor",
    p => { p.snapshotRunId = null; p.completedAtUtc = null; },
    p => { p.state = "partial"; p.hasVisibilityGap = true; p.lossKind = "source_row_limit"; }];
  for (const mutate of mutations) { const payload = page(); mutate(payload); rejects(payload); }
  const microseconds = page(); microseconds.completedAtUtc = "2026-10-02T12:00:00.1234560Z";
  assert.equal(parse(microseconds).completedAtUtc, microseconds.completedAtUtc);
  for (const lossKind of ["source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection",
    "blocking_graph_limit", "duplicate_overlap", "visibility_incomplete"]) {
    const payload = page(); Object.assign(payload, { state: "partial", lossKind, minimumLostItems: 1001, hasVisibilityGap: true });
    Object.assign(payload.items[0], { trendState: "evidence_unavailable", trendSampleDays: 0, daysUntilFull: null });
    assert.equal(parse(payload).lossKind, lossKind);
  }
});

test("volume transport uses bounded JSON and fails safely without exposing foreign rows or raw server errors", async () => {
  const original = globalThis.fetch;
  const requested = [];
  try {
    const controller = new AbortController();
    globalThis.fetch = async (path, init) => { requested.push([path, init]); return Response.json(page()); };
    clearApiCache();
    assert.equal((await getSqlVolumePage(target, controller.signal, undefined, binding)).targetRevision, 3);
    assert.match(requested[0][0], /\/resources\/volumes\?limit=25$/);
    assert.equal(requested[0][1].credentials, "same-origin");
    for (const response of [Response.json({ ...page(), instanceId: other }), Response.json({ ...page(), targetRevision: 4 }),
      new Response(JSON.stringify(page()), { headers: { "content-type": "text/html" } }),
      new Response("{invalid json secret", { headers: { "content-type": "application/json" } }),
      new Response(new Uint8Array([255]), { headers: { "content-type": "application/json" } }),
      new Response("{}", { headers: { "content-type": "application/json", "content-length": String(sqlVolumeMaximumResponseBytes + 1) } }),
      new Response("x".repeat(sqlVolumeMaximumResponseBytes + 1), { headers: { "content-type": "application/json" } })]) {
      clearApiCache(); globalThis.fetch = async () => response;
      await assert.rejects(getSqlVolumePage(target, controller.signal, undefined, binding), { message: invalidSqlVolumeResponseMessage });
    }
    clearApiCache(); globalThis.fetch = async () => new Response("secret", { status: 403 });
    await assert.rejects(getSqlVolumePage(target, controller.signal, undefined, binding), /not authorized/);
    clearApiCache(); globalThis.fetch = async () => { throw new Error("secret host credentials"); };
    await assert.rejects(getSqlVolumePage(target, controller.signal, undefined, binding), { message: "SQL-reported volume capacity is temporarily unavailable." });
    clearApiCache(); let called = false; globalThis.fetch = async () => { called = true; return Response.json(page()); };
    const aborted = new AbortController(); aborted.abort(new DOMException("Cancelled", "AbortError"));
    await assert.rejects(getSqlVolumePage(target, aborted.signal, undefined, binding), { name: "AbortError" });
    assert.equal(called, false);
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test("volume continuation preserves opaque cursor and validates the same native snapshot before returning rows", async () => {
  const original = globalThis.fetch;
  try {
    const cursor = btoa("protected + cursor");
    const payload = page(); payload.items = [row(2)];
    clearApiCache(); globalThis.fetch = async path => {
      assert.equal(new URL(path, "http://localhost").searchParams.get("cursor"), cursor);
      return Response.json(payload);
    };
    assert.equal((await getSqlVolumePage(target, new AbortController().signal, cursor,
      { ...binding, snapshotRunId: run, afterVolumeKey: key(1) })).items[0].volumeKey, key(2));
    clearApiCache(); globalThis.fetch = async () => Response.json({ ...payload, snapshotRunId: other });
    await assert.rejects(getSqlVolumePage(target, new AbortController().signal, cursor,
      { ...binding, snapshotRunId: run }), { message: invalidSqlVolumeResponseMessage });
    clearApiCache(); globalThis.fetch = async () => new Response("private reason", { status: 400 });
    await assert.rejects(getSqlVolumePage(target, new AbortController().signal, cursor, binding), /snapshot changed/);
  } finally { globalThis.fetch = original; clearApiCache(); }
});
