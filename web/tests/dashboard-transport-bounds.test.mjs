import test from 'node:test';
import assert from 'node:assert/strict';
import { clearApiCache } from '../src/apiClient.ts';
import { getOverview } from '../src/features/overview/overviewApi.ts';
import { getPage, parseSession } from '../src/features/activity/activityParser.mjs';
import { readLive } from '../src/features/activity/liveActivityApi.ts';

const target = '11111111-1111-4111-8111-000000000001';
const window = { fromUtc: '2026-09-26T08:00:00Z', toUtc: '2026-09-26T12:00:00Z' };
const readers = [
  ['Overview', 17, signal => getOverview(target, window, signal)],
  ['Activity page', 5, signal => getPage('/api/v1/activity', parseSession, target, signal)],
  ['Live activity', 17, signal => readLive(target, '', new URLSearchParams(), signal)],
];

for (const [label, maximumChunks, read] of readers) {
  test(`${label} stops an oversized unannounced stream at its feature transport bound`, async () => {
    const original = globalThis.fetch;
    let chunks = 0, cancelled = false;
    clearApiCache();
    try {
      globalThis.fetch = async () => new Response(new ReadableStream({
        pull(controller) {
          if (chunks === 32) { controller.close(); return; }
          chunks++;
          controller.enqueue(new Uint8Array(64 * 1024).fill(32));
        },
        cancel() { cancelled = true; },
      }, { highWaterMark: 0 }), { headers: { 'content-type': 'application/json', 'cache-control': 'no-store' } });
      await assert.rejects(() => read(new AbortController().signal));
      assert.equal(chunks, maximumChunks, 'The shared transport must stop before buffering the entire provider response.');
      assert.equal(cancelled, true, 'Cancel the oversized provider stream.');
    } finally {
      globalThis.fetch = original;
      clearApiCache();
    }
  });
}

test('Overview keeps provider failures private while retaining cancellation identity', async () => {
  const original = globalThis.fetch;
  clearApiCache();
  try {
    globalThis.fetch = async () => { throw new Error('Synthetic provider detail must not reach the dashboard.'); };
    await assert.rejects(() => getOverview(target, window, new AbortController().signal),
      { message: 'Overview evidence is temporarily unavailable.' });
    const controller = new AbortController(), reason = new Error('Cancelled by this subscriber.');
    controller.abort(reason);
    await assert.rejects(() => getOverview(target, window, controller.signal), error => error === reason);
  } finally { globalThis.fetch = original; clearApiCache(); }
});
