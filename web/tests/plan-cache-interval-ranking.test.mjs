import assert from 'node:assert/strict';
import test from 'node:test';
import { getPlanCacheIntervalRanking } from '../src/features/queries/queryPerformanceApi.ts';

const targetId = '11111111-1111-4111-8111-111111111111';
const fromUtc = '2026-09-28T00:00:00Z', toUtc = '2026-09-29T00:00:00Z';
const row = { databaseId: 6, databaseIncarnation: 'a'.repeat(64), queryFingerprint: 'b'.repeat(64), planFingerprint: 'c'.repeat(64),
  cpuMilliseconds: '9007199254740993.00100000000000000000', durationMilliseconds: '0.00000000000000000000',
  executions: '9007199254740993', logicalReads: '0', writes: '0', rows: '0', comparableRows: 1,
  firstObservedAtUtc: '2026-09-28T11:00:00Z', lastObservedAtUtc: '2026-09-28T12:00:00Z' };
const base = { targetId, fromUtc, toUtc, metric: 'cpu', semantics: 'plan_cache_delta', coverage: 'observed_entries_only',
  sourceFrames: 2, incompleteFrames: 1, comparableRows: 1, unavailableRows: 3, boundaryRows: 1, hasMore: false, items: [row] };
async function read(body, metric = 'cpu') {
  const previous = globalThis.fetch;
  try {
    globalThis.fetch = async url => {
      assert.match(url, /query-performance\/cache\/top\?/);
      assert.equal(new URL(url, 'http://fixture').searchParams.get('metric'), metric);
      return new Response(JSON.stringify(body), { headers: { 'content-type': 'application/json' } });
    };
    return await getPlanCacheIntervalRanking(targetId, fromUtc, toUtc, metric, new AbortController().signal);
  } finally { globalThis.fetch = previous; }
}

test('interval ranking preserves fractional milliseconds and counts above JavaScript precision', async () => {
  const result = await read(base);
  assert.equal(result.items[0].cpuMilliseconds, row.cpuMilliseconds);
  assert.equal(result.items[0].executions, row.executions);
  assert.equal(result.incompleteFrames, 1);
  assert.equal(result.boundaryRows, 1);
});
test('interval ranking compares exact values rather than rounded JavaScript numbers', async () => {
  const other = { ...row, planFingerprint: 'd'.repeat(64), cpuMilliseconds: '9007199254740993.002' };
  await assert.rejects(read({ ...base, comparableRows: 2, items: [row, other] }));
  assert.equal((await read({ ...base, comparableRows: 2, items: [other, row] })).items.length, 2);
});
test('identity, metric, window and coverage drift are rejected', async () => {
  for (const changed of [{ targetId: '22222222-2222-4222-8222-222222222222' }, { metric: 'duration' },
    { semantics: 'plan_cache_cumulative' }, { coverage: 'no_comparable_rows' }, { sourceFrames: 0 },
    { fromUtc: '2026-09-28T00:00:00.0000001Z' }, { hasMore: true },
    { items: [row, row] }, { items: [{ ...row, executions: '0.1' }] },
    { items: [{ ...row, firstObservedAtUtc: '2026-09-27T23:59:59Z' }] }])
    await assert.rejects(read({ ...base, ...changed }));
});
test('an empty window retains unavailable and boundary comparisons instead of inventing zeroes', async () => {
  const result = await read({ ...base, comparableRows: 0, coverage: 'no_comparable_rows', items: [] });
  assert.deepEqual(result.items, []);
  assert.equal(result.unavailableRows, 3); assert.equal(result.boundaryRows, 1);
});
test('sub-microsecond window bounds are rejected before a request can round them', async () => {
  const previous = globalThis.fetch;
  try {
    globalThis.fetch = async () => { throw new Error('Unexpected network request'); };
    await assert.rejects(getPlanCacheIntervalRanking(targetId, '2026-09-28T00:00:00.0000001Z', toUtc,
      'cpu', new AbortController().signal), /window or metric was invalid/);
  } finally { globalThis.fetch = previous; }
});
