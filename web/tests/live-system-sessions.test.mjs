import test from 'node:test';
import assert from 'node:assert/strict';
import { readLive } from '../src/features/activity/liveActivityApi.ts';
import { clearApiCache } from '../src/apiClient.ts';
import { nativeLive, alpha } from '../component-tests/waitsAccessFixture.mjs';
import { loadActivitySelection, validateActivitySelectionPage } from '../src/features/activity/activitySelectionModel.ts';

test('system requests without database context are accepted while malformed database metadata stays rejected', async () => {
  const previous = globalThis.fetch;
  const parameters = new URLSearchParams({ includeSystem: 'true', includeIdle: 'true' });
  const page = nativeLive(new URL('http://localhost/activity/live'));
  const systemRow = { ...page.rows[0], isUser: false, databaseId: 0, databaseName: null, status: 'background', command: 'LAZY WRITER' };
  const read = () => readLive(alpha, '', parameters, new AbortController().signal);
  try {
    globalThis.fetch = async () => Response.json({ ...page, rows: [systemRow] });
    assert.deepEqual((await read()).rows, [systemRow]);
    for (const fields of [{ isUser: true }, { isUser: undefined }, { databaseId: -1 }, { databaseId: 32768 }, { databaseId: 0.5 }]) {
      globalThis.fetch = async () => Response.json({ ...page, rows: [{ ...systemRow, ...fields }] });
      await assert.rejects(read(), /Invalid session request metadata/);
    }
    globalThis.fetch = async () => Response.json({ ...page, rows: [systemRow], databases: [{ id: 0, name: null }] });
    await assert.rejects(read(), /Invalid session database metadata/);
  } finally { globalThis.fetch = previous; clearApiCache(); }
});

test('capture restoration accepts system database zero while keeping user, bounds and identity checks', async () => {
  const page = nativeLive(new URL('http://localhost/activity/live'));
  const selection = { snapshotId: page.snapshotId, observedUtc: page.observedUtc, identity: page.rows[0].identity };
  const system = { ...page.rows[0], identity: 'e'.repeat(64), isUser: false, databaseId: 0, databaseName: null };
  const captured = { ...page, rows: [system, page.rows[0]] };
  const restored = await loadActivitySelection(selection, new URLSearchParams({ includeSystem: 'true' }), async () => captured, new AbortController().signal);
  assert.equal(restored.row.identity, selection.identity);
  for (const fields of [{ isUser: true }, { isUser: undefined }, { databaseId: -1 }, { databaseId: 32768 }, { databaseId: 0.5 }])
    assert.throws(() => validateActivitySelectionPage({ ...page, rows: [{ ...system, ...fields }] }, selection), /invalid or duplicate request identities/);
  assert.throws(() => validateActivitySelectionPage({ ...page, rows: [system, system] }, selection), /invalid or duplicate request identities/);
});
