import test from "node:test";
import assert from "node:assert/strict";
import {
  getDestinationsForAdmin, getMaintenanceForAdmin,
  parseDestinationAdminPage, parseMaintenanceAdminPage,
} from "../src/features/alerts/alertAdminListsApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const other = "22222222-2222-4222-8222-222222222222";
const maintenance = {
  windowId: "33333333-3333-4333-8333-333333333333",
  startsAtUtc: "2026-10-01T12:00:00Z", endsAtUtc: "2026-10-01T13:00:00Z",
  reason: "Planned patch", revision: 3, cancelledAtUtc: null,
};
const destination = {
  destinationId: "44444444-4444-4444-8444-444444444444",
  kind: "https-webhook", enabled: false, approved: false, revision: 5,
};
const page = items => ({ targetId: target, listingStartedUtc: "2026-09-29T12:00:00Z",
  items, hasMore: true, nextCursor: "YWJjZA==" });

test("maintenance pages retain cancellations and reject target or window errors", () => {
  assert.equal(parseMaintenanceAdminPage(page([maintenance]), target).items[0].reason, "Planned patch");
  assert.equal(parseMaintenanceAdminPage(page([{ ...maintenance, cancelledAtUtc: "2026-09-29T12:00:00Z" }]), target).items[0].cancelledAtUtc,
    "2026-09-29T12:00:00Z");
  assert.throws(() => parseMaintenanceAdminPage(page([maintenance]), other), /invalid/);
  assert.throws(() => parseMaintenanceAdminPage(page([maintenance, maintenance]), target), /invalid/);
  assert.throws(() => parseMaintenanceAdminPage(page([{ ...maintenance, endsAtUtc: maintenance.startsAtUtc }]), target), /invalid/);
  assert.throws(() => parseMaintenanceAdminPage({ ...page([maintenance]), hasMore: false }, target), /invalid/);
});

test("destination pages contain only bounded public state", () => {
  assert.deepEqual(parseDestinationAdminPage(page([destination]), target).items[0], destination);
  assert.throws(() => parseDestinationAdminPage(page([{ ...destination, kind: "smtp" }]), target), /invalid/);
  assert.throws(() => parseDestinationAdminPage(page([{ ...destination, revision: 0 }]), target), /invalid/);
  assert.throws(() => parseDestinationAdminPage(page([{ ...destination, configurationReference: "provisioned/secret" }]), target), /invalid/);
  assert.throws(() => parseDestinationAdminPage({ ...page([destination]), items: new Array(101).fill(destination) }, target), /invalid/);
  assert.equal(parseDestinationAdminPage({ ...page([destination]), hasMore: false, nextCursor: null }, target).nextCursor, undefined);
});

test("admin list clients bind target, cursor, and role-specific access errors", async () => {
  const prior = globalThis.fetch;
  try {
    let request;
    globalThis.fetch = async (url, options) => {
      request = { url, options };
      return new Response(JSON.stringify(page([maintenance])), { status: 200, headers: { "content-type": "application/json" } });
    };
    const signal = new AbortController().signal;
    assert.equal((await getMaintenanceForAdmin(target, signal, "YWJjZA==")).items[0].windowId, maintenance.windowId);
    assert.match(request.url, new RegExp(`/observation-targets/${target}/alerts/maintenance\\?limit=100&cursor=`));
    assert.equal(request.options.credentials, "same-origin");
    assert.ok(request.options.signal instanceof AbortSignal); assert.equal(request.options.signal.aborted, signal.aborted);
    globalThis.fetch = async () => new Response(null, { status: 403 });
    await assert.rejects(() => getMaintenanceForAdmin(target, signal), /Target Administrator/);
    await assert.rejects(() => getDestinationsForAdmin(target, signal), /Security Administrator/);
  } finally { globalThis.fetch = prior; }
});
