import test from "node:test";
import assert from "node:assert/strict";
import { chartEvidenceStatus } from "../src/components/evidenceStatusModel.ts";

const series = (extra = {}) => ({ label: "SQL", dimension: null, state: "observed", points: [{ value: 0, samples: 1 }], ...extra });

test("quiet observed metadata counts zero values without claiming healthy or continuous coverage", () => {
  const status = chartEvidenceStatus([series()]);
  assert.equal(status.attention, false);
  assert.equal(status.summary, "observed");
  assert.deepEqual(status.rows[0], { label: "SQL", state: "observed", observed: 1, missing: 0, total: 1 });
  assert.doesNotMatch(status.summary, /healthy|fresh|complete coverage/u);
});

test("missing, unusable and zero-sample buckets stay explicit beside partial source states", () => {
  const status = chartEvidenceStatus([series({ state: "partial", points: [
    { value: 1, samples: 2 }, { value: null, samples: 2 }, { value: 2, samples: 0 }, { value: Infinity, samples: 1 },
  ] })]);
  assert.equal(status.attention, true);
  assert.equal(status.summary, "partial · 3 missing sample buckets");
  assert.equal(status.rows[0].observed, 1);
  assert.equal(status.rows[0].missing, 3);
});

test("empty, unavailable and mixed series never disappear into a successful indicator", () => {
  assert.deepEqual(chartEvidenceStatus([]), { rows: [], summary: "no source series", attention: true });
  assert.equal(chartEvidenceStatus([series({ points: [] })]).summary, "no observed samples");
  const mixed = chartEvidenceStatus([series(), series({ state: "visibility_incomplete", dimension: "Host" })]);
  assert.equal(mixed.attention, true);
  assert.equal(mixed.summary, "observed, visibility incomplete");
  assert.equal(mixed.rows[1].label, "Host");
});
