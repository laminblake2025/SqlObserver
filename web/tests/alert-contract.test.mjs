import assert from "node:assert/strict";
import test from "node:test";
import { getActiveAlerts, getFleetActiveAlerts } from "../src/features/alerts/alertApi.ts";
import { parseActiveAlerts, parseFleetAlerts, readJsonBounded } from "../src/features/alerts/alertRuntimeParser.ts";

test("production alert parser binds target and rejects malformed identities, states, dates and oversized pages", () => {
  const target = "11111111-1111-4111-8111-111111111111";
  const item = { alertId: "22222222-2222-4222-8222-222222222222", ruleId: "33333333-3333-4333-8333-333333333333", ruleName: "CPU high", state: "firing", firstObservedUtc: "2026-08-24T00:00:00Z", value: null, deliverySuppressed: false };
  const page = { targetId: target, snapshotUtc: "2026-08-24T00:00:00Z", nextCursor: null, items: [item] };
  const malformed = [
    ["another target", { ...page, targetId: "44444444-4444-4444-8444-444444444444" }],
    ["101 rows", { ...page, items: Array.from({ length: 101 }, () => item) }],
    ["unknown state", { ...page, items: [{ ...item, state: "critical" }] }],
    ["invalid alert UUID", { ...page, items: [{ ...item, alertId: "invalid" }] }],
    ["invalid rule UUID", { ...page, items: [{ ...item, ruleId: "invalid" }] }],
    ["non-UTC observation", { ...page, items: [{ ...item, firstObservedUtc: "2026-08-24T01:00:00+01:00" }] }],
    ["invalid transition date", { ...page, items: [{ ...item, acknowledgedUtc: "yesterday" }] }],
    ["invalid cursor", { ...page, nextCursor: "invalid*" }],
    ["unbounded cursor", { ...page, nextCursor: "a".repeat(1025) }],
    ["non-numeric value", { ...page, items: [{ ...item, value: "0" }] }],
    ["non-finite value", { ...page, items: [{ ...item, value: Infinity }] }],
  ];
  for (const [reason, value] of malformed) assert.throws(() => parseActiveAlerts(value, target), /invalid/, reason);
  const accepted = parseActiveAlerts(page, target);
  assert.equal(accepted.nextCursor, undefined);
  assert.equal(accepted.items[0].value, null);
  assert.equal(parseActiveAlerts({ ...page, items: [{ ...item, value: 0 }] }, target).items[0].value, 0);
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
