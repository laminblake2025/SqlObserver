import test from 'node:test';
import assert from 'node:assert/strict';
import { readLive } from '../src/features/activity/liveActivityApi.ts';
import { clearApiCache } from '../src/apiClient.ts';

test('native inclusive session history becomes a half-open workspace window without discarding valid captures', async () => {
  const previous = globalThis.fetch;
  const from = '2026-10-02T11:30:00.123456Z', to = '2026-10-02T11:30:00.123457Z';
  const atStart = { id: '11111111-1111-4111-8111-111111111111', observedUtc: from, truncated: false };
  const atEnd = { id: '22222222-2222-4222-8222-222222222222', observedUtc: to, truncated: false };
  try {
    globalThis.fetch = async path => {
      const request = new URL(path, 'http://localhost');
      assert.equal(request.searchParams.get('from'), from);
      assert.equal(request.searchParams.get('to'), to);
      return Response.json([atStart, atEnd]);
    };
    const parameters = new URLSearchParams({ from, to });
    const signal = new AbortController().signal;
    assert.deepEqual(await readLive('33333333-3333-4333-8333-333333333333', '/history', parameters, signal), [atStart]);
    for (const observedUtc of ['2026-10-02T11:30:00.123455Z', '2026-10-02T11:30:00.123458Z']) {
      globalThis.fetch = async () => Response.json([{ ...atStart, observedUtc }]);
      await assert.rejects(readLive('33333333-3333-4333-8333-333333333333', '/history', parameters, signal), /Invalid session history capture/);
    }
    globalThis.fetch = async () => Response.json([atEnd, atEnd]);
    await assert.rejects(readLive('33333333-3333-4333-8333-333333333333', '/history', parameters, signal), /Invalid session history capture/);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});
