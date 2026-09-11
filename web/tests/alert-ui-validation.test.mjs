import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import { alertMatchesFilter } from "../src/features/alerts/alertFilter.ts";

const states = ["normal", "pending", "firing", "acknowledged", "resolved"];

test("alert filters keep all loaded states distinct from active and firing", () => {
  for (const state of states) assert.equal(alertMatchesFilter("all", state), true, state);
  assert.deepEqual(states.filter((state) => alertMatchesFilter("active", state)), ["pending", "firing", "acknowledged"]);
  assert.deepEqual(states.filter((state) => alertMatchesFilter("firing", state)), ["firing"]);
});

test("alert panel exposes loading/error states and protects pending load-more", async () => {
  const panel = await readFile(new URL("../src/features/alerts/TargetAlertsPanel.tsx", import.meta.url), "utf8");
  assert.match(panel, /Loading target-scoped alerts/);
  assert.match(panel, /role="alert"/);
  assert.match(panel, /disabled=\{!nextCursor \|\| loadingMore\}/);
  assert.match(panel, /setError\(undefined\)/);
});
