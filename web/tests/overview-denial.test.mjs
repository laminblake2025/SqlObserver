import assert from 'node:assert/strict';
import test from 'node:test';
import { getOverview, OverviewRequestError } from '../src/features/overview/overviewApi.ts';
import { clearApiCache } from '../src/apiClient.ts';

const target = '11111111-1111-4111-8111-000000000001';
const window = { fromUtc: '2026-09-26T08:00:00.123456Z', toUtc: '2026-09-26T12:00:00.654321Z' };

for (const [status, message] of [[401, 'Your access could not be verified. Refresh to retry.'],
  [403, 'This server selection is unavailable or outside your access.'],
  [503, 'Overview evidence is temporarily unavailable.']])
  test(`Overview preserves native HTTP${status} metadata with fixed safe copy`, async () => {
    const previous = globalThis.fetch; clearApiCache();
    globalThis.fetch = async () => new Response('PRIVATE_PROVIDER_TOKEN<script>unsafe</script>', { status });
    try {
      await assert.rejects(getOverview(target, window, new AbortController().signal), failure =>
        failure instanceof OverviewRequestError && failure.status === status && failure.message === message);
    } finally { globalThis.fetch = previous; clearApiCache(); }
  });

test('a cancelled late Overview401 preserves the original cancellation instead of a denial', async () => {
  const previous = globalThis.fetch; clearApiCache();
  const controller = new AbortController(), reason = new Error('Retired scope');
  globalThis.fetch = async () => { controller.abort(reason); return new Response('PRIVATE_OLD_DENIAL', { status: 401 }); };
  try { await assert.rejects(getOverview(target, window, controller.signal), failure => failure === reason); }
  finally { globalThis.fetch = previous; clearApiCache(); }
});
