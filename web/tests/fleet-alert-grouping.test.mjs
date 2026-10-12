import assert from "node:assert/strict";
import test from "node:test";
import { readRoute } from "../src/dashboardModel.ts";
import { alertActivityHref, groupFleetAlerts } from "../src/features/alerts/fleetAlertGrouping.ts";

const alerts = [
  { targetId: "server-a", targetName: "Payments", alertId: "a1", ruleId: "r1", ruleName: "High CPU", firstObservedUtc: "2026-09-28T12:00:00Z", firedUtc: "2026-09-28T12:01:00Z" },
  { targetId: "server-b", targetName: "Warehouse", alertId: "b1", ruleId: "r2", ruleName: "High CPU", firstObservedUtc: "2026-09-28T11:00:00Z" },
  { targetId: "server-a", targetName: "Payments", alertId: "a2", ruleId: "r3", ruleName: "Blocking", firstObservedUtc: "2026-09-28T10:00:00Z" },
];

test("fleet alert groups preserve loaded order and do not merge different servers", () => {
  const servers = groupFleetAlerts(alerts, "server");
  assert.deepEqual(servers.map(group => [group.label, group.items.map(item => item.alertId)]),
    [["Payments", ["a1", "a2"]], ["Warehouse", ["b1"]]]);
  const rules = groupFleetAlerts(alerts, "rule");
  assert.deepEqual(rules.map(group => [group.label, group.items.map(item => item.alertId)]),
    [["High CPU", ["a1", "b1"]], ["Blocking", ["a2"]]]);
  assert.deepEqual(groupFleetAlerts(alerts, "none")[0].items, alerts);
});

test("alert activity link opens an hour around firing with the exact event selected", () => {
  const scope = { target: "old", range: "24h", compare: true };
  const href = alertActivityHref(alerts[0], scope, Date.parse("2026-09-29T12:00:00Z"));
  const params = new URLSearchParams(href.split("?")[1]);
  assert.deepEqual(readRoute(href), { page: "activity", target: "server-a", activityAtUtc: alerts[0].firedUtc });
  assert.equal(params.get("range"), "custom");
  assert.equal(params.get("from"), "2026-09-28T11:31:00.000Z");
  assert.equal(params.get("to"), "2026-09-28T12:31:00.000Z");
  assert.equal(params.get("compare"), null);
});

test("fresh alerts use first observation and never select a future range end", () => {
  const now = Date.parse("2026-09-28T11:05:00Z");
  const href = alertActivityHref(alerts[1], { target: "", range: "24h" }, now);
  const params = new URLSearchParams(href.split("?")[1]);
  assert.equal(params.get("at"), alerts[1].firstObservedUtc);
  assert.equal(params.get("to"), "2026-09-28T11:05:00.000Z");
});
