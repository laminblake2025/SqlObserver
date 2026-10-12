import assert from 'node:assert/strict';
import test from 'node:test';
import { ApiRequestError, clearApiCache } from '../src/apiClient.ts';
import { getBufferNodePleWindow, parseBufferNodePleWindow } from '../src/features/resources/bufferNodePleApi.ts';
import { getDatabaseFileIoTrend } from '../src/features/resources/databaseFileIoTrendApi.ts';
import { alpha, firstWindow, nativePle } from '../component-tests/diagnosticWorkflowFixture.mjs';

for (const status of [401, 403]) for (const kind of ['ple', 'file']) test(`${kind} HTTP ${status} exposes typed status without protected provider text`, async () => {
  const original = globalThis.fetch; clearApiCache();
  globalThis.fetch = async () => new Response(JSON.stringify({ detail: 'SECRET_PROVIDER_DENIAL' }), { status, headers: { 'content-type': 'application/json', 'cache-control': 'no-store' } });
  try {
    const controller = new AbortController();
    await assert.rejects(kind === 'ple' ? getBufferNodePleWindow(alpha, firstWindow.fromUtc, firstWindow.toUtc, 1, controller.signal)
      : getDatabaseFileIoTrend(alpha, 7, 1, firstWindow.fromUtc, firstWindow.toUtc, controller.signal),
    error => error instanceof ApiRequestError && error.status === status && !error.message.includes('SECRET_PROVIDER_DENIAL'));
  } finally { globalThis.fetch = original; clearApiCache(); }
});

test('a valid native missing requested node stays exact rather than replacing it with the first observed node', () => {
  const source = nativePle({ target: alpha, ...firstWindow, nodeId: 9 });
  const result = parseBufferNodePleWindow(source, alpha, firstWindow.fromUtc, firstWindow.toUtc, 9);
  assert.equal(result.selectedNodeId, 9); assert.deepEqual(result.points, []); assert.deepEqual(result.nodes.map(node => node.nodeId), [0, 1]);
  assert.throws(() => parseBufferNodePleWindow({ ...source, selectedNodeId: 0 }, alpha, firstWindow.fromUtc, firstWindow.toUtc, 9));
});
