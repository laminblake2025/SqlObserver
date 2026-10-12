import test from "node:test";
import assert from "node:assert/strict";
import { dashboardBaselineCards } from "../src/features/health/dashboardBaselineModel.ts";

const targetId = "11111111-1111-4111-8111-111111111111";
const metric = "engine.batch_requests_per_second";
const fromUtc = "2026-09-26T08:00:00Z", toUtc = "2026-09-26T10:00:00Z";
const series = (extra = {}) => ({ targetId, metric, unit: "batches/sec", dimension: null, state: "observed", label: "SQL server",
  points: [{ timeUtc: "2026-09-26T08:30:00Z", value: 5, samples: 3 }, { timeUtc: "2026-09-26T09:30:00Z", value: 15, samples: 10 }], ...extra });
const band = (extra = {}) => ({ targetId, metric, unit: "batches/sec", dimension: null,
  fromUtc: "2026-09-26T09:00:00Z", toUtc, low: 8, median: 10, high: 12, label: "SQL workload source",
  visibilityState: "complete", coverage: 1, confidence: .5, sourceFromUtc: "2026-08-29T00:00:00Z", sourceToUtc: "2026-09-26T00:00:00Z", ...extra });
const cards = (extra = {}) => dashboardBaselineCards({ targetId, fromUtc, toUtc, series: [series()], bands: [band()], ...extra });

test("dashboard summary binds four SQL metrics and compares the latest bucket to its own hourly reference", () => {
  const result = cards();
  assert.equal(result.length, 4);
  assert.deepEqual(result.map(card => card.unit), ["batches/sec", "%", "grants", "GiB"]);
  assert.equal(result[0].observation.value, 15);
  assert.equal(result[0].observation.samples, 10);
  assert.equal(result[0].reference.median, 10);
  assert.equal(result[0].referenceQuality, "complete");
  assert.equal(result[1].observationState, "missing");
});

test("latest missing, zero-sample, invalid and duplicated buckets never fall back to earlier good samples", () => {
  const older = series().points[0], latest = series().points[1];
  for (const extra of [{ value: null }, { samples: 0 }]) {
    const current = cards({ series: [series({ points: [older, { ...latest, ...extra }] })] })[0];
    assert.equal(current.observationState, "missing");
    assert.equal(current.observation.value, null);
    assert.equal(current.observation.timeUtc, latest.timeUtc);
  }
  assert.equal(cards({ series: [series({ points: [older, { ...latest, value: Infinity }] })] })[0].observationState, "invalid");
  assert.equal(cards({ series: [series({ points: [older, latest, { ...latest, value: 99 }] })] })[0].observationState, "ambiguous");
  assert.equal(cards({ series: [series({ points: [{ ...latest, value: 0 }] })] })[0].observation.value, 0);
});

test("target, unit and dimension identities are exact; duplicate matching series and overlapping references are ambiguous", () => {
  for (const mismatch of [{ targetId: "22222222-2222-4222-8222-222222222222" }, { unit: "%" }, { dimension: "server" }]) {
    assert.equal(cards({ series: [series(mismatch)] })[0].observationState, "missing");
    assert.equal(cards({ bands: [band(mismatch)] })[0].referenceState, "missing");
  }
  assert.equal(cards({ series: [series(), series()] })[0].observationState, "ambiguous");
  assert.equal(cards({ bands: [band(), band()] })[0].referenceState, "ambiguous");
  assert.equal(cards({ bands: [band({ fromUtc, toUtc: "2026-09-26T09:00:00Z" })] })[0].referenceState, "missing");
  assert.equal(cards({ bands: [band({ fromUtc })] })[0].referenceState, "invalid");
});

test("bucket and reference bounds preserve microseconds, normalize seventh trailing zero and exclude the window end", () => {
  const preciseFrom = "2026-09-26T08:00:00.000001Z", preciseTo = "2026-09-26T09:00:00.000002Z";
  const input = { fromUtc: preciseFrom, toUtc: preciseTo,
    series: [series({ points: [
      { timeUtc: "2026-09-26T08:00:00.000000Z", value: 99, samples: 1 },
      { timeUtc: preciseFrom, value: 2, samples: 1 },
      { timeUtc: "2026-09-26T09:00:00.0000010Z", value: 3, samples: 1 },
      { timeUtc: preciseTo, value: 99, samples: 1 },
    ] })], bands: [band({ toUtc: "2026-09-26T09:00:00.0000020Z" })] };
  const current = cards(input)[0];
  assert.equal(current.observation.value, 3);
  assert.equal(current.referenceState, "available");
  assert.equal(cards({ ...input, bands: [band({ toUtc: "2026-09-26T09:00:00.0000010Z" })] })[0].referenceState, "missing");
  assert.equal(cards({ ...input, fromUtc: preciseTo })[0].observationState, "invalid");
  assert.equal(cards({ series: [series({ points: [{ timeUtc: "2026-09-26T09:30:00.0000001Z", value: 9, samples: 1 }] })] })[0].observationState, "invalid");
});

test("reference quality comes only from structured metadata; partial, unavailable and unknown sources remain explicit", () => {
  const partial = cards({ series: [series({ state: "partial" })], bands: [band({ visibilityState: "partial", coverage: .75 })] })[0];
  assert.equal(partial.observationState, "partial");
  assert.equal(partial.referenceQuality, "partial");
  const unknown = cards({ bands: [band({ visibilityState: undefined, coverage: undefined, confidence: undefined,
    sourceFromUtc: undefined, sourceToUtc: undefined, label: "complete coverage 100% (untrusted label wording)" })] })[0];
  assert.equal(unknown.referenceQuality, "unknown");
  assert.equal(unknown.reference.label, "complete coverage 100% (untrusted label wording)");
  assert.equal(cards({ bands: [band({ visibilityState: "unavailable" })] })[0].reference, undefined);
  for (const extra of [{ coverage: 2 }, { confidence: NaN }, { low: 15 }, { sourceToUtc: "2026-09-26T08:00:00.000001Z" }])
    assert.equal(cards({ bands: [band(extra)] })[0].referenceState, "invalid");
});
