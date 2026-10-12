import assert from "node:assert/strict";
import test from "node:test";
import { parseBufferNodePleWindow } from "../src/features/resources/bufferNodePleApi.ts";
import { bufferManagerPleSeries, bufferNodePleSeries } from "../src/features/resources/bufferNodePleModel.ts";

const targetId = "dd760e6d-8ab4-4535-aee1-b7cef3587338";
const fromUtc = "2026-09-28T10:00:00.000Z";
const toUtc = "2026-09-28T12:00:00.000Z";
const payload = {
  targetId, fromUtc, toUtc,
  semantics: "numa_node_page_life_expectancy_gauge",
  unit: "seconds", revisionScope: "current", bucketSeconds: 3600,
  selectedNodeId: 0, visibilityGapCount: 1,
  nodes: [{ nodeId: 0, sampleCount: 2, lastObservedAtUtc: "2026-09-28T10:30:00.000Z" }],
  points: [{ bucketStartUtc: fromUtc, sampleCount: 2,
    minimumSeconds: 80, averageSeconds: 90, maximumSeconds: 100 }],
  aggregatePoints: [{ bucketStartUtc: fromUtc, sampleCount: 1,
    minimumSeconds: 200, averageSeconds: 200, maximumSeconds: 200 }],
};

test("node and aggregate PLE remain separate gauges with empty hours visible", () => {
  const parsed = parseBufferNodePleWindow(payload, targetId, fromUtc, toUtc, null);
  const nodes = bufferNodePleSeries(parsed);
  const aggregate = bufferManagerPleSeries(parsed);
  assert.equal(nodes.length, 3);
  assert.equal(aggregate.length, 3);
  assert.equal(nodes[1].metric, "engine.buffer_node_page_life_expectancy_seconds");
  assert.equal(aggregate[1].metric, "engine.page_life_expectancy_seconds");
  assert.equal(nodes[1].points[0].value, 90);
  assert.equal(aggregate[1].points[0].value, 200);
  assert.equal(nodes[1].points[1].value, null);
  assert.equal(nodes[1].points[1].samples, 0);
});

test("buffer node parser rejects cross-target and misleading gauge evidence", () => {
  assert.throws(() => parseBufferNodePleWindow(payload,
    "07e8e844-1376-4558-9a39-3b1891f2b863", fromUtc, toUtc, null));
  assert.throws(() => parseBufferNodePleWindow({ ...payload,
    points: [{ ...payload.points[0], minimumSeconds: 101 }] }, targetId, fromUtc, toUtc, null));
  assert.throws(() => parseBufferNodePleWindow({ ...payload,
    nodes: [...payload.nodes, payload.nodes[0]] }, targetId, fromUtc, toUtc, null));
  assert.throws(() => parseBufferNodePleWindow({ ...payload, selectedNodeId: 1 },
    targetId, fromUtc, toUtc, null));
  assert.throws(() => parseBufferNodePleWindow({ ...payload, selectedNodeId: null },
    targetId, fromUtc, toUtc, null));
});
