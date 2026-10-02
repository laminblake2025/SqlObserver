import test from 'node:test';
import assert from 'node:assert/strict';
import { getQueryStoreGroupPlanTrend } from '../src/features/queries/queryPerformanceApi.ts';
import { clearApiCache } from '../src/apiClient.ts';

const targetId = '11111111-1111-4111-8111-111111111111';
const query = 'a'.repeat(64), plan = 'b'.repeat(64);
const point = bucketStartUtc => ({ bucketStartUtc, sourceRows: 1,
  runtimeComparableRows: 1, runtimeBaselineRows: 0, runtimeResetRows: 0, runtimeAmbiguousRows: 0,
  cpuMilliseconds: '200', durationMilliseconds: '300', executions: '2', logicalReads: '12', writes: '1', rows: '4',
  waitComparableRows: 1, waitBaselineRows: 0, waitResetRows: 0, waitUnavailableRows: 0, waitStaleRows: 0, waitCategories: { '3': '9' } });

test('Query Store trend accepts a one-microsecond window and a bucket one microsecond before its end', async () => {
  const original = globalThis.fetch;
  clearApiCache();
  try {
    const fromUtc = '2026-09-26T12:00:00Z', toUtc = '2026-09-26T12:00:00.000001Z';
    let bucket = fromUtc;
    globalThis.fetch = async () => Response.json({ targetId, databaseId: 5, queryFingerprint: query, planFingerprint: plan,
      fromUtc, toUtc, semantics: 'query_store_group_delta', revisionScope: 'current', coverage: 'observed_groups_only',
      bucketSeconds: 3600, points: [point(bucket)] });
    const read = () => getQueryStoreGroupPlanTrend(targetId, 5, query, plan, fromUtc, toUtc, new AbortController().signal);
    assert.equal((await read()).points.length, 1);
    bucket = toUtc;
    await assert.rejects(read, /bucket was outside its window/);
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('Query Store trend rejects a fractional hourly bucket and an exact seven-day overflow', async () => {
  const original = globalThis.fetch;
  clearApiCache();
  try {
    const fromUtc = '2026-09-26T12:00:00Z', toUtc = '2026-09-26T14:00:00Z';
    let calls = 0;
    globalThis.fetch = async () => { calls++; return Response.json({ targetId, databaseId: 5, queryFingerprint: query, planFingerprint: plan,
      fromUtc, toUtc, semantics: 'query_store_group_delta', revisionScope: 'current', coverage: 'observed_groups_only',
      bucketSeconds: 3600, points: [point('2026-09-26T13:00:00.000001Z')] }); };
    const read = end => getQueryStoreGroupPlanTrend(targetId, 5, query, plan, fromUtc, end, new AbortController().signal);
    await assert.rejects(() => read(toUtc), /bucket was outside its window/);
    await assert.rejects(() => read('2026-10-03T12:00:00.000001Z'), /request was invalid/);
    assert.equal(calls, 1, 'Reject the overlong window before any network read.');
  } finally { globalThis.fetch = original; clearApiCache(); }
});
