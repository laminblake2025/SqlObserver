import assert from "node:assert/strict";
import test from "node:test";
import { readRoute } from "../src/dashboardModel.ts";
import { issueHref } from "../src/features/health/serverDashboardModel.ts";
import { readOverviewScope } from "../src/features/overview/overviewModel.ts";

const target = "11111111-1111-4111-8111-111111111111";
const scope = { target, range: "custom", from: "2026-09-23T10:00:00Z", to: "2026-09-23T11:00:00Z", compare: true };

test("server investigation link preserves the selected range and opens activity at the issue time", () => {
  const at = "2026-09-23T10:32:00Z";
  const href = issueHref(scope, target, "activity", at);
  assert.deepEqual(readOverviewScope(href), scope);
  assert.deepEqual(readRoute(href), { page: "activity", target, activityAtUtc: at });
  const alerts = issueHref(scope, target, "alerts", at);
  assert.deepEqual(readOverviewScope(alerts), scope);
  assert.equal(readRoute(alerts).activityAtUtc, undefined);
});
