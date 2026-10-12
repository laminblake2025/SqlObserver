import assert from "node:assert/strict";
import { test } from "node:test";
import {
  readReportSelection, reportSelectionHref, retainReportSelectionHref, resolveReportWindow, reportContextKey,
} from "../src/features/reports/reportsWorkspaceModel.ts";
import { overviewHref, readOverviewScope } from "../src/features/overview/overviewModel.ts";

const target = "11111111-1111-4111-8111-111111111111";
const scope = { target, range: "custom", from: "2026-08-01T13:45:00.000001Z", to: "2026-08-02T13:45:00.000002Z", compare: true, databaseId: 5 };
const now = Date.parse("2026-08-24T13:00:00Z");

test("report kind URLs default to snapshot and reject duplicate, empty and unknown values", () => {
  assert.deepEqual(readReportSelection("#/reports"), { state: "valid", kind: "instance-health" });
  for (const kind of ["instance-health", "performance-window", "incident-evidence", "capacity-readiness"])
    assert.deepEqual(readReportSelection(`#/reports?reportKind=${kind}`), { state: "valid", kind });
  for (const query of ["reportKind=", "reportKind=unknown", "reportKind=performance-window&reportKind=performance-window", "reportKind=capacity-readiness&reportKind=incident-evidence"])
    assert.equal(readReportSelection(`#/reports?${query}`).state, "invalid");
});

test("report links restore shared scope and exact microsecond boundaries", () => {
  const href = reportSelectionHref(scope, "incident-evidence");
  assert.deepEqual(readOverviewScope(href), scope);
  assert.deepEqual(readReportSelection(href), { state: "valid", kind: "incident-evidence" });
  assert.throws(() => reportSelectionHref(scope, "unknown"));
});

test("time and target navigation retain invalid report parameters without leaking them to other pages", () => {
  const current = "#/reports?reportKind=unknown&reportKind=capacity-readiness";
  const destination = overviewHref({ ...scope, target: "44444444-4444-4444-8444-444444444444", range: "1h" }, "reports");
  const retained = retainReportSelectionHref(destination, current);
  assert.deepEqual(new URLSearchParams(retained.split("?")[1]).getAll("reportKind"), ["unknown", "capacity-readiness"]);
  assert.equal(readReportSelection(retained).state, "invalid");
  assert.equal(retainReportSelectionHref(overviewHref(scope, "queries"), current), overviewHref(scope, "queries"));
  assert.equal(retainReportSelectionHref(destination, "#/queries?reportKind=unknown"), destination);
  const repaired = reportSelectionHref(scope, "performance-window");
  assert.equal(retainReportSelectionHref(repaired, current), repaired);
});

test("windowed reports resolve canonical shared UTC bounds without losing source precision", () => {
  assert.deepEqual(resolveReportWindow(scope, "performance-window", now), { fromUtc: scope.from, toUtc: scope.to });
  assert.deepEqual(resolveReportWindow({ ...scope, from: "2026-08-01T13:45:00.0000010Z" }, "performance-window", now), { fromUtc: scope.from, toUtc: scope.to });
  assert.equal(resolveReportWindow({ ...scope, from: "invalid", to: "invalid" }, "instance-health", now), undefined);
});

test("report-specific caps reject wider, future and malformed shared ranges", () => {
  const sevenDays = { ...scope, from: "2026-08-01T00:00:00Z", to: "2026-08-08T00:00:00Z" };
  assert.doesNotThrow(() => resolveReportWindow(sevenDays, "performance-window", now));
  assert.throws(() => resolveReportWindow({ ...sevenDays, to: "2026-08-08T00:00:00.000001Z" }, "incident-evidence", now), /shared time range.*at most 7 days/);
  const month = { ...scope, from: "2026-07-01T00:00:00Z", to: "2026-08-01T00:00:00Z" };
  assert.doesNotThrow(() => resolveReportWindow(month, "capacity-readiness", now));
  for (const changed of [{ to: "2026-08-01T00:00:00.000001Z", from: month.from }, { from: "2026-02-30T00:00:00Z" }, { to: "2026-08-25T00:00:00Z" }, { from: scope.to, to: scope.from }])
    assert.throws(() => resolveReportWindow({ ...scope, ...changed }, "capacity-readiness", now), /Update the shared time range/);
});

test("fresh Live window resolution moves with now and explicit context changes have distinct identities", () => {
  const live = { ...scope, range: "1h", from: undefined, to: undefined };
  const first = resolveReportWindow(live, "performance-window", now);
  const later = resolveReportWindow(live, "performance-window", now + 5000);
  assert.equal(first.toUtc, "2026-08-24T13:00:00.000Z");
  assert.equal(later.toUtc, "2026-08-24T13:00:05.000Z");
  const selection = { state: "valid", kind: "performance-window" };
  const key = reportContextKey(target, selection, live);
  assert.equal(reportContextKey(target.toUpperCase(), selection, { ...live, compare: false, databaseId: 9 }), key);
  for (const [id, chosen, context] of [["44444444-4444-4444-8444-444444444444", selection, live], [target, { state: "valid", kind: "incident-evidence" }, live], [target, selection, { ...live, range: "6h" }]])
    assert.notEqual(reportContextKey(id, chosen, context), key);
  const customKey = reportContextKey(target, selection, scope);
  for (const changed of [{ from: "2026-08-01T13:45:00.000003Z" }, { to: "2026-08-02T13:45:00.000003Z" }])
    assert.notEqual(reportContextKey(target, selection, { ...scope, ...changed }), customKey);
});
