import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { transformWithOxc } from "vite";
import { parseAvailabilityPage, availabilityCoverageMessage, availabilityUtcKey,
  assertAvailabilitySnapshot } from "../src/features/operations/availabilityEvidence.ts";
import { getAvailabilityEvidence, AvailabilityEvidenceError } from "../src/features/operations/availabilityEvidenceApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const run = "22222222-2222-4222-8222-222222222222";
const at = "2026-09-30T12:00:00.000001Z";
const identity = { groupFingerprint: "a".repeat(64), replicaFingerprint: "b".repeat(64),
  observerReplicaFingerprint: "c".repeat(64), replicaIsLocal: false, visibilityScope: "PrimaryAllKnown", stateObserved: true };
const replica = { ...identity, role: "SECONDARY", operationalState: "FAILED_NO_QUORUM", connectedState: "DISCONNECTED" };
const database = { ...identity, databaseFingerprint: "d".repeat(64), synchronizationState: "NOT SYNCHRONIZING",
  synchronizationHealth: "NOT_HEALTHY", databaseState: "SUSPECT", isSuspended: null,
  logSendQueueKb: null, logSendRateKbPerSecond: 0, redoQueueKb: 234, redoRateKbPerSecond: null,
  sourceLastCommitLocal: "2026-11-01T01:30:00.123456" };
function page(items = [replica]) {
  return { schemaVersion: 2, targetId: target, atUtc: at, targetRevision: 2, runId: run,
    sourceObservedAtUtc: "2026-09-30T11:59:00.000001Z", completedAtUtc: "2026-09-30T11:59:01Z",
    state: "Complete", evidenceStatus: "retained", sourceRows: items.length, outputItems: items.length,
    retainedItems: items.length, lossKind: "none", truncated: false, collectorEnabled: true,
    items, hasMore: false, nextCursor: null };
}
const parse = (value, stream = "replicas") => parseAvailabilityPage(value, stream, target, at);

test("AG v2 preserves fault states, null counters, zero rates and exact raw local time", () => {
  assert.deepEqual(parse(page()).items[0], replica);
  const result = parse(page([database]), "databases");
  assert.equal(result.items[0].logSendQueueKb, null);
  assert.equal(result.items[0].logSendRateKbPerSecond, 0);
  assert.equal(result.items[0].sourceLastCommitLocal, database.sourceLastCommitLocal);
  assert.match(availabilityCoverageMessage(result), /does not mean every replica is healthy/);
});

test("AG v2 distinguishes configured unobserved rows from observed state", () => {
  const hidden = { ...replica, stateObserved: false, role: null, operationalState: null, connectedState: null,
    visibilityScope: "SecondaryLocalOnly", replicaIsLocal: null, observerReplicaFingerprint: null };
  assert.equal(parse(page([hidden])).items[0].stateObserved, false);
  assert.throws(() => parse(page([{ ...hidden, role: "SECONDARY" }])));
});

test("full three-part AG identity keeps one database on two replicas separate", () => {
  const second = { ...database, replicaFingerprint: "e".repeat(64) };
  assert.equal(parse(page([database, second]), "databases").items.length, 2);
  for (const items of [[database, database], [second, database]]) assert.throws(() => parse(page(items), "databases"));
});

test("cutoff and receipt bindings retain all six fractional digits", () => {
  assert.equal(availabilityUtcKey("2026-09-30T12:00:00.001Z"), "2026-09-30T12:00:00.001000Z");
  for (const value of ["2026-09-30T12:00:00.000002Z", "2026-02-30T12:00:00Z", "2026-09-30T12:00:00.0000010Z", "2026-09-30T12:00:00+00:00"])
    assert.throws(() => parse({ ...page(), atUtc: value }));
  assert.throws(() => parse({ ...page(), completedAtUtc: "2026-09-30T12:00:00.000002Z" }));
  assert.throws(() => parse({ ...page(), targetId: run }));
});

test("unknown fields and malformed source time or counters fail without fabricated values", () => {
  for (const altered of [{ ...database, sourceLastCommitLocal: `${database.sourceLastCommitLocal}Z` },
    { ...database, sourceLastCommitLocal: "2026-02-29T00:00:00.000001" }, { ...database, redoQueueKb: -1 },
    { ...database, redoQueueKb: Number.MAX_SAFE_INTEGER + 1 }, { ...database, commandText: "untrusted" },
    { ...database, databaseState: "UNKNOWN" }]) assert.throws(() => parse(page([altered]), "databases"));
  const missing = { ...database }; delete missing.isSuspended;
  assert.throws(() => parse(page([missing]), "databases"));
  assert.throws(() => parse({ ...page(), arbitrary: true }));
});

const receipt = { ...page([]), sourceObservedAtUtc: null, sourceRows: 0, outputItems: 0, retainedItems: 0 };
const receipts = [
  { ...receipt, evidenceStatus: "not_collected", state: "NoData", runId: null, completedAtUtc: null, collectorEnabled: false },
  { ...receipt, evidenceStatus: "no_data", state: "NoData" },
  { ...receipt, evidenceStatus: "collection_failed", state: "PermissionDenied" },
  { ...receipt, evidenceStatus: "withheld_by_byte_limit", state: "Partial", sourceRows: 20, lossKind: "response_byte_limit", truncated: true },
  { ...receipt, evidenceStatus: "evidence_not_retained", state: "Degraded", sourceRows: 2, outputItems: 2 },
];
for (const value of receipts) test(`coverage ${value.evidenceStatus} stays distinct from healthy/no AG`, () => {
  const result = parse(value);
  assert.equal(result.evidenceStatus, value.evidenceStatus);
  assert.ok(availabilityCoverageMessage(result).length > 30);
  assert.throws(() => parse({ ...value, items: [replica] }));
});

test("continuations reject a changed run, revision, target or microsecond cutoff", () => {
  assert.doesNotThrow(() => assertAvailabilitySnapshot(page(), page()));
  for (const change of [{ runId: target }, { targetRevision: 3 }, { targetId: run }, { atUtc: at.replace("000001", "000002") }])
    assert.throws(() => assertAvailabilitySnapshot(page(), { ...page(), ...change }), /snapshot changed/);
});

test("production API sends each stream's signed cursor and a fixed cutoff with same-origin credentials", async () => {
  const original = globalThis.fetch;
  const requests = [];
  globalThis.fetch = async (url, options) => {
    requests.push({ url: new URL(url, "https://local.test"), options });
    const first = !requests.at(-1).url.searchParams.has("cursor");
    const items = first ? Array.from({ length: 50 }, (_, index) => ({ ...replica, replicaFingerprint: index.toString(16).padStart(64, "0") })) : [replica];
    return new Response(JSON.stringify({ ...page(items), sourceRows: 51, outputItems: 51, retainedItems: 51,
      hasMore: first, nextCursor: first ? "signed+cursor/==" : null }));
  };
  try {
    const signal = new AbortController().signal;
    const first = await getAvailabilityEvidence(target, "replicas", at, signal);
    await getAvailabilityEvidence(target, "replicas", at, signal, first.nextCursor);
    assert.equal(requests[0].url.pathname, `/api/v2/observation-targets/${target}/availability-groups/replicas`);
    assert.equal(requests[1].url.searchParams.get("atUtc"), at);
    assert.equal(requests[1].url.searchParams.get("cursor"), "signed+cursor/==");
    assert.equal(requests[1].url.searchParams.get("limit"), "50");
    assert.ok(requests[1].options.signal instanceof AbortSignal); assert.equal(requests[1].options.signal.aborted, signal.aborted);
    assert.equal(requests[1].options.credentials, "same-origin");
    assert.equal(requests[1].options.cache, "no-store");
  } finally { globalThis.fetch = original; }
});

test("API maps stale, role denial, invalid evidence and streamed size failures without reflecting response bodies", async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [400, 401, 403, 409, 503, 504]) {
      globalThis.fetch = async () => new Response("provider-secret", { status });
      await assert.rejects(() => getAvailabilityEvidence(target, "databases", at, new AbortController().signal, "cursor"),
        failure => failure instanceof AvailabilityEvidenceError && !failure.message.includes("provider") &&
          failure.restart === (status === 400 || status === 409) && failure.retryable === (status !== 401 && status !== 403) &&
          (![401,403].includes(status) || failure.status === status && failure.message === "A Viewer, Operator or TargetAdministrator role scoped to this server is required."));
    }
    globalThis.fetch = async () => new Response(JSON.stringify({ ...page(), targetId: run }));
    await assert.rejects(() => getAvailabilityEvidence(target, "replicas", at, new AbortController().signal), /could not be verified/);
    globalThis.fetch = async () => new Response(new ReadableStream({ start(controller) {
      controller.enqueue(new Uint8Array(1_048_577)); controller.close();
    } }));
    await assert.rejects(() => getAvailabilityEvidence(target, "replicas", at, new AbortController().signal), /could not be verified/);
    const controller = new AbortController(); controller.abort();
    globalThis.fetch = async () => { throw new DOMException("cancelled", "AbortError"); };
    await assert.rejects(() => getAvailabilityEvidence(target, "replicas", at, controller.signal), failure => failure.name === "AbortError");
  } finally { globalThis.fetch = original; }
});

// Compile the production JSX in memory: assertions inspect React's rendered DOM, not source text.
const source = await readFile(new URL("../src/features/operations/AvailabilityEvidenceView.tsx", import.meta.url), "utf8");
const require = createRequire(import.meta.url);
const javascript = (await transformWithOxc(source, "AvailabilityEvidenceView.tsx", { jsx: { runtime: "automatic" } })).code
  .replaceAll('"react/jsx-runtime"', JSON.stringify(pathToFileURL(require.resolve("react/jsx-runtime")).href))
  .replaceAll('"./availabilityEvidence.ts"', JSON.stringify(new URL("../src/features/operations/availabilityEvidence.ts", import.meta.url).href));
const { AvailabilityEvidenceView } = await import(`data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`);
function render(value, stream = "replicas") {
  return renderToStaticMarkup(createElement(AvailabilityEvidenceView, { page: value, stream, formatTime: value => {
    assert.match(value, /Z$/); return `UTC ${value}`;
  } }));
}

test("rendered evidence retains unknown vs zero, KB units and unconverted server local time", () => {
  const html = render(parse(page([database]), "databases"), "databases");
  assert.match(html, /Log send queue \(KB\)/);
  assert.match(html, /Redo rate \(KB\/s\)/);
  assert.match(html, /<td>Unknown<\/td><td>0<\/td><td>234<\/td><td>Unknown<\/td>/);
  assert.match(html, /2026-11-01 01:30:00\.123456/);
  assert.match(html, /SUSPECT/);
  assert.match(html, /does not convert it/);
  assert.match(html, /DISCONNECTED|NOT HEALTHY/);
});

test("rendered secondary visibility and retired receipts give actionable evidence status", () => {
  const html = render(parse(page([{ ...replica, visibilityScope: "SecondaryLocalOnly" }])));
  assert.match(html, /Secondary: local state only/);
  assert.match(html, /FAILED NO QUORUM/);
  const retired = render(parse(receipts[4]));
  assert.match(retired, /no longer retained/);
  assert.match(retired, /choose a newer cutoff/);
  const disabled = render(parse(receipts[0]));
  assert.match(disabled, /target administrator/);
  assert.doesNotMatch(disabled, /No AG|healthy/);
});
