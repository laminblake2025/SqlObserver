import test from "node:test";
import assert from "node:assert/strict";
import {
  MaintenanceAttempts, prepareMaintenanceWindow,
  createMaintenanceWindow, cancelMaintenanceWindow,
} from "../src/features/alerts/maintenanceAdminActions.ts";

const target = "11111111-1111-4111-8111-111111111111";
const windowId = "22222222-2222-4222-8222-222222222222";

test("maintenance form converts local times to UTC and enforces the seven-day bound", () => {
  const prepared = prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T13:00", "Planned patch");
  assert.equal(prepared.startsAtUtc, new Date("2026-10-01T12:00").toISOString());
  assert.equal(prepared.endsAtUtc, new Date("2026-10-01T13:00").toISOString());
  assert.throws(() => prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T12:00", "Patch"), /seven days/);
  assert.throws(() => prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-09T13:00", "Patch"), /seven days/);
  assert.throws(() => prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T13:00", "  "), /reason/);
  assert.throws(() => prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T13:00", "é".repeat(257)), /512/);
});

test("maintenance create and cancel reuse operation identities until confirmed", () => {
  const attempts = new MaintenanceAttempts();
  const window = prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T13:00", "Planned patch");
  const first = attempts.createFor(target, window);
  assert.deepEqual(attempts.createFor(target, window), first);
  assert.notDeepEqual(attempts.createFor(target, { ...window, reason: "Other patch" }), first);
  attempts.completeCreate(target, window);
  assert.notDeepEqual(attempts.createFor(target, window), first);
  const cancellation = attempts.cancellationFor(target, windowId, 3);
  assert.equal(attempts.cancellationFor(target, windowId, 3), cancellation);
  assert.notEqual(attempts.cancellationFor(target, windowId, 4), cancellation);
  attempts.completeCancellation(target, windowId, 3);
  assert.notEqual(attempts.cancellationFor(target, windowId, 3), cancellation);
});

test("maintenance mutation requests carry stable identities and expected revision", async () => {
  const previous = globalThis.fetch;
  try {
    const requests = [];
    globalThis.fetch = async (url, options) => {
      requests.push({ url, options, body: JSON.parse(options.body) });
      return new Response(JSON.stringify({ auditId: crypto.randomUUID() }), { status: 200 });
    };
    const signal = new AbortController().signal;
    const window = prepareMaintenanceWindow("2026-10-01T12:00", "2026-10-01T13:00", "Planned patch");
    const attempt = { windowId, operationId: crypto.randomUUID() };
    await createMaintenanceWindow(target, window, attempt, signal);
    await cancelMaintenanceWindow(target, windowId, 3, attempt.operationId, signal);
    assert.match(requests[0].url, new RegExp(`/observation-targets/${target}/alerts/maintenance$`));
    assert.deepEqual(requests[0].body, { ...window, ...attempt });
    assert.equal(requests[0].options.credentials, "same-origin");
    assert.equal(new Headers(requests[0].options.headers).get("content-type"), "application/json");
    assert.match(requests[1].url, /\/maintenance\/22222222-2222-4222-8222-222222222222\/cancel$/);
    assert.deepEqual(requests[1].body, { operationId: attempt.operationId, expectedRevision: 3 });
    globalThis.fetch = async () => new Response(null, { status: 409 });
    await assert.rejects(() => cancelMaintenanceWindow(target, windowId, 3, attempt.operationId, signal), /changed/);
  } finally { globalThis.fetch = previous; }
});
