import assert from "node:assert/strict";
import test from "node:test";
import { getPage, maximumResponseBytes, parseEdge, parseHistory, parsePage, parseRequest, parseSession, parseWait, safeCursor, safeStatusMessage } from "../src/features/activity/activityParser.mjs";

const target = "11111111-1111-4111-8111-111111111111";
const at = "2026-08-23T18:00:00Z";
const envelope = (items, extra = {}) => ({ instanceId: target, repositoryTimeUtc: at, evidence: null, items, ...extra });
const session = { sessionId: 1, status: "running", isUserProcess: true, databaseId: 5, openTransactionCount: 0, cpuMilliseconds: "1", memoryUsagePages: "2", reads: "3", writes: "4", logicalReads: "5", totalElapsedMilliseconds: "6", observedAtUtc: at };
const request = { sessionId: 1, requestId: 1, status: "running", command: "select", databaseId: 5, cpuMilliseconds: "1", totalElapsedMilliseconds: "2", reads: "3", writes: "4", logicalReads: "5", rowCount: "6", percentComplete: 25, observedAtUtc: at };
const wait = { waitType: "LCK_M_S", waitingTasksCount: "1", waitTimeMilliseconds: "2", maximumWaitTimeMilliseconds: "3", signalWaitTimeMilliseconds: "4", baselineAvailable: true, resetDetected: false, waitingTasksDelta: "1", waitTimeMillisecondsDelta: "2", signalWaitTimeMillisecondsDelta: "3", observedAtUtc: at };
const edge = { blockedSessionId: 1, blockerKind: "session", blockerSessionId: 2, waitType: "LCK_M_S", waitingTaskCount: "1", waitDurationMilliseconds: "2", rootBlockerSessionId: 2, chainDepth: 1, chainState: "resolved", observedAtUtc: at };
const evidence = { runId: target, targetRevision: "1", collectorId: "blocking.current", freshness: "current", outcome: "succeeded", reason: "completed", isPartial: false, loss: null, completedAtUtc: at };
const streamResponse = (body, headers = {}) => new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode(body)); controller.close(); } }), { status: 200, headers: { "content-type": "application/json", ...headers } });

test("production parser accepts null evidence and preserves every DTO shape", () => {
  assert.equal(parsePage(envelope([session]), parseSession, target).items[0].memoryUsagePages, "2");
  assert.equal(parsePage(envelope([request]), parseRequest, target).items[0].rowCount, "6");
  assert.equal(parsePage(envelope([wait], { baselineRunId: target }), parseWait, target).baselineRunId, target);
  assert.equal(parsePage(envelope([edge], { maximumChainDepth: 32, maximumGraphNodes: 256 }), parseEdge, target).items[0].chainState, "resolved");
  const history = parsePage({ ...envelope([{ evidence, edge }], { fromUtc: at, toUtc: "2026-08-23T19:00:00Z", nextCursor: "x" }), evidence }, parseHistory, target);
  assert.equal(history.items[0].edge.blockedSessionId, 1);
  assert.equal(history.items[0].evidence.collectorId, "blocking.current");
});

test("production parser rejects malformed fields, oversized pages, and cross-target pages", () => {
  assert.throws(() => parsePage(envelope(Array.from({ length: 26 }, () => session)), parseSession, target));
  assert.throws(() => parsePage(envelope([{ ...session, reads: "bad" }]), parseSession, target));
  assert.throws(() => parsePage({ ...envelope([]), instanceId: "22222222-2222-4222-8222-222222222222" }, parseSession, target));
  assert.equal(safeCursor("x".repeat(512)).length, 512);
  assert.throws(() => safeCursor("x".repeat(513)));
});

test("production HTTP reader enforces headers, streamed bytes, null bodies, and status messages", async () => {
  const originalFetch = globalThis.fetch;
  try {
    globalThis.fetch = async () => streamResponse(JSON.stringify(envelope([])));
    assert.equal((await getPage("/activity", parseSession, target, new AbortController().signal)).instanceId, target);
    globalThis.fetch = async () => streamResponse("{}", { "content-length": String(maximumResponseBytes + 1) });
    await assert.rejects(() => getPage("/activity", parseSession, target, new AbortController().signal));
    globalThis.fetch = async () => new Response(null, { status: 200 });
    await assert.rejects(() => getPage("/activity", parseSession, target, new AbortController().signal));
    globalThis.fetch = async () => streamResponse("x".repeat(maximumResponseBytes + 1));
    await assert.rejects(() => getPage("/activity", parseSession, target, new AbortController().signal));
  } finally { globalThis.fetch = originalFetch; }
  assert.match(safeStatusMessage(403), /not authorized/u);
  assert.match(safeStatusMessage(504), /execution limit/u);
});
