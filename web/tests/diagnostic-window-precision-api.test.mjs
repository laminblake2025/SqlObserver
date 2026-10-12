import test from 'node:test';
import assert from 'node:assert/strict';
import { clearApiCache } from '../src/apiClient.ts';
import { getQueryPerformanceTop, getQueryPerformanceStatus, getQueryPerformanceHistory, getQueryStoreTopWaits,
  getQueryStorePlanRegressions, getPlanCacheIntervalRanking, getPlanCacheIntervalHistory } from '../src/features/queries/queryPerformanceApi.ts';
import { getBlockingHistoryPage, getServerWaitHistoryPage } from '../src/features/activity/activityApi.ts';

const targetId = '11111111-1111-4111-8111-111111111111';
const query = 'a'.repeat(64), plan = 'b'.repeat(64);
const selection = { targetId, databaseId: 6, databaseIncarnation: 'c'.repeat(64), queryFingerprint: query, planFingerprint: plan };
const signal = () => new AbortController().signal;
const from = '2026-09-26T12:00:00.000001Z', to = '2026-09-26T12:00:00.000002Z';
const readers = {
  top: (fromUtc, toUtc) => getQueryPerformanceTop(targetId, 'cpu', undefined, fromUtc, toUtc, signal()),
  status: (fromUtc, toUtc) => getQueryPerformanceStatus(targetId, fromUtc, toUtc, signal()),
  history: (fromUtc, toUtc) => getQueryPerformanceHistory(targetId, 6, query, fromUtc, toUtc, signal()),
  waits: (fromUtc, toUtc) => getQueryStoreTopWaits(targetId, fromUtc, toUtc, signal()),
  regressions: (fromUtc, toUtc) => getQueryStorePlanRegressions(targetId, fromUtc, toUtc, signal()),
  cacheRanking: (fromUtc, toUtc) => getPlanCacheIntervalRanking(targetId, fromUtc, toUtc, 'cpu', signal()),
  cacheHistory: (fromUtc, toUtc) => getPlanCacheIntervalHistory(selection, fromUtc, toUtc, signal()),
  blocking: (fromUtc, toUtc) => getBlockingHistoryPage(targetId, { fromUtc, toUtc }, signal()),
  waitHistory: (fromUtc, toUtc) => getServerWaitHistoryPage(targetId, { fromUtc, toUtc }, signal()),
};
function nativeResponse(name, fromUtc = from, toUtc = to, comparisonFromUtc = '2026-09-26T12:00:00Z') {
  const base = { targetId, fromUtc, toUtc, items: [] };
  if (name === 'top') return { ...base, metric: 'cpu', snapshotUtc: toUtc, nextCursor: null };
  if (name === 'history') return { ...base, databaseId: 6, queryFingerprint: query, snapshotUtc: toUtc, nextCursor: null };
  if (name === 'status') return { targetId, snapshotUtc: toUtc, source: 'query_store', sourceState: 'read_write',
    coverage: 'no_activity', fresh: true, truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: [] };
  if (name === 'waits') return { ...base, category: null, semantics: 'query_store_group_wait_delta', coverage: 'no_comparable_groups' };
  if (name === 'regressions') return { ...base, comparisonFromUtc, semantics: 'query_store_group_plan_comparison', coverage: 'no_qualifying_changes' };
  if (name === 'cacheRanking') return { ...base, metric: 'cpu', semantics: 'plan_cache_delta', coverage: 'no_comparable_rows',
    sourceFrames: 0, incompleteFrames: 0, comparableRows: 0, unavailableRows: 0, boundaryRows: 0, hasMore: false };
  if (name === 'cacheHistory') return { ...base, ...selection, semantics: 'plan_cache_delta', coverage: 'observed_entries_only',
    snapshotUtc: toUtc, revision: 1, totalRows: 0, nextCursor: null };
  return { instanceId: targetId, fromUtc, toUtc, repositoryTimeUtc: toUtc, items: [], nextCursor: null };
}
function observation(overrides = {}) {
  return { databaseId: 6, queryFingerprint: query, planFingerprint: plan, source: 'query_store', sourceState: 'read_write',
    semantics: 'query_store_interval', metric: 'cpu', value: 0, intervalStartUtc: from, intervalEndUtc: to,
    coverage: 'complete', fresh: true, truncated: false, contentAvailable: false,
    collectionRunId: '22222222-2222-4222-8222-222222222222', observationKey: 'd'.repeat(32), ...overrides };
}

test('all owning diagnostic transports accept positive microsecond windows and native seventh fractional zero', async () => {
  const original = globalThis.fetch; clearApiCache();
  try {
    for (const [name, read] of Object.entries(readers)) {
      const requested = [];
      globalThis.fetch = async url => { requested.push(String(url)); return Response.json(nativeResponse(name)); };
      await read(from, to);
      await read(from.replace('Z', '0Z'), to.replace('Z', '0Z'));
      assert.equal(requested.length, 2, name);
      const params = new URL(requested[1], 'https://local.invalid').searchParams;
      assert.equal(params.get('fromUtc'), from.replace('Z', '0Z'), name);
      assert.equal(params.get('toUtc'), to.replace('Z', '0Z'), name);
    }
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('query and activity caps reject a one-microsecond overflow before fetching and accept their exact maximum', async () => {
  const original = globalThis.fetch; clearApiCache();
  try {
    for (const [name, read] of Object.entries(readers)) {
      const activity = name === 'blocking' || name === 'waitHistory';
      const end = activity ? '2026-09-27T12:00:00.000001Z' : '2026-10-03T12:00:00.000001Z';
      const comparison = '2026-09-19T12:00:00.000001Z';
      let calls = 0;
      globalThis.fetch = async () => { calls++; return Response.json(nativeResponse(name, from, end, comparison)); };
      await read(from, end);
      for (const invalidEnd of [end.replace('.000001Z', '.000002Z'), from, '2026-09-26T12:00:00Z',
        to.replace('.000002Z', '.0000021Z'), '2026-02-30T12:00:00Z', '2026-09-26T12:00:00.000002+01:00'])
        await assert.rejects(() => read(from, invalidEnd), /invalid|bounds/i, `${name}: ${invalidEnd}`);
      assert.equal(calls, 1, `${name} rejects malformed, reversed, equal and overlong UTC windows before transport`);
    }
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('top and history accept microsecond intervals but reject rows escaping their exact window', async () => {
  const original = globalThis.fetch; clearApiCache();
  try {
    for (const name of ['top', 'history']) {
      let row = observation();
      if (name === 'history') row = { ...row, cpuMilliseconds: 0 };
      globalThis.fetch = async () => Response.json({ ...nativeResponse(name), items: [row] });
      assert.equal((await readers[name](from, to)).items.length, 1, name);
      row = { ...row, intervalStartUtc: '2026-09-26T12:00:00Z' };
      await assert.rejects(() => readers[name](from, to), /bound window/, name);
      row = { ...row, intervalStartUtc: from, intervalEndUtc: '2026-09-26T12:00:00.000003Z' };
      await assert.rejects(() => readers[name](from, to), /bound window/, name);
    }
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('wait and regression observation bounds and previous comparison start remain exact within a millisecond', async () => {
  const original = globalThis.fetch; clearApiCache();
  try {
    let observed = from;
    globalThis.fetch = async () => Response.json({ ...nativeResponse('waits'), coverage: 'observed_groups_only', items: [{
      databaseId: 6, queryFingerprint: query, planFingerprint: plan, waitMilliseconds: '1', comparableRows: 1,
      sourceRows: 1, lastObservedAtUtc: observed }] });
    assert.equal((await readers.waits(from, to)).items.length, 1);
    observed = to; await assert.rejects(() => readers.waits(from, to), /row was invalid/);
    observed = '2026-09-26T12:00:00Z'; await assert.rejects(() => readers.waits(from, to), /row was invalid/);
    globalThis.fetch = async () => Response.json(nativeResponse('regressions', from, to, from));
    await assert.rejects(() => readers.regressions(from, to), /outside its bounds/,
      'A one-microsecond comparison-start mismatch must not round into the expected instant.');
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('activity history responses must preserve both exact requested bounds, including native UTC equivalent spellings', async () => {
  const original = globalThis.fetch; clearApiCache();
  try {
    for (const name of ['blocking', 'waitHistory']) {
      let response = nativeResponse(name, from.replace('Z', '0+00:00'), to.replace('Z', '0+00:00'));
      globalThis.fetch = async () => Response.json(response);
      assert.equal((await readers[name](from, to)).items.length, 0, name);
      for (const overrides of [{ fromUtc: undefined }, { toUtc: undefined },
        { fromUtc: '2026-09-26T12:00:00Z' }, { toUtc: '2026-09-26T12:00:00.000003Z' },
        { fromUtc: from.replace('Z', '+01:00') }]) {
        response = { ...nativeResponse(name), ...overrides };
        await assert.rejects(() => readers[name](from, to), /invalid response|match the selected UTC window/, name);
      }
    }
  } finally { globalThis.fetch = original; clearApiCache(); }
});
