import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { getActiveAlerts, getFleetActiveAlerts } from "../src/features/alerts/alertApi.ts";
import { parseActiveAlerts, parseFleetAlerts, readJsonBounded } from "../src/features/alerts/alertRuntimeParser.ts";

test("alert client binds target and rejects unknown states or oversized pages", async () => {
  const source = await readFile(new URL("../src/features/alerts/alertApi.ts", import.meta.url), "utf8");
  assert.match(source, /row\.targetId !== targetId/);
  assert.match(source, /row\.items\.length > 100/);
  assert.match(source, /isState/);
  assert.match(source, /isUuid/);
  assert.match(source, /isUtc/);
});

test("alert UI exposes acknowledgement only for firing alerts", async () => {
  const source = await readFile(new URL("../src/features/alerts/TargetAlertsPanel.tsx", import.meta.url), "utf8");
  assert.match(source, /item\.state === "firing"/);
  assert.match(source, /acknowledgeAlert/);
});

test("alert runtime accepts terminal null cursor and collector-health null value", async () => {
  const source = await readFile(new URL("../src/features/alerts/alertApi.ts", import.meta.url), "utf8");
  assert.match(source, /nextCursor === null/);
  assert.match(source, /candidate\.value !== undefined/);
});

test("production alert parser and bounded streamed reader execute at runtime", async () => {
  const target = "11111111-1111-4111-8111-111111111111";
  const page = parseActiveAlerts({ targetId: target, snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: null, items: [{ alertId: "22222222-2222-4222-8222-222222222222", ruleId: "33333333-3333-4333-8333-333333333333", ruleName: "CPU high", state: "firing", firstObservedUtc: "2026-08-24T00:00:00Z", value: null, deliverySuppressed: false }] }, target);
  assert.equal(page.nextCursor, undefined);
  const response = new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode('{"ok":true}')); controller.close(); } }));
  assert.deepEqual(await readJsonBounded(response), { ok: true });
  const oversized = new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(257 * 1024)); controller.close(); } }));
  await assert.rejects(() => readJsonBounded(oversized), /too large/);
  assert.equal(parseActiveAlerts({ targetId: target, snapshotUtc: "2026-08-24T00:00:00+00:00", nextCursor: null, items: [] }, target).targetId, target);
  assert.throws(() => parseActiveAlerts({ targetId: target, snapshotUtc: "2026-08-24T01:00:00+01:00", nextCursor: null, items: [] }, target), /invalid/);
});

test("fleet alert parser requires target identity and displays the configured rule name", () => {
  const item = { targetId: "11111111-1111-4111-8111-111111111111", targetName: "Production SQL", alertId: "22222222-2222-4222-8222-222222222222", ruleId: "33333333-3333-4333-8333-333333333333", ruleName: "CPU high", state: "firing", firstObservedUtc: "2026-08-24T00:00:00Z", deliverySuppressed: false };
  const page = { snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: null, items: [item] };
  assert.equal(parseFleetAlerts(page).items[0].ruleName, "CPU high");
  assert.equal(parseFleetAlerts(page).items[0].targetName, "Production SQL");
  assert.throws(() => parseFleetAlerts({ ...page, items: [{ ...item, targetId: "invalid" }] }), /invalid/);
  assert.throws(() => parseFleetAlerts({ ...page, items: [{ ...item, ruleName: "" }] }), /invalid/);
  assert.throws(() => parseFleetAlerts({ ...page, nextCursor: "invalid*" }), /invalid/);
});

test("fleet alert client carries the bounded cursor into the next page", async () => {
  const calls = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    return new Response(JSON.stringify({ snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: calls.length === 1 ? "Y3Vyc29yMQ==" : null, items: [] }), { headers: { "content-type": "application/json" } });
  };
  try {
    const first = await getFleetActiveAlerts(new AbortController().signal);
    const second = await getFleetActiveAlerts(new AbortController().signal, 100, first.nextCursor);
    assert.equal(first.nextCursor, "Y3Vyc29yMQ==");
    assert.equal(second.nextCursor, undefined);
    assert.match(calls[0], /\/api\/v1\/alerts\/active\?limit=100/);
    assert.match(calls[1], /cursor=Y3Vyc29yMQ%3D%3D/);
  } finally { globalThis.fetch = originalFetch; }
});

test("production alert client replays the server cursor across two pages", async () => {
  const target = "11111111-1111-4111-8111-111111111111";
  const pages = [
    { targetId: target, snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: "Y3Vyc29yMQ==", items: [] },
    { targetId: target, snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: null, items: [] },
  ];
  const calls = [];
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url) => {
    calls.push(String(url));
    return new Response(JSON.stringify(pages[calls.length - 1]), { headers: { "content-type": "application/json" } });
  };
  try {
    const first = await getActiveAlerts(target, new AbortController().signal, 100);
    const second = await getActiveAlerts(target, new AbortController().signal, 100, first.nextCursor);
    assert.equal(first.nextCursor, "Y3Vyc29yMQ==");
    assert.equal(second.nextCursor, undefined);
    assert.match(calls[0], /limit=100/);
    assert.match(calls[1], /cursor=Y3Vyc29yMQ%3D%3D/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
