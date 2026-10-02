import assert from "node:assert/strict";
import test from "node:test";
import { alertMatchesFilter } from "../src/features/alerts/alertFilter.ts";

const states = ["normal", "pending", "firing", "acknowledged", "resolved"];

test("alert filters keep all loaded states distinct from active and firing", () => {
  for (const state of states) assert.equal(alertMatchesFilter("all", state), true, state);
  assert.deepEqual(states.filter((state) => alertMatchesFilter("active", state)), ["pending", "firing", "acknowledged"]);
  assert.deepEqual(states.filter((state) => alertMatchesFilter("firing", state)), ["firing"]);
});

// Mounted loading, accessible error/retry and pending pagination behavior lives
// in component-tests/AlertPanel.spec.mjs; this file retains the runtime filter contract.
