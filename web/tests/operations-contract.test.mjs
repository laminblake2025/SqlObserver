import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { parseOperationalPage, readBoundedJson } from "../src/features/operations/operationsParser.mjs";
import { getOperationalPage, OperationalApiError } from "../src/features/operations/operationsApi.ts";
import { initialOperationsState, operationsReducer } from "../src/features/operations/operationsState.ts";
import { buildOperationsRenderModel } from "../src/features/operations/operationsRenderModel.ts";

const target = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const base = kind => ({ targetId: target, targetRevision: 4, runId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", observedAtUtc: "2026-08-25T12:00:00Z", state: "Complete", items: [], hasMore: false, nextCursor: null, truncated: false, ...(kind === "agent" ? { coverageFromUtc: "2026-08-24T12:00:00Z", coverageToUtc: "2026-08-25T12:00:00Z" } : {}), ...(kind.startsWith("ag-") ? { visibilityScope: "PrimaryAllKnown" } : {}) });

test("M9 parser executes each of the six route contracts", () => {
  for (const kind of ["backups", "agent", "tempdb-summary", "tempdb-files", "ag-replicas", "ag-databases"]) assert.equal(parseOperationalPage(base(kind), kind, target).targetId, target);
});

test("M9 parser rejects cursor, target, state, and sensitive Agent drift", () => {
  assert.throws(() => parseOperationalPage({ ...base("backups"), targetId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd" }, "backups", target));
  assert.throws(() => parseOperationalPage({ ...base("backups"), hasMore: false, nextCursor: "opaque" }, "backups", target));
  assert.throws(() => parseOperationalPage({ ...base("backups"), state: "Unknown" }, "backups", target));
  assert.throws(() => parseOperationalPage({ ...base("agent"), items: [{ jobId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", historyInstanceId: 1, stepId: 1, retryAttempt: 0, durationSeconds: 1, firstObservedAtUtc: "2026-08-25T12:00:00Z", failureFingerprint: "a".repeat(64), contentAvailable: true, sourceTimeUtc: null }] }, "agent", target));
  for (const key of ["messageText", "jobNames", "commandText", "sourceLocalFinish", "runDateLocal", "runTimeLocal", "MessageText", "JobNames", "CommandText"]) {
    assert.throws(() => parseOperationalPage({ ...base("agent"), items: [{ jobId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", historyInstanceId: 1, stepId: 1, runStatus: 0, failureKind: "Failed", retryAttempt: 0, durationSeconds: 1, firstObservedAtUtc: "2026-08-25T12:00:00Z", failureFingerprint: "a".repeat(64), contentAvailable: false, [key]: "sensitive" }] }, "agent", target));
  }
});

test("M9 reader enforces streamed response bytes before parser execution", async () => {
  const body = new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(JSON.stringify(base("backups")))); controller.close(); } });
  const response = new Response(body, { headers: { "content-type": "application/json" } });
  assert.equal((await readBoundedJson(response, "backups", target)).targetRevision, 4);
  const huge = new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(1024 * 1024 + 1)); controller.close(); } }));
  await assert.rejects(() => readBoundedJson(huge, "backups", target));
});

test("Agent classification preserves job and step records and rejects inconsistent flags", () => {
  const row = { jobId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", historyInstanceId: 1, stepId: 0, runStatus: 0, failureKind: "Failed", retryAttempt: 0, durationSeconds: 1, firstObservedAtUtc: "2026-08-25T12:00:00Z", failureFingerprint: "a".repeat(64), contentAvailable: false, isJobOutcome: true, countsAsJobFailure: true };
  const steps = [row, { ...row, stepId: 1, isJobOutcome: false, countsAsJobFailure: false }, { ...row, stepId: 1, runStatus: 2, failureKind: "Retry", isJobOutcome: false, countsAsJobFailure: false }, { ...row, runStatus: 3, failureKind: "Cancelled", countsAsJobFailure: false }];
  assert.deepEqual(parseOperationalPage({ ...base("agent"), items: steps }, "agent", target).items, steps);
  for (const altered of [{ ...row, isJobOutcome: "true" }, { ...row, countsAsJobFailure: "false" }, { ...row, stepId: 1 }, { ...row, runStatus: 2 }, { ...row, failureKind: "Retry" }, { ...row, isJobOutcome: null }]) {
    assert.throws(() => parseOperationalPage({ ...base("agent"), items: [altered] }, "agent", target), /agent classification/);
  }
});

test("Agent classification remains optional for older responses", () => {
  const row = { jobId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc", historyInstanceId: 1, stepId: 1, runStatus: 0, failureKind: "Failed", retryAttempt: 0, durationSeconds: 1, firstObservedAtUtc: "2026-08-25T12:00:00Z", failureFingerprint: "a".repeat(64), contentAvailable: false };
  assert.deepEqual(parseOperationalPage({ ...base("agent"), items: [row] }, "agent", target).items, [row]);
});

test("M9 API and panel retain six distinct routes and no response reflection", async () => {
  const api = await readFile(new URL("../src/features/operations/operationsApi.ts", import.meta.url), "utf8");
  const panel = await readFile(new URL("../src/features/operations/OperationsPanel.tsx", import.meta.url), "utf8");
  for (const route of ["backups", "sql-agent/failures", "tempdb", "tempdb/files", "availability-groups/replicas", "availability-groups/databases"]) assert.match(api, new RegExp(route.replaceAll("/", "\\/")));
  for (const label of ["Backups", "SQL Agent failures", "TempDB summary", "TempDB files", "Availability Group replicas", "Availability Group databases"]) assert.match(panel, new RegExp(label));
  assert.match(api, /readBoundedJson/); assert.doesNotMatch(api, /response\.json\(\)/);
});

test("M9 production client replays cursors across all six surfaces", async () => {
  const originalFetch = globalThis.fetch; const seen = [];
  try {
    globalThis.fetch = async url => { seen.push(String(url)); const kind = String(url).includes("sql-agent") ? "agent" : String(url).includes("tempdb/files") ? "tempdb-files" : String(url).includes("tempdb") ? "tempdb-summary" : String(url).includes("replicas") ? "ag-replicas" : String(url).includes("databases") ? "ag-databases" : "backups"; const page = base(kind); if (!String(url).includes("cursor=")) { page.nextCursor = "opaque-page-2"; page.hasMore = true; } return new Response(JSON.stringify(page), { status: 200, headers: { "content-type": "application/json" } }); };
    for (const kind of ["backups", "agent", "tempdb-summary", "tempdb-files", "ag-replicas", "ag-databases"]) { const first = await getOperationalPage(target, kind); await getOperationalPage(target, kind, { cursor: first.nextCursor }); }
    assert.equal(seen.filter(url => url.includes("cursor=opaque-page-2")).length, 6);
  } finally { globalThis.fetch = originalFetch; }
});

test("M9 production client safely maps malformed/status/oversized and aborts streamed responses", async () => {
  const originalFetch = globalThis.fetch;
  try {
    globalThis.fetch = async () => new Response("provider secret", { status: 503 });
    await assert.rejects(() => getOperationalPage(target, "backups"), error => error instanceof OperationalApiError && !error.message.includes("provider"));
    globalThis.fetch = async () => new Response("not json", { status: 200 });
    await assert.rejects(() => getOperationalPage(target, "backups"), error => error instanceof OperationalApiError && error.status === 502);
    globalThis.fetch = async () => new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(1024 * 1024 + 1)); controller.close(); } }), { status: 200 });
    await assert.rejects(() => getOperationalPage(target, "backups"), error => error instanceof OperationalApiError && error.status === 502);
    const before = new AbortController(); before.abort(); globalThis.fetch = async () => new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(JSON.stringify(base("backups")))); controller.close(); } }), { status: 200 });
    await assert.rejects(() => getOperationalPage(target, "backups", { signal: before.signal }), error => error instanceof DOMException && error.name === "AbortError");
    const during = new AbortController(); let release; const gate = new Promise(resolve => { release = resolve; });
    globalThis.fetch = async () => new Response(new ReadableStream({ async start(controller) { controller.enqueue(new TextEncoder().encode(JSON.stringify(base("backups")).slice(0, 10))); await gate; controller.enqueue(new TextEncoder().encode("}")); controller.close(); } }), { status: 200 });
    const pending = getOperationalPage(target, "backups", { signal: during.signal }); setTimeout(() => { during.abort(); release(); }, 5);
    await assert.rejects(() => pending, error => error instanceof DOMException && error.name === "AbortError");
  } finally { globalThis.fetch = originalFetch; }
});

test("M9 production operations reducer covers loading, complete, degraded, no-data, retry, append, and cancel reset", () => {
  const page = { ...base("backups"), items: [{ label: "<hostile>" }], hasMore: true, nextCursor: "next" };
  let state = initialOperationsState();
  assert.equal(state.backups.loading, true);
  state = operationsReducer(state, { type: "success", kind: "backups", page, append: false });
  assert.equal(state.backups.page.items[0].label, "<hostile>");
  state = operationsReducer(state, { type: "success", kind: "backups", page: { ...page, state: "Degraded", items: [{ label: "second" }] }, append: true });
  assert.equal(state.backups.page.items.length, 2);
  for (const stateName of ["NoData", "Complete", "Partial"]) state = operationsReducer(state, { type: "success", kind: "backups", page: { ...page, state: stateName, items: [] }, append: false });
  state = operationsReducer(state, { type: "failure", kind: "backups", error: "retry", retryable: true });
  assert.equal(state.backups.error, "retry");
  state = operationsReducer(state, { type: "reset" });
  assert.equal(state.backups.loading, true);
});

test("M9 render model maps every surface and state with safe retry, paging, and abort flags", () => {
  const kinds = ["backups", "agent", "tempdb-summary", "tempdb-files", "ag-replicas", "ag-databases"];
  const states = ["Complete", "Partial", "Degraded", "NoData", "Unsupported", "PermissionDenied"];
  for (const kind of kinds) for (const state of states) {
    let stateValue = initialOperationsState();
    stateValue = operationsReducer(stateValue, { type: "success", kind, page: { ...base(kind), state, items: [{ label: "<img onerror=alert(1)>" }], hasMore: true, nextCursor: "opaque" }, append: false });
    const card = buildOperationsRenderModel(stateValue).cards[kind];
    assert.equal(card.status, state); assert.equal(card.canLoadMore, true); assert.equal(card.canRetry, false); assert.equal(card.inertLabels[0], "<img onerror=alert(1)>");
  }
  let retryState = initialOperationsState();
  retryState = operationsReducer(retryState, { type: "failure", kind: "backups", error: "retry", retryable: true });
  assert.equal(buildOperationsRenderModel(retryState).cards.backups.status, "Error");
  assert.equal(buildOperationsRenderModel(retryState).cards.backups.canRetry, true);
  retryState = operationsReducer(retryState, { type: "cancel", kind: "backups" });
  assert.equal(buildOperationsRenderModel(retryState).cards.backups.canCancel, false);
});

test("operations cancellation remains truthful and recoverable without discarding an existing page", () => {
  let initial = initialOperationsState();
  initial = operationsReducer(initial, { type: "cancel", kind: "agent" });
  const cancelled = buildOperationsRenderModel(initial).cards.agent;
  assert.equal(cancelled.status, "Error");
  assert.match(cancelled.error, /cancelled/i);
  assert.equal(cancelled.canRetry, true);
  assert.equal(cancelled.canCancel, false);

  const page = { ...base("backups"), items: [{ label: "first" }], hasMore: true, nextCursor: "next" };
  let loaded = initialOperationsState();
  loaded = operationsReducer(loaded, { type: "success", kind: "backups", page, append: false });
  loaded = operationsReducer(loaded, { type: "start", kind: "backups", append: false });
  const inFlight = buildOperationsRenderModel(loaded).cards.backups;
  assert.equal(inFlight.status, "Loading");
  assert.equal(inFlight.canCancel, true);
  loaded = operationsReducer(loaded, { type: "cancel", kind: "backups" });
  const preserved = buildOperationsRenderModel(loaded).cards.backups;
  assert.equal(preserved.page, page);
  assert.deepEqual(preserved.items, page.items);
  assert.match(preserved.error, /cancelled/i);
  assert.equal(preserved.canRetry, true);
  assert.equal(preserved.canCancel, false);
});

test("operations parser retains validated TempDB totals from the live response shape", () => {
  const live = parseOperationalPage({
    ...base("tempdb-summary"),
    totalBytes: 142606336,
    usedBytes: 86114304,
    logTotalBytes: 75489280,
    logUsedBytes: 56487936,
  }, "tempdb-summary", target);
  assert.equal(live.totalBytes, 142606336);
  assert.equal(live.usedBytes, 86114304);
  assert.equal(live.logTotalBytes, 75489280);
  assert.equal(live.logUsedBytes, 56487936);

  const zero = parseOperationalPage({ ...base("tempdb-summary"), totalBytes: 0, usedBytes: 0, logTotalBytes: 0, logUsedBytes: 0 }, "tempdb-summary", target);
  assert.equal(zero.totalBytes, 0);
  assert.equal(zero.usedBytes, 0);
  assert.equal(zero.logTotalBytes, 0);
  assert.equal(zero.logUsedBytes, 0);

  const unknown = parseOperationalPage({ ...base("tempdb-summary"), totalBytes: null, usedBytes: null, logTotalBytes: null, logUsedBytes: null }, "tempdb-summary", target);
  assert.equal(unknown.totalBytes, null);
  assert.equal(unknown.usedBytes, null);
  assert.equal(unknown.logTotalBytes, null);
  assert.equal(unknown.logUsedBytes, null);

  for (const field of ["totalBytes", "usedBytes", "logTotalBytes", "logUsedBytes"]) {
    for (const value of [-1, 1.5, Number.MAX_SAFE_INTEGER + 2, "86114304"]) {
      assert.throws(() => parseOperationalPage({ ...base("tempdb-summary"), [field]: value }, "tempdb-summary", target));
    }
  }
  assert.throws(() => parseOperationalPage({ ...base("tempdb-summary"), totalBytes: 10, usedBytes: 11 }, "tempdb-summary", target));
  assert.throws(() => parseOperationalPage({ ...base("tempdb-summary"), logTotalBytes: 10, logUsedBytes: 11 }, "tempdb-summary", target));
});

test("operations parser retains validated AG visibility for both casing forms", () => {
  for (const kind of ["ag-replicas", "ag-databases"]) {
    assert.equal(parseOperationalPage({ ...base(kind), visibilityScope: "ResolvingLocalOnly" }, kind, target).visibilityScope, "ResolvingLocalOnly");
    assert.equal(parseOperationalPage({ ...base(kind), visibilityScope: undefined, VisibilityScope: "SecondaryLocalOnly" }, kind, target).visibilityScope, "SecondaryLocalOnly");
    assert.throws(() => parseOperationalPage({ ...base(kind), visibilityScope: "provider-secret" }, kind, target));
  }
});
