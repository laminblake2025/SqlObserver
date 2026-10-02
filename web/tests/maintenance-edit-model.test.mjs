import test from "node:test";
import assert from "node:assert/strict";
import { beginMaintenanceEdit, prepareMaintenanceEdit } from "../src/features/alerts/maintenanceEditModel.ts";
import { MaintenanceAttempts, updateMaintenanceWindow } from "../src/features/alerts/maintenanceAdminActions.ts";
import { clearApiCache } from "../src/apiClient.ts";
import { parseMaintenanceAdminPage } from "../src/features/alerts/alertAdminListsApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const original = () => ({ windowId: "22222222-2222-4222-8222-222222222222", revision: 3, cancelledAtUtc: null,
  startsAtUtc: "2026-10-02T12:00:00.1234560+00:00", endsAtUtc: "2026-10-02T12:00:30.654321Z", reason: "Patch" });

test("maintenance listing preserves positive microsecond-only windows for inspection and editing", () => {
  const item = { ...original(), endsAtUtc: "2026-10-02T12:00:00.1234570Z" };
  const page = { targetId: target, listingStartedUtc: "2026-10-02T12:00:00Z", items: [item], hasMore: false, nextCursor: null };
  const parsed = parseMaintenanceAdminPage(page, target).items[0];
  assert.deepEqual(prepareMaintenanceEdit(beginMaintenanceEdit(parsed)), {
    startsAtUtc: item.startsAtUtc, endsAtUtc: item.endsAtUtc, reason: item.reason,
  });
  for (const endsAtUtc of [item.startsAtUtc, "2026-10-02T12:00:00.1234550Z", "2026-10-02T12:00:00.1234571Z"])
    assert.throws(() => parseMaintenanceAdminPage({ ...page, items: [{ ...item, endsAtUtc }] }, target), /invalid/);
});

test("reason-only maintenance edits preserve native microseconds even for bounds within the same local minute", () => {
  const item = original(), draft = beginMaintenanceEdit(item);
  assert.equal(draft.startsLocal, draft.endsLocal);
  const payload = prepareMaintenanceEdit({ ...draft, reason: "Updated patch reason" });
  assert.deepEqual(payload, { startsAtUtc: item.startsAtUtc, endsAtUtc: item.endsAtUtc, reason: "Updated patch reason" });
  const changed = prepareMaintenanceEdit({ ...draft, endsLocal: draft.endsLocal.slice(0, -2) + "01" });
  assert.equal(changed.startsAtUtc, item.startsAtUtc);
  assert.equal(changed.endsAtUtc, new Date(draft.endsLocal.slice(0, -2) + "01").toISOString());
});

test("maintenance edits validate exact duration, nonexistent civil times and UTF-8 reason limits", () => {
  const draft = beginMaintenanceEdit(original());
  for (const reason of [" ", "é".repeat(257), "Patch\nrestart"]) assert.throws(() => prepareMaintenanceEdit({ ...draft, reason }), /reason/);
  assert.equal(prepareMaintenanceEdit({ ...draft, reason: "é".repeat(256) }).reason.length, 256);
  assert.throws(() => prepareMaintenanceEdit({ ...draft, startsLocal: "2026-02-30T12:00" }), /does not exist/);
  assert.throws(() => prepareMaintenanceEdit({ ...draft, startsLocal: "0000-10-02T12:00" }), /valid/);
  assert.throws(() => prepareMaintenanceEdit({ ...draft, endsLocal: "2026-10-10T12:00" }), /seven days/);
  assert.throws(() => prepareMaintenanceEdit({ ...draft, endsLocal: "2026-10-01T12:00" }), /seven days/);
  assert.throws(() => beginMaintenanceEdit({ ...original(), cancelledAtUtc: "2026-10-02T10:00:00Z" }), /configured/);
  assert.throws(() => beginMaintenanceEdit({ ...original(), startsAtUtc: "2026-10-02T12:00:00.1234561Z" }));
  const exactWeek = { ...original(), startsAtUtc: "2026-10-02T12:00:00.123456Z", endsAtUtc: "2026-10-09T12:00:00.123456Z" };
  assert.equal(prepareMaintenanceEdit(beginMaintenanceEdit(exactWeek)).endsAtUtc, exactWeek.endsAtUtc);
  assert.throws(() => prepareMaintenanceEdit(beginMaintenanceEdit({ ...exactWeek, endsAtUtc: "2026-10-09T12:00:00.123457Z" })), /seven days/);
});

test("maintenance local edits reject DST gaps and retain untouched repeated-hour instants", () => {
  const previous = process.env.TZ;
  process.env.TZ = "America/New_York";
  try {
    const item = { ...original(), startsAtUtc: "2026-11-01T06:30:00.123456Z", endsAtUtc: "2026-11-01T07:30:00.123456Z" };
    const draft = beginMaintenanceEdit(item);
    assert.equal(draft.startsLocal, "2026-11-01T01:30");
    assert.equal(prepareMaintenanceEdit({ ...draft, reason: "After clock change" }).startsAtUtc, item.startsAtUtc);
    assert.throws(() => prepareMaintenanceEdit({ ...draft, startsLocal: "2026-03-08T02:30" }), /does not exist/);
    const earlier = prepareMaintenanceEdit({ ...draft, startsLocal: "2026-11-01T01:31" });
    assert.equal(earlier.startsAtUtc, "2026-11-01T05:31:00.000Z");
  } finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous; }
});

test("maintenance edit retries retain operation identity only for the exact target/window/revision/payload", () => {
  const attempts = new MaintenanceAttempts(), item = original(), payload = prepareMaintenanceEdit(beginMaintenanceEdit(item));
  const first = attempts.editFor(target, item.windowId, item.revision, payload);
  assert.equal(attempts.editFor(target.toUpperCase(), item.windowId, item.revision, payload), first);
  for (const [targetId, windowId, revision, window] of [[crypto.randomUUID(), item.windowId, 3, payload],
    [target, crypto.randomUUID(), 3, payload], [target, item.windowId, 4, payload],
    [target, item.windowId, 3, { ...payload, reason: "Other" }], [target, item.windowId, 3, { ...payload, endsAtUtc: "2026-10-02T13:00:00Z" }]])
    assert.notEqual(attempts.editFor(targetId, windowId, revision, window), first);
  attempts.completeEdit(target, item.windowId, 3, payload);
  assert.notEqual(attempts.editFor(target, item.windowId, 3, payload), first);
});

test("maintenance PUT carries expected revision and exact bounds; conflict or unconfirmed receipt never becomes success", async () => {
  const previous = globalThis.fetch;
  try {
    const item = original(), payload = prepareMaintenanceEdit(beginMaintenanceEdit(item)), operationId = crypto.randomUUID();
    const signal = new AbortController().signal;
    globalThis.fetch = async (path, options) => {
      assert.equal(path, `/api/v1/observation-targets/${target}/alerts/maintenance/${item.windowId}`);
      assert.equal(options.method, "PUT"); assert.equal(options.credentials, "same-origin");
      assert.deepEqual(JSON.parse(options.body), { ...payload, windowId: item.windowId, expectedRevision: 3, operationId });
      return Response.json({ auditId: crypto.randomUUID(), recordedAtUtc: "2026-10-02T12:00:00.1234560Z" });
    };
    await updateMaintenanceWindow(target, item.windowId, 3, payload, operationId, signal);
    for (const response of [Response.json({ auditId: crypto.randomUUID() }), new Response("secret"),
      Response.json({ auditId: "foreign", recordedAtUtc: "2026-10-02T12:00:00Z" })]) {
      globalThis.fetch = async () => response;
      await assert.rejects(updateMaintenanceWindow(target, item.windowId, 3, payload, operationId, signal), /could not be confirmed/);
    }
    globalThis.fetch = async () => new Response(null, { status: 409 });
    await assert.rejects(updateMaintenanceWindow(target, item.windowId, 3, payload, operationId, signal), /Refresh it/);
    globalThis.fetch = async () => new Response(null, { status: 403 });
    await assert.rejects(updateMaintenanceWindow(target, item.windowId, 3, payload, operationId, signal), /Administrator/);
    const aborted = new AbortController(); aborted.abort(new DOMException("Cancelled", "AbortError"));
    await assert.rejects(updateMaintenanceWindow(target, item.windowId, 3, payload, operationId, aborted.signal), { name: "AbortError" });
  } finally { globalThis.fetch = previous; clearApiCache(); }
});
