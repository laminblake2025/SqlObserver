import { test } from 'node:test';
import assert from 'node:assert/strict';
import { headBlockerFindings } from '../src/features/health/headBlockerModel.ts';
const edge = (session, root, wait, state = 'resolved') => ({ blockedSessionId: session, rootBlockerSessionId: root,
  blockerSessionId: root, blockerKind: 'session', waitType: 'LCK_M_X', waitingTaskCount: '1',
  waitDurationMilliseconds: wait, chainDepth: 1, chainState: state, observedAtUtc: '2026-01-15T13:00:00.123456Z' });
const page = items => ({ repositoryTimeUtc: '2026-01-15T13:01:00Z', items,
  evidence: { runId: '33333333-3333-4333-8333-000000000001', targetRevision: '7', collectorId: 'blocking.current',
    freshness: 'current', outcome: 'succeeded', isPartial: false, reason: 'completed', completedAtUtc: '2026-01-15T13:00:01Z' } });
test('ranks distinct blocked sessions rather than waiting-task edges, then exact integer wait', () => {
  const value = headBlockerFindings(page([edge(61, 50, '1'), edge(61, 50, '9'), edge(62, 50, '2'), edge(70, 55, '999999999999999999999999')]), 7);
  assert.deepEqual(value.items, [{ sessionId: 50, blockedSessions: 2, maximumWaitMilliseconds: '9' },
    { sessionId: 55, blockedSessions: 1, maximumWaitMilliseconds: '999999999999999999999999' }]);
  assert.equal(value.complete, true);
});
test('cycle, depth-limit, external and self-blocking rows cannot become confirmed head blockers', () => {
  const value = headBlockerFindings(page([edge(61, 50, '1', 'cycle'), edge(62, 50, '1', 'depth_limit'),
    edge(63, 50, '1', 'external_blocker'), edge(50, 50, '1')]), 7);
  assert.equal(value.items.length, 0); assert.equal(value.unresolvedEdges, 4); assert.equal(value.complete, false);
});
test('a prior revision cannot produce a current confirmed root', () => {
  const value = headBlockerFindings(page([edge(61, 50, '1')]), 8);
  assert.equal(value.items.length, 0); assert.equal(value.comparable, false);
});
test('cursor, loss, stale and partial responses never claim complete counts or an all-clear', () => {
  for (const mutation of [p => p.nextCursor = 'next', p => p.evidence.loss = { kind: 'bounded', minimumLostItems: 1 },
    p => p.evidence.freshness = 'stale', p => p.evidence.isPartial = true, p => p.evidence.reason = 'blocking_graph_limit']) {
    const p = page([]); mutation(p); assert.equal(headBlockerFindings(p, 7).complete, false);
  }
});
test('invalid calendar observations safely refuse graph promotion', () => {
  const p = page([edge(61, 50, '1')]); p.items[0].observedAtUtc = '2026-02-30T13:00:00Z';
  assert.equal(headBlockerFindings(p, 7).comparable, false);
});
