import assert from "node:assert/strict";
import test from "node:test";
import { buildDeadlockGraph } from "../src/features/deadlocks/deadlockGraph.ts";

test("deadlock graph preserves waiter-to-blocker direction and every participant", () => {
  const detail = {
    summary: { eventId: "e", occurredAtUtc: "2026-09-09T00:00:00Z", fingerprint: "f", participantCount: 3, relationCount: 2, parseTruncated: false, collectedAtUtc: "2026-09-09T00:01:00Z" },
    participants: [{ sessionId: 82, isVictim: true }, { sessionId: 91, isVictim: false }, { sessionId: 104, isVictim: false }],
    relations: [
      { waiterSessionId: 82, blockerSessionId: 91, resourceCategory: "key", lockMode: "X" },
      { waiterSessionId: 104, blockerSessionId: 82, resourceCategory: "page", lockMode: "S" },
    ],
  };
  const graph = buildDeadlockGraph(detail);
  assert.deepEqual(graph.nodes.map((node) => node.sessionId), [82, 91, 104]);
  assert.equal(graph.nodes.find((node) => node.sessionId === 82)?.isVictim, true);
  assert.deepEqual(graph.edges.map((edge) => [edge.waiterSessionId, edge.blockerSessionId]), [[82, 91], [104, 82]]);
});

test("deadlock graph adds relationship endpoints not present in participant summaries", () => {
  const graph = buildDeadlockGraph({
    summary: { eventId: "e", occurredAtUtc: "2026-09-09T00:00:00Z", fingerprint: "f", participantCount: 1, relationCount: 1, parseTruncated: true, collectedAtUtc: "2026-09-09T00:01:00Z" },
    participants: [{ sessionId: 1, isVictim: false }],
    relations: [{ waiterSessionId: 1, blockerSessionId: 2, resourceCategory: "other", lockMode: "OTHER" }],
  });
  assert.deepEqual(graph.nodes.map((node) => node.sessionId), [1, 2]);
  assert.equal(graph.nodes.find((node) => node.sessionId === 2)?.isVictim, false);
});

function onNodeBoundary(point, node) {
  const dx = Math.abs(point.x - node.x);
  const dy = Math.abs(point.y - node.y);
  const onVerticalEdge = Math.abs(dx - 78) < 1e-9 && dy <= 42 + 1e-9;
  const onHorizontalEdge = Math.abs(dy - 42) < 1e-9 && dx <= 78 + 1e-9;
  return onVerticalEdge || onHorizontalEdge;
}

test("deadlock connectors terminate on node boundaries and separate reciprocal or parallel relationships", () => {
  const graph = buildDeadlockGraph({
    summary: { eventId: "e", occurredAtUtc: "2026-09-09T00:00:00Z", fingerprint: "f", participantCount: 2, relationCount: 3, parseTruncated: false, collectedAtUtc: "2026-09-09T00:01:00Z" },
    participants: [{ sessionId: 10, isVictim: false }, { sessionId: 20, isVictim: true }],
    relations: [
      { waiterSessionId: 10, blockerSessionId: 20, resourceCategory: "key", lockMode: "X" },
      { waiterSessionId: 20, blockerSessionId: 10, resourceCategory: "page", lockMode: "S" },
      { waiterSessionId: 10, blockerSessionId: 20, resourceCategory: "object_lock", lockMode: "IX" },
    ],
  });
  const nodes = new Map(graph.nodes.map((node) => [node.sessionId, node]));

  for (const edge of graph.edges) {
    assert.equal(onNodeBoundary(edge.start, nodes.get(edge.waiterSessionId)), true);
    assert.equal(onNodeBoundary(edge.end, nodes.get(edge.blockerSessionId)), true);
    assert.ok(edge.path.startsWith("M "));
  }
  assert.equal(new Set(graph.edges.map((edge) => edge.path)).size, 3);
  assert.equal(new Set(graph.edges.map((edge) => `${edge.start.x}:${edge.start.y}|${edge.end.x}:${edge.end.y}`)).size, 3);
  assert.equal(new Set(graph.edges.map((edge) => `${edge.label.x}:${edge.label.y}`)).size, 3);
  assert.ok(graph.nodes.every((node) => node.x - 78 >= 0 && node.x + 78 <= graph.width && node.y - 42 >= 0 && node.y + 42 <= graph.height));
});

test("diagonal reciprocal and parallel connectors stay on their rectangle edges", () => {
  const graph = buildDeadlockGraph({
    summary: { eventId: "e", occurredAtUtc: "2026-09-09T00:00:00Z", fingerprint: "f", participantCount: 4, relationCount: 4, parseTruncated: false, collectedAtUtc: "2026-09-09T00:01:00Z" },
    participants: [{ sessionId: 1, isVictim: false }, { sessionId: 2, isVictim: false }, { sessionId: 3, isVictim: false }, { sessionId: 4, isVictim: false }],
    relations: [
      { waiterSessionId: 1, blockerSessionId: 4, resourceCategory: "key", lockMode: "X" },
      { waiterSessionId: 4, blockerSessionId: 1, resourceCategory: "page", lockMode: "S" },
      { waiterSessionId: 1, blockerSessionId: 4, resourceCategory: "object_lock", lockMode: "IX" },
      { waiterSessionId: 4, blockerSessionId: 1, resourceCategory: "metadata", lockMode: "U" },
    ],
  });
  const nodes = new Map(graph.nodes.map((node) => [node.sessionId, node]));
  for (const edge of graph.edges) {
    assert.equal(onNodeBoundary(edge.start, nodes.get(edge.waiterSessionId)), true);
    assert.equal(onNodeBoundary(edge.end, nodes.get(edge.blockerSessionId)), true);
  }
  assert.equal(new Set(graph.edges.map((edge) => `${edge.start.x}:${edge.start.y}|${edge.end.x}:${edge.end.y}`)).size, 4);
});

test("self relationships use distinct bounded loop paths and boundary endpoints", () => {
  const graph = buildDeadlockGraph({
    summary: { eventId: "e", occurredAtUtc: "2026-09-09T00:00:00Z", fingerprint: "f", participantCount: 1, relationCount: 2, parseTruncated: false, collectedAtUtc: "2026-09-09T00:01:00Z" },
    participants: [{ sessionId: 7, isVictim: false }],
    relations: [
      { waiterSessionId: 7, blockerSessionId: 7, resourceCategory: "key", lockMode: "S" },
      { waiterSessionId: 7, blockerSessionId: 7, resourceCategory: "page", lockMode: "X" },
    ],
  });
  const node = graph.nodes[0];
  assert.equal(graph.edges.every((edge) => onNodeBoundary(edge.start, node) && onNodeBoundary(edge.end, node)), true);
  assert.equal(new Set(graph.edges.map((edge) => edge.path)).size, 2);
  assert.equal(new Set(graph.edges.map((edge) => `${edge.label.x}:${edge.label.y}`)).size, 2);
});
