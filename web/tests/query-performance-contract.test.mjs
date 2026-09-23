import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { getQueryPerformance, getQueryPerformanceHistory, getQueryPerformanceStatus, getQueryPerformanceTop } from "../src/features/queries/queryPerformanceApi.ts";

test("query performance panel shows all source evidence by default", async () => {
  const panel = await readFile(new URL("../src/features/queries/TargetQueryPerformancePanel.tsx", import.meta.url), "utf8");
  assert.match(panel, /const \[source, setSource\] = useState(?:<[^>]+>)?\("mixed"\)/);
  assert.match(panel, /<option value="mixed">All sources · evidence<\/option>/);
  assert.match(panel, /queryPerformanceDatabaseOptions/);
  assert.match(panel, /databaseName/);
});

test("production endpoint response shape is flat, bounded, and content-unavailable", () => {
  const response = { targetId: "00000000-0000-0000-0000-000000000001", metric: "executions", snapshotUtc: new Date().toISOString(), nextCursor: "opaque-cursor", items: [{ databaseId: 5, queryFingerprint: "a".repeat(64), planFingerprint: null, source: "query_store", sourceState: "read_write", metric: "executions", value: null, semantics: "query_store_interval", intervalStartUtc: new Date(Date.now() - 60000).toISOString(), intervalEndUtc: new Date().toISOString(), coverage: "complete", fresh: true, truncated: false, contentAvailable: false }] };
  assert.ok(response.items.length <= 200);
  assert.match(response.items[0].queryFingerprint, /^[0-9a-f]{64}$/);
  assert.equal(response.planFingerprint, undefined);
  assert.equal(response.items[0].planFingerprint, null);
  assert.equal(typeof response.nextCursor, "string");
  assert.equal(response.items[0].contentAvailable, false);
  assert.equal(typeof response.items[0].sourceState, "string");
});

test("all six allowlisted metrics retain independent cursor pages", () => {
  const metrics = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
  const cursors = Object.fromEntries(metrics.map(metric => [metric, `${metric}-cursor`]))
  assert.deepEqual(Object.keys(cursors), metrics);
  assert.equal(cursors.executions, "executions-cursor");
  assert.equal(cursors.logical_reads, "logical_reads-cursor");
});

test("production history client sends a bound cursor and parses every metric", async () => {
  const originalFetch = globalThis.fetch;
  let requested = "";
  globalThis.fetch = async (input) => {
    requested = String(input);
    return new Response(JSON.stringify({ targetId: "target-1", databaseId: 5, queryFingerprint: "a".repeat(64), snapshotUtc: "2026-08-24T12:00:00Z", fromUtc: "2026-08-24T11:00:00Z", toUtc: "2026-08-24T12:00:00Z", nextCursor: "next", items: [{ databaseId: 5, queryFingerprint: "a".repeat(64), planFingerprint: null, source: "plan_cache", sourceState: "read_failure", semantics: "plan_cache_cumulative", cpuMilliseconds: 1, durationMilliseconds: 2, executions: 3, logicalReads: 4, writes: 5, rows: 6, intervalStartUtc: "2026-08-24T11:00:00Z", intervalEndUtc: "2026-08-24T12:00:00Z", coverage: "truncated", fresh: false, truncated: true, contentAvailable: false, collectionRunId: "11111111-1111-4111-8111-111111111111", observationKey: "b".repeat(32) }] }), { status: 200, headers: { "content-type": "application/json", "content-length": "900" } });
  };
  try {
    const page = await getQueryPerformanceHistory("target-1", 5, "a".repeat(64), "2026-08-24T11:00:00Z", "2026-08-24T12:00:00Z", undefined, "opaque-cursor");
    assert.match(requested, /fromUtc=2026-08-24T11%3A00%3A00Z/);
    assert.match(requested, /toUtc=2026-08-24T12%3A00%3A00Z/);
    assert.match(requested, /cursor=opaque-cursor/);
    assert.equal(page.nextCursor, "next");
    assert.deepEqual(page.items[0].metrics, { cpuMilliseconds: 1, durationMilliseconds: 2, executions: 3, logicalReads: 4, writes: 5, rows: 6 });
  } finally { globalThis.fetch = originalFetch; }
});

test("production status parser requires mixed aggregate and per-database evidence", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({ targetId: "target-2", snapshotUtc: "2026-08-24T12:00:00Z", source: "mixed", sourceState: "mixed", coverage: "truncated", fresh: false, truncated: true, contentAvailable: false, databaseStatuses: [{ databaseId: 5, status: "query_store_rows", sourceState: "read_write", reason: "query_store_read", fallbackAttempted: false, truncated: false, lossKind: "none", sourceRowsRead: 1, responseBytes: 100, minimumLostItems: 0, lossCountIsExact: true, minimumLostBytes: 0 }], databaseCatalog: [{ databaseId: 5, databaseName: "SqlObserverLabSales" }, { databaseId: 7, databaseName: "SqlObserverLabPublisher" }] }), { status: 200, headers: { "content-type": "application/json", "content-length": "700" } });
  try { const status = await getQueryPerformanceStatus("target-2"); assert.equal(status.sourceState, "mixed"); assert.equal(status.databaseStatuses[0].status, "query_store_rows"); assert.equal(status.databaseCatalog[1].databaseName, "SqlObserverLabPublisher"); }
  finally { globalThis.fetch = originalFetch; }
});

test("production status parser rejects optional or arbitrary database statuses", async () => {
  const originalFetch = globalThis.fetch;
  const envelope = (databaseStatuses) => ({ targetId: "target-3", snapshotUtc: "2026-08-24T12:00:00Z", source: "query_store", sourceState: "read_write", coverage: "complete", fresh: true, truncated: false, contentAvailable: false, databaseStatuses });
  globalThis.fetch = async () => new Response(JSON.stringify(envelope(undefined)), { status: 200, headers: { "content-type": "application/json", "content-length": "500" } });
  try { await assert.rejects(() => getQueryPerformanceStatus("target-3")); }
  finally { globalThis.fetch = originalFetch; }
  globalThis.fetch = async () => new Response(JSON.stringify(envelope([{ databaseId: 5, status: "arbitrary", sourceState: "read_write", reason: "bad", fallbackAttempted: false, truncated: false, lossKind: "none", sourceRowsRead: 0, responseBytes: 0 }])), { status: 200, headers: { "content-type": "application/json", "content-length": "700" } });
  try { await assert.rejects(() => getQueryPerformanceStatus("target-3")); }
  finally { globalThis.fetch = originalFetch; }
});

test("production top client uses one fixed window for all six metric pages", async () => {
  const originalFetch = globalThis.fetch; const requests = [];
  globalThis.fetch = async input => { const url = new URL(String(input), "http://localhost"); requests.push(url); const metric = url.searchParams.get("metric"); return new Response(JSON.stringify({ targetId: "target-fixed", metric, snapshotUtc: "2026-08-24T12:00:00Z", fromUtc: url.searchParams.get("fromUtc"), toUtc: url.searchParams.get("toUtc"), nextCursor: `${metric}-next`, items: [] }), { status: 200, headers: { "content-type": "application/json", "content-length": "350" } }); };
  try { const page = await getQueryPerformance("target-fixed"); assert.equal(requests.length, 6); assert.equal(new Set(requests.map(x => x.searchParams.get("fromUtc"))).size, 1); assert.equal(new Set(requests.map(x => x.searchParams.get("toUtc"))).size, 1); assert.equal(Object.keys(page.cursorsByMetric ?? {}).length, 6); }
  finally { globalThis.fetch = originalFetch; }
});

test("production parser rejects wrong metric, mixed item state, and reversed intervals", async () => {
  const originalFetch = globalThis.fetch;
  const base = { targetId: "target-adversarial", metric: "cpu", snapshotUtc: "2026-08-24T12:00:00Z", fromUtc: "2026-08-24T11:00:00Z", toUtc: "2026-08-24T12:00:00Z", nextCursor: null, items: [{ databaseId: 5, queryFingerprint: "a".repeat(64), planFingerprint: null, source: "query_store", sourceState: "read_write", metric: "duration", value: 1, semantics: "query_store_interval", intervalStartUtc: "2026-08-24T12:00:00Z", intervalEndUtc: "2026-08-24T11:00:00Z", coverage: "complete", fresh: true, truncated: false, contentAvailable: false }] };
  globalThis.fetch = async () => new Response(JSON.stringify(base), { status: 200, headers: { "content-type": "application/json", "content-length": "900" } });
  try { await assert.rejects(() => getQueryPerformanceTop("target-adversarial", "cpu", undefined, "2026-08-24T11:00:00Z", "2026-08-24T12:00:00Z")); }
  finally { globalThis.fetch = originalFetch; }
  globalThis.fetch = async () => new Response(JSON.stringify({ ...base, items: [{ ...base.items[0], metric: "cpu", sourceState: "mixed", intervalStartUtc: "2026-08-24T11:00:00Z", intervalEndUtc: "2026-08-24T12:00:00Z" }] }), { status: 200, headers: { "content-type": "application/json", "content-length": "900" } });
  try { await assert.rejects(() => getQueryPerformanceTop("target-adversarial", "cpu", undefined, "2026-08-24T11:00:00Z", "2026-08-24T12:00:00Z")); }
  finally { globalThis.fetch = originalFetch; }
});

test("production status parser preserves explicit target failure evidence", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({ targetId: "target-failure", snapshotUtc: "2026-08-24T12:00:00Z", source: "unavailable", sourceState: "unavailable", coverage: "unavailable", fresh: false, truncated: false, contentAvailable: false, targetStatus: "deadline_exceeded", targetReason: "deadline_exceeded", databaseStatuses: [] }), { status: 200, headers: { "content-type": "application/json", "content-length": "500" } });
  try { const status = await getQueryPerformanceStatus("target-failure"); assert.equal(status.source, "unavailable"); assert.equal(status.targetStatus, "deadline_exceeded"); assert.equal(status.fresh, false); }
  finally { globalThis.fetch = originalFetch; }
});

test("production status parser accepts every unsupported target reason and rejects mismatched pairs", async () => {
  const originalFetch = globalThis.fetch;
  const reasons = ["target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_missing", "capability_profile_missing", "capability_profile_stale"];
  for (const targetReason of reasons) {
    globalThis.fetch = async () => new Response(JSON.stringify({ targetId: "target-reasons", snapshotUtc: "2026-08-24T12:00:00Z", source: "unavailable", sourceState: "unavailable", coverage: "unavailable", fresh: false, truncated: false, contentAvailable: false, targetStatus: "unsupported", targetReason, databaseStatuses: [] }), { status: 200, headers: { "content-type": "application/json", "content-length": "500" } });
    const status = await getQueryPerformanceStatus("target-reasons");
    assert.equal(status.targetReason, targetReason);
  }
  globalThis.fetch = async () => new Response(JSON.stringify({ targetId: "target-reasons", snapshotUtc: "2026-08-24T12:00:00Z", source: "unavailable", sourceState: "unavailable", coverage: "unavailable", fresh: false, truncated: false, contentAvailable: false, targetStatus: "connection_failure", targetReason: "capability_missing", databaseStatuses: [] }), { status: 200, headers: { "content-type": "application/json", "content-length": "500" } });
  try { await assert.rejects(() => getQueryPerformanceStatus("target-reasons")); }
  finally { globalThis.fetch = originalFetch; }
});

test("query windows accept equivalent UTC precision but reject distinct instants", async () => {
  const originalFetch = globalThis.fetch;
  let responseFrom = "2026-09-04T01:00:00Z";
  globalThis.fetch = async () => new Response(JSON.stringify({ targetId: "precision", metric: "cpu", snapshotUtc: "2026-09-04T02:00:00Z", fromUtc: responseFrom, toUtc: "2026-09-04T02:00:00Z", nextCursor: null, items: [] }), { headers: { "content-type": "application/json" } });
  try {
    await getQueryPerformanceTop("precision", "cpu", undefined, "2026-09-04T01:00:00.000Z", "2026-09-04T02:00:00.000Z");
    responseFrom = "2026-09-04T01:00:00.0000001Z";
    await assert.rejects(() => getQueryPerformanceTop("precision", "cpu", undefined, "2026-09-04T01:00:00.000Z", "2026-09-04T02:00:00.000Z"), /outside its bounds/);
  } finally { globalThis.fetch = originalFetch; }
});

test("ranking filters are sent to the server with the same bound window on every page", async () => {
 const originalFetch=globalThis.fetch;
 const requests=[];
 globalThis.fetch=async input=>{
  requests.push(new URL(String(input),"http://localhost"));
  return new Response(JSON.stringify({targetId:"filtered",metric:"cpu",snapshotUtc:"2026-09-04T02:00:00Z",fromUtc:"2026-09-04T01:00:00Z",toUtc:"2026-09-04T02:00:00Z",items:[],nextCursor:null}),{headers:{"content-type":"application/json"}});
 };
 try {
  for(const cursor of [undefined,"next-page"]) await getQueryPerformanceTop("filtered","cpu",cursor,"2026-09-04T01:00:00Z","2026-09-04T02:00:00Z",undefined,{databaseId:7,source:"plan_cache"});
  assert.equal(requests.length,2);
  for(const url of requests) {
   assert.equal(url.searchParams.get("databaseId"),"7");
   assert.equal(url.searchParams.get("source"),"plan_cache");
   assert.equal(url.searchParams.get("fromUtc"),"2026-09-04T01:00:00Z");
  }
  assert.equal(requests[1].searchParams.get("cursor"),"next-page");
  for(const filters of [{databaseId:0},{databaseId:32768},{source:"mixed"}]) await assert.rejects(()=>getQueryPerformanceTop("filtered","cpu",undefined,undefined,undefined,undefined,filters),/filters were invalid/);
 } finally {globalThis.fetch=originalFetch;}
});

test("query request errors include a safe copyable correlation reference",async()=>{
 const originalFetch=globalThis.fetch;
 globalThis.fetch=async()=>new Response("provider details must never be displayed",{status:500,headers:{"X-Correlation-ID":"query-test-42"}});
 try {await assert.rejects(()=>getQueryPerformanceTop("target","cpu"),/^Error: Query performance request failed\. Reference: query-test-42$/);}
 finally {globalThis.fetch=originalFetch;}
});
