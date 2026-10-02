import test from "node:test";
import assert from "node:assert/strict";
import { selectedFileAllocation } from "../src/features/resources/selectedFileAllocationModel.ts";

const target = "11111111-1111-4111-8111-111111111111";
const collector = { state: "current", reason: "none", hasVisibilityGap: false };
const file = () => ({ databaseId: 5, fileId: 1, logicalName: "Orders_log", fileType: "log", state: "online",
  sizeBytes: "9007199254740993", maximumSizeBytes: "9223372036854775807", growthBytes: "9007199254740994", growthPercent: 0,
  observedAtUtc: "2026-10-02T12:00:00.123456Z", collector });
const input = () => ({ targetId: target, selection: { databaseId: 5, fileId: 1 }, databaseId: 5,
  page: { instanceId: target, collector, items: [file()], repositoryTimeUtc: "2026-10-02T12:01:00Z", nextCursor: null },
  loading: false, failed: false, contextCurrent: true });

test("selected allocation retains native Int64 strings and distinguishes fixed, percentage, zero and ambiguous growth", () => {
  const original = input(), result = selectedFileAllocation(original);
  assert.equal(result.state, "available");
  assert.equal(result.file, original.page.items[0]);
  assert.equal(result.file.sizeBytes, "9007199254740993");
  assert.equal(result.maximum, "9223372036854775807");
  assert.equal(result.growth, "9007199254740994 bytes per growth event");
  const percentage = input(); Object.assign(percentage.page.items[0], { growthBytes: "0", growthPercent: 10 });
  assert.equal(selectedFileAllocation(percentage).growth, "10% per growth event");
  const zero = input(); zero.page.items[0].growthBytes = "0";
  assert.equal(selectedFileAllocation(zero).growth, "Autogrowth disabled (zero increment reported)");
  const ambiguous = input(); ambiguous.page.items[0].growthPercent = 10;
  assert.match(selectedFileAllocation(ambiguous).growth, /mode is ambiguous/);
});

test("unbounded native maximum stays explicitly unlimited or unknown without deriving free space or forecasts", () => {
  const payload = input(); payload.page.items[0].maximumSizeBytes = null;
  const result = selectedFileAllocation(payload);
  assert.equal(result.maximum, "No finite maximum reported (unlimited or unknown)");
  assert.equal("freeBytes" in result, false); assert.equal("daysUntilFull" in result, false);
  payload.page.items[0].maximumSizeBytes = payload.page.items[0].sizeBytes;
  assert.equal(selectedFileAllocation(payload).maximum, payload.page.items[0].sizeBytes);
});

test("allocation never substitutes another file, database, target or stale request context", () => {
  const cases = [
    { ...input(), selection: undefined }, { ...input(), selection: { databaseId: 5, fileId: 99 } },
    { ...input(), databaseId: 6 }, { ...input(), contextCurrent: false }, { ...input(), failed: true },
    { ...input(), loading: true }, { ...input(), page: undefined },
  ];
  const foreign = input(); foreign.page.instanceId = "22222222-2222-4222-8222-222222222222"; cases.push(foreign);
  const duplicate = input(); duplicate.page.items.push(file()); cases.push(duplicate);
  for (const value of cases) { const result = selectedFileAllocation(value); assert.equal(result.state, "unavailable"); assert.equal("file" in result, false); }
  assert.match(selectedFileAllocation(cases[1]).notice, /absent.*no other file/);
  assert.match(selectedFileAllocation(cases[4]).notice, /could not be loaded/);
});

test("stale and incomplete page or file evidence retains source facts with explicit attention", () => {
  assert.equal(selectedFileAllocation(input()).attention, false);
  for (const level of ["page", "file"]) for (const alteration of [{ state: "stale" }, { hasVisibilityGap: true }]) {
    const value = input();
    if (level === "page") value.page.collector = { ...collector, ...alteration };
    else value.page.items[0].collector = { ...collector, ...alteration };
    assert.equal(selectedFileAllocation(value).attention, true);
    assert.equal(selectedFileAllocation(value).file.logicalName, "Orders_log");
  }
});
