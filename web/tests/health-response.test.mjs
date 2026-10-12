import test from "node:test";
import assert from "node:assert/strict";
import {
  healthResponseMaximumBytes, parseTargetHealthResponse, parseDatabaseHealthResponse, parseDatabaseFileHealthResponse,
  parseDatabaseOptionChangesResponse, parseServerConfigurationChangesResponse, parseServerConfigurationSnapshotResponse,
} from "../src/features/health/healthResponseModel.ts";
import { getTargetHealth, getDatabaseHealth, getDatabaseFileHealth, getDatabaseOptionChanges, getServerConfigurationChanges,
  getServerConfigurationSnapshot, HealthRequestError } from "../src/features/health/healthApi.ts";
import { clearApiCache } from "../src/apiClient.ts";

const target = "11111111-1111-4111-8111-111111111111", run = "22222222-2222-4222-8222-222222222222";
const other = "33333333-3333-4333-8333-333333333333";
const at = "2026-09-26T09:00:00.123456Z", fromUtc = "2026-09-26T08:00:00.123456Z", toUtc = "2026-09-26T12:00:00.123456Z";
const window = { fromUtc, toUtc };
const collector = () => ({ collectorId: "engine.core", manifestVersion: 1, outputSchemaVersion: 1, state: "current", reason: "none",
  lastExecutionOutcome: "succeeded", circuitState: "closed", durationMilliseconds: 10, retryCount: 0, sourceRows: 1, outputRows: 1,
  insertedRows: 1, duplicateRows: 0, rejectedRows: 0, responseBytes: 128, persistedBytes: 128, sampleLossKind: null,
  minimumLostItems: null, lossCountIsExact: null, minimumLostBytes: null, hasVisibilityGap: false,
  scheduledAtUtc: at, lastAttemptAtUtc: at, lastSuccessAtUtc: at, nextDueAtUtc: toUtc });
const targetPayload = () => ({ instanceId: target, repositoryTimeUtc: at, state: "current", collectors: [collector()],
  coreMetrics: [{ sampleId: run, metricId: "engine.user_connections", observedAtUtc: at, value: 2, dimensions: [{ key: "scope", value: "aggregate" }] }] });
const database = () => ({ databaseId: 5, name: "Orders", state: "online", recoveryModel: "full", userAccess: "multi_user",
  isReadOnly: false, compatibilityLevel: 160, logReuseWaitDescription: "NOTHING", observedAtUtc: at, collector: collector() });
const file = () => ({ databaseId: 5, fileId: 1, logicalName: "Orders_data", fileType: "rows", state: "online", sizeBytes: "9007199254740993",
  maximumSizeBytes: null, growthBytes: "1024", growthPercent: 0, readCount: "9223372036854775807", writeCount: "1", bytesRead: "1024",
  bytesWritten: "512", ioStallMilliseconds: "9", readStallMilliseconds: "4", writeStallMilliseconds: "5", observedAtUtc: at, collector: collector() });
const page = (item) => ({ instanceId: target, repositoryTimeUtc: at, collector: collector(), items: [item], nextCursor: null });
const optionPayload = () => ({ targetId: target, ...window, semantics: "detected_between_consecutive_complete_inventory_runs", items: [{
  databaseId: 5, databaseName: "Orders", optionKey: "compatibility_level", previousValue: "150", currentValue: "160",
  previousObservedAtUtc: "2026-09-25T23:00:00Z", observedAtUtc: at, detectedAtUtc: at, collectionRunId: run }] });
const setting = () => ({ configurationId: 1539, configurationName: "cost threshold for parallelism", sourceType: "int", configuredValue: "50", inUseValue: "50" });
const changePayload = () => ({ targetId: target, targetRevision: 1, publicationGeneration: 2, ...window, coverage: "observed_changes_only", nextCursor: null,
  items: [{ ...setting(), previousConfiguredValue: "-1", previousInUseValue: "5", configuredChanged: true, inUseChanged: true,
    previousObservedAtUtc: "2026-09-25T23:00:00Z", observedAtUtc: at, detectedAtUtc: at, runId: run }] });
const snapshotPayload = () => ({ targetId: target, targetRevision: 1, runId: run, observedAtUtc: at, serverMajorVersion: 16,
  collectorEnabled: true, lastOutcome: "succeeded", coverage: "latest_complete_scan", nextCursor: null,
  items: [{ ...setting(), isDynamic: true, isAdvanced: true }] });

test("six native health contracts bind their target and retain exact Int64 strings and signed configuration values", () => {
  assert.equal(parseTargetHealthResponse(targetPayload(), target).coreMetrics[0].value, 2);
  assert.equal(parseDatabaseHealthResponse(page(database()), target).items[0].name, "Orders");
  assert.equal(parseDatabaseFileHealthResponse(page(file()), target).items[0].sizeBytes, "9007199254740993");
  assert.equal(parseDatabaseOptionChangesResponse(optionPayload(), target, window).items[0].optionKey, "compatibility_level");
  assert.equal(parseServerConfigurationChangesResponse(changePayload(), target, window).items[0].previousConfiguredValue, "-1");
  assert.equal(parseServerConfigurationSnapshotResponse(snapshotPayload(), target).items[0].isDynamic, true);
  const signed = targetPayload(); signed.coreMetrics[0].value = -1;
  assert.equal(parseTargetHealthResponse(signed, target).coreMetrics[0].value, -1);
  for (const [parse, payload] of [[parseTargetHealthResponse, targetPayload()], [parseDatabaseHealthResponse, page(database())],
    [parseDatabaseFileHealthResponse, page(file())], [parseDatabaseOptionChangesResponse, optionPayload()],
    [parseServerConfigurationChangesResponse, changePayload()], [parseServerConfigurationSnapshotResponse, snapshotPayload()]])
    assert.throws(() => parse(payload, other, window));
});

test("nullable legacy evidence and never-collected configuration states remain valid without fabricated source rows", () => {
  const legacy = file(); legacy.readStallMilliseconds = null; legacy.writeStallMilliseconds = null;
  assert.equal(parseDatabaseFileHealthResponse(page(legacy), target).items[0].readStallMilliseconds, null);
  const pending = targetPayload(); Object.assign(pending.collectors[0], { state: "pending", reason: "never_collected",
    lastExecutionOutcome: null, durationMilliseconds: null, lastAttemptAtUtc: null, lastSuccessAtUtc: null });
  assert.equal(parseTargetHealthResponse(pending, target).collectors[0].lastSuccessAtUtc, null);
  const empty = { ...snapshotPayload(), runId: null, observedAtUtc: null, coverage: "never_collected", collectorEnabled: false, lastOutcome: null, items: [] };
  assert.equal(parseServerConfigurationSnapshotResponse(empty, target).serverMajorVersion, 16);
  const noChanges = { ...changePayload(), publicationGeneration: 0, coverage: "never_collected", items: [] };
  assert.equal(parseServerConfigurationChangesResponse(noChanges, target, window).items.length, 0);
});

test("older database inventory omits log reuse wait without losing inventory or fabricating an observation", async () => {
  const legacy = database(); delete legacy.logReuseWaitDescription;
  const inventory = page(legacy);
  const parsed = parseDatabaseHealthResponse(inventory, target);
  assert.equal(parsed.items[0].name, "Orders");
  assert.equal(parsed.items[0].logReuseWaitDescription, null);
  assert.equal(parsed.items[0].collector.lastSuccessAtUtc, at);
  assert.equal(parseDatabaseHealthResponse(page({ ...legacy, logReuseWaitDescription: null }), target).items[0].logReuseWaitDescription, null);
  for (const invalid of ["", 0, false, {}, "NO WAIT", "x".repeat(61)])
    assert.throws(() => parseDatabaseHealthResponse(page({ ...legacy, logReuseWaitDescription: invalid }), target));
  assert.throws(() => parseDatabaseHealthResponse(inventory, other));
  assert.throws(() => parseDatabaseHealthResponse(page({ ...legacy, databaseId: 0 }), target));
  assert.throws(() => parseDatabaseHealthResponse(page({ ...legacy, collector: { state: "current" } }), target));
  await withFetch(async () => response(inventory), async () => {
    const fetched = await getDatabaseHealth(target, new AbortController().signal);
    assert.equal(fetched.items[0].name, "Orders");
    assert.equal(fetched.items[0].logReuseWaitDescription, null);
  });
});

test("all six native collector loss kinds remain visible while unknown loss enums are rejected", () => {
  for (const sampleLossKind of ["source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "duplicate_overlap", "visibility_incomplete"]) {
    const payload = targetPayload();
    Object.assign(payload.collectors[0], { sampleLossKind, minimumLostItems: 1, minimumLostBytes: 0, lossCountIsExact: false, hasVisibilityGap: true });
    assert.equal(parseTargetHealthResponse(payload, target).collectors[0].sampleLossKind, sampleLossKind);
  }
  const unknown = targetPayload(); unknown.collectors[0].sampleLossKind = "unknown_loss";
  assert.throws(() => parseTargetHealthResponse(unknown, target));
});

test("malformed target/collector/metric structures fail before consumers can render unchecked fields", () => {
  for (const mutate of [p => delete p.collectors, p => p.collectors[0].state = "healthy", p => p.collectors[0].sourceRows = Number.MAX_SAFE_INTEGER + 1,
    p => p.collectors[0].reason = "unknown", p => p.collectors[0].circuitState = "broken", p => p.collectors[0].lastExecutionOutcome = "NoData",
    p => p.coreMetrics[0].sampleId = "1", p => p.coreMetrics[0].value = Infinity, p => p.coreMetrics[0].dimensions.push(p.coreMetrics[0].dimensions[0]),
    p => p.coreMetrics[0].dimensions[0].value = "\ud800", p => p.coreMetrics[0].dimensions = Array(17).fill({ key: "x", value: "x" }),
    p => p.coreMetrics.push(p.coreMetrics[0]), p => p.repositoryTimeUtc = "2026-02-30T09:00:00Z"] ) {
    const payload = targetPayload(); mutate(payload); assert.throws(() => parseTargetHealthResponse(payload, target));
  }
});

test("database/file page bounds, enums and counter arithmetic reject malformed evidence without rounding", () => {
  for (const mutate of [p => p.items = Array(26).fill(database()), p => p.items[0].collector = { state: "current" },
    p => p.items[0].databaseId = 0, p => p.items[0].state = "unknown", p => p.items[0].isReadOnly = "false",
    p => p.items[0].name = "😀".repeat(257), p => p.nextCursor = "x".repeat(257)]) {
    const payload = page(database()); mutate(payload); assert.throws(() => parseDatabaseHealthResponse(payload, target));
  }
  assert.equal(parseDatabaseHealthResponse(page({ ...database(), name: "😀".repeat(256) }), target).items[0].name.length, 512);
  for (const extra of [{ fileId: 65536 }, { readCount: 1 }, { readCount: "9223372036854775808" }, { sizeBytes: "01" },
    { readStallMilliseconds: null }, { ioStallMilliseconds: "8" }, { maximumSizeBytes: "1" }, { growthPercent: 101 }])
    assert.throws(() => parseDatabaseFileHealthResponse(page({ ...file(), ...extra }), target));
});

test("option/configuration histories bind exact microsecond windows and half-open detection times", () => {
  for (const parse of [parseDatabaseOptionChangesResponse, parseServerConfigurationChangesResponse]) {
    const payload = parse === parseDatabaseOptionChangesResponse ? optionPayload() : changePayload();
    assert.equal(parse({ ...payload, fromUtc: "2026-09-26T08:00:00.1234560Z" }, target, window).fromUtc, "2026-09-26T08:00:00.1234560Z");
    assert.throws(() => parse({ ...payload, fromUtc: "2026-09-26T08:00:00.123457Z" }, target, window));
    assert.throws(() => parse({ ...payload, items: [{ ...payload.items[0], detectedAtUtc: toUtc }] }, target, window));
    assert.throws(() => parse({ ...payload, items: Array(101).fill(payload.items[0]) }, target, window));
    assert.throws(() => parse({ ...payload, items: [{ ...payload.items[0], detectedAtUtc: "2026-09-26T09:00:00.1234561Z" }] }, target, window));
  }
  assert.throws(() => parseDatabaseOptionChangesResponse({ ...optionPayload(), semantics: "current_only" }, target, window));
  for (const mutate of [p => p.items[0].configuredChanged = false, p => p.items[0].sourceType = "float", p => p.items[0].configuredValue = "2147483648",
    p => p.publicationGeneration = 0, p => p.targetRevision = 0]) {
    const payload = changePayload(); mutate(payload); assert.throws(() => parseServerConfigurationChangesResponse(payload, target, window));
  }
  assert.throws(() => parseServerConfigurationSnapshotResponse({ ...snapshotPayload(), runId: null }, target));
});

async function withFetch(mock, work) {
  const previous = globalThis.fetch; clearApiCache(); globalThis.fetch = mock;
  try { return await work(); } finally { clearApiCache(); globalThis.fetch = previous; }
}
const response = (payload, headers = {}) => new Response(JSON.stringify(payload), { headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers } });
const safeError = error => error instanceof HealthRequestError && error.message === "Health evidence returned an invalid or mismatched response. Refresh to retry.";

test("all six GET functions use endpoint-specific validation while keeping their native API signatures", async () => {
  const calls = [];
  await withFetch(async (path, init) => {
    calls.push({ path, init });
    const payload = path.includes("/database-option-changes?") ? optionPayload() : path.includes("/server-configuration-changes?") ? changePayload()
      : path.includes("/server-configuration-snapshot?") ? snapshotPayload() : path.includes("/databases?") ? page(database())
      : path.includes("/files?") ? page(file()) : targetPayload();
    return response(payload);
  }, async () => {
    const signal = new AbortController().signal;
    await getTargetHealth(target, signal); await getDatabaseHealth(target, signal); await getDatabaseFileHealth(target, signal);
    await getDatabaseOptionChanges(target, window, signal); await getServerConfigurationChanges(target, window, signal); await getServerConfigurationSnapshot(target, signal);
  });
  assert.equal(calls.length, 6);
  assert.ok(calls.every(call => call.init.credentials === "same-origin" && call.init.headers.get("accept") === "application/json"));
  assert.equal(new URL(calls[3].path, "https://localhost").searchParams.get("fromUtc"), fromUtc);
});

test("malformed JSON, media types, Unicode, foreign scopes and oversized bodies return safe health failures", async () => {
  for (const reply of [() => response({ ...targetPayload(), instanceId: other }), () => response(targetPayload(), { "content-type": "text/plain" }),
    () => new Response("{", { headers: { "content-type": "application/json" } }),
    () => new Response(new Uint8Array([255]), { headers: { "content-type": "application/json" } }),
    () => response(targetPayload(), { "content-length": String(healthResponseMaximumBytes + 1) }),
    () => new Response(" ".repeat(healthResponseMaximumBytes + 1), { headers: { "content-type": "application/json" } })])
    await withFetch(async () => reply(), async () => { await assert.rejects(getTargetHealth(target, new AbortController().signal), safeError); });
});

test("health response cap cancels declared oversized streams before waiting for their body", { timeout: 1000 }, async () => {
  let cancelled = false;
  const stream = new ReadableStream({
    start(controller) { controller.enqueue(new TextEncoder().encode(JSON.stringify(targetPayload()))); },
    cancel() { cancelled = true; },
  });
  await withFetch(async () => new Response(stream, { headers: { "content-type": "application/json", "content-length": String(healthResponseMaximumBytes + 1) } }), async () => {
    await assert.rejects(getTargetHealth(target, new AbortController().signal), safeError);
  });
  assert.equal(cancelled, true);
});

test("health transport retains original cancellation reasons and safe denied/status messages", async () => {
  let resolve, calls = 0;
  await withFetch(() => { calls++; return new Promise(done => { resolve = done; }); }, async () => {
    const controller = new AbortController(), reason = new DOMException("Investigation changed", "AbortError");
    const work = getTargetHealth(target, controller.signal);
    const rejected = assert.rejects(work, error => error === reason);
    await Promise.resolve(); await Promise.resolve(); controller.abort(reason); await rejected;
    resolve(response(targetPayload()));
    await assert.rejects(getDatabaseHealth(target, controller.signal), error => error === reason);
    assert.equal(calls, 1);
  });
  for (const [status, message] of [[403, /not authorized/], [404, /No health snapshot/], [409, /Restart from the first page/], [429, /Too many health/], [504, /execution limit/]])
    await withFetch(async () => new Response(null, { status }), async () => {
      await assert.rejects(getTargetHealth(target, new AbortController().signal), error => error instanceof HealthRequestError && message.test(error.message));
    });
});
