import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiRequestError, clearApiCache } from '../src/apiClient.ts';
import { loadDatabaseScopeChoices, DatabaseScopeLoadError } from '../src/components/databaseScopeModel.ts';
import { listObservationTargets } from '../src/features/targets/targetApi.ts';
import { getQueryPerformanceStatus, getQueryPerformanceHistory } from '../src/features/queries/queryPerformanceApi.ts';
import { getFleetActiveAlerts } from '../src/features/alerts/alertApi.ts';
import { getDatabaseHealth, HealthRequestError } from '../src/features/health/healthApi.ts';
import { getMyAccess } from '../src/features/targets/meApi.ts';

const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const page = { instanceId: target, repositoryTimeUtc: to, items: [{ databaseId: 5, name: 'Prior authorized Orders' }], nextCursor: 'second' };
for (const status of [401, 403]) test(`inventory HTTP ${status} discards all previously received names and preserves the native status`, async () => {
  let calls = 0;
  await assert.rejects(loadDatabaseScopeChoices(target, new AbortController().signal, async () => {
    if (++calls === 1) return page;
    throw new HealthRequestError('Safe native access failure', status);
  }), error => {
    assert.ok(error instanceof DatabaseScopeLoadError);
    assert.equal(error.status, status); assert.deepEqual(error.choices.items, []);
    assert.equal(error.choices.repositoryTimeUtc, undefined); assert.equal(error.choices.complete, false);
    assert.equal(error.message, 'Safe native access failure'); return true;
  });
  assert.equal(calls, 2);
});
test('transient native inventory failure retains validated names and status without claiming completeness', async () => {
  let calls = 0;
  await assert.rejects(loadDatabaseScopeChoices(target, new AbortController().signal, async () => {
    if (++calls === 1) return page;
    throw new HealthRequestError('Safe temporary failure', 503);
  }), error => {
    assert.equal(error.status, 503); assert.deepEqual(error.choices.items, page.items);
    assert.equal(error.choices.repositoryTimeUtc, to); assert.equal(error.choices.complete, false); return true;
  });
});
test('native status survives every palette HTTP reader without trusting response body text', async () => {
  const prior = globalThis.fetch;
  const readers = [
    signal => listObservationTargets(signal, undefined, 100),
    signal => getFleetActiveAlerts(signal, 100),
    signal => getQueryPerformanceStatus(target, undefined, undefined, signal),
    signal => getQueryPerformanceHistory(target, 5, 'a'.repeat(64), from, to, signal),
    signal => getDatabaseHealth(target, signal),
    signal => getMyAccess(target, signal),
  ];
  try {
    for (const status of [401, 403, 503]) {
      globalThis.fetch = async () => new Response('SECRET_NATIVE_PROVIDER <script>bad()</script>', { status });
      for (const read of readers) {
        clearApiCache();
        await assert.rejects(read(new AbortController().signal), error => {
          assert.ok(error instanceof ApiRequestError || error instanceof HealthRequestError);
          assert.equal(error.status, status); assert.ok(!error.message.includes('SECRET_NATIVE_PROVIDER')); return true;
        });
      }
    }
  } finally { clearApiCache(); globalThis.fetch = prior; }
});
test('aborted inventory propagates cancellation instead of adopting denial or partial names', async () => {
  const owner = new AbortController(); let calls = 0;
  await assert.rejects(loadDatabaseScopeChoices(target, owner.signal, async () => {
    calls++; owner.abort(new DOMException('Context changed', 'AbortError'));
    throw new HealthRequestError('Denied old context', 403);
  }), error => error.name === 'AbortError' && error.message === 'Context changed');
  assert.equal(calls, 1);
});
