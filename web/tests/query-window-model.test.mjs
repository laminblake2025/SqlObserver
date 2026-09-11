import assert from "node:assert/strict";
import test from "node:test";
import { queryPerformanceDefaultHref, resolveQueryWindow } from "../src/features/queries/queryWindowModel.ts";

const now = Date.parse("2026-09-10T12:00:00Z");
const baseScope = { target: "server/one", range: "custom", from: "2026-09-03T12:00:00Z", to: "2026-09-10T12:00:00Z", compare: true };

test("query window accepts the exact seven-day boundary", () => {
  const result = resolveQueryWindow(baseScope, now);
  assert.equal(result.state, "valid");
  if (result.state !== "valid") return;
  assert.deepEqual(result.window, { fromUtc: "2026-09-03T12:00:00.000Z", toUtc: "2026-09-10T12:00:00.000Z" });
});

test("query window reports a clear unsupported state above seven days", () => {
  const result = resolveQueryWindow({ ...baseScope, from: "2026-09-03T11:59:59.999Z" }, now);
  assert.equal(result.state, "unsupported");
  assert.match(result.message, /up to 7 days/);
});

test("invalid query windows do not produce a fallback request window", () => {
  const result = resolveQueryWindow({ ...baseScope, from: "not-a-time", to: "2026-09-10T12:00:00Z" }, now);
  assert.equal(result.state, "invalid");
  assert.match(result.message, /valid UTC range/);
  assert.equal(queryPerformanceDefaultHref(baseScope), "#/queries?target=server%2Fone&range=24h&compare=1");
});

test("range resolution is stable for unchanged inputs and keeps zero-duration rejection", () => {
  const first = resolveQueryWindow({ target: "server", range: "7d", compare: false }, now);
  const second = resolveQueryWindow({ target: "server", range: "7d", compare: false }, now);
  assert.deepEqual(first, second);
  assert.equal(resolveQueryWindow({ ...baseScope, from: baseScope.to }, now).state, "invalid");
});
