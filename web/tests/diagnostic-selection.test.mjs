import test from 'node:test';
import assert from 'node:assert/strict';
import { readResourceSelection, resourceSelectionHref, retainResourceSelectionHref, resourceWindowMatches } from '../src/features/resources/resourceSelectionModel.ts';
import { readActivitySelection, activitySelectionHref, retainActivitySelectionHref, loadActivitySelection } from '../src/features/activity/activitySelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const snapshotId = '22222222-2222-4222-8222-000000000001';
const scope = { target, range: 'custom', from: '2026-09-26T08:00:00.123456Z', to: '2026-09-26T12:00:00.654321Z', compare: true };
const observedUtc = '2026-09-26T10:00:00.123456Z';
const selection = { snapshotId, observedUtc, identity: 'a'.repeat(64), chain: 'session:52' };
const row = identity => ({ identity, sessionId: 52, requestId: 1, databaseId: 5 });
const page = (rows, nextCursor = null) => ({ rows, nextCursor, snapshotId, observedUtc, repositoryTimeUtc: observedUtc, databases: [], state: 'available', truncated: false });

test('Resources links restore opaque IDs and exact inspection time while retaining global scope', () => {
  const selected = { file: { databaseId: 5, fileId: 2 }, numaNodeId: 0, inspectionUtc: observedUtc };
  const href = resourceSelectionHref(scope, selected);
  assert.deepEqual(readResourceSelection(href, target), selected);
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(resourceSelectionHref({ ...scope, range: '1h' }, {}), href)), selected);
  const narrowed = retainResourceSelectionHref(resourceSelectionHref({ ...scope, databaseId: 6 }, {}), href);
  assert.equal(readResourceSelection(narrowed).file, undefined);
  assert.equal(resourceWindowMatches(scope.from, scope.to, scope.from.replace('123456', '123457'), scope.to), false);
});

test('Resources rejects ambiguous context and invalid selections without choosing substitute evidence', () => {
  const href = resourceSelectionHref(scope, { file: { databaseId: 5, fileId: 2 }, numaNodeId: 0, inspectionUtc: observedUtc });
  for (const suffix of ['&range=1h', '&databaseId=0', '&compare=1', '&from=2026-09-26T09:00:00Z']) {
    const invalid = readResourceSelection(href + suffix);
    assert.equal(invalid.invalidContext, true);
    assert.equal(invalid.file, undefined);
    assert.equal(invalid.inspectionUtc, undefined);
  }
  assert.equal(readResourceSelection(href.replace('numaNodeId=0', 'numaNodeId=1024')).invalidNode, true);
  assert.equal(readResourceSelection(href.replace('resourceFileId=2', 'resourceFileId=0')).file, undefined);
  assert.equal(readResourceSelection(href.replace(encodeURIComponent(observedUtc), encodeURIComponent(scope.to))).inspectionUtc, undefined);
});

test('Activity links bind exact capture identity and clear incompatible target, database or window', () => {
  const href = activitySelectionHref(scope, selection);
  assert.deepEqual(readActivitySelection(href, target), selection);
  for (const changed of [{ ...scope, databaseId: 5 }, { ...scope, target: '11111111-1111-4111-8111-000000000002' }, { ...scope, from: '2026-09-26T11:00:00Z' }]) {
    const destination = activitySelectionHref(changed);
    assert.equal(retainActivitySelectionHref(destination, href), destination);
  }
  for (const suffix of ['&sessionIdentity=' + 'b'.repeat(64), '&range=1h', '&databaseId=0', '&at=' + observedUtc]) assert.equal(readActivitySelection(href + suffix), null);
  assert.equal(readActivitySelection(href.replace(encodeURIComponent(observedUtc), encodeURIComponent(scope.to))), null);
});

test('Activity restoration pages metadata by request identity even when session IDs are reused', async () => {
  const calls = [];
  const result = await loadActivitySelection(selection, new URLSearchParams('databaseId=5'), async params => {
    calls.push(new URLSearchParams(params));
    return params.has('cursor') ? page([row(selection.identity)]) : page([row('b'.repeat(64))], 'page2');
  }, new AbortController().signal);
  assert.equal(result.row.identity, selection.identity);
  assert.equal(result.pagesRead, 2);
  assert.equal(calls[1].get('snapshot'), snapshotId);
  assert.equal(calls[1].get('databaseId'), '5');
});

test('Activity rejects mismatched captures, microsecond times, duplicate rows and repeated cursors', async () => {
  const run = reader => loadActivitySelection(selection, new URLSearchParams(), reader, new AbortController().signal);
  await assert.rejects(run(async () => ({ ...page([]), observedUtc: observedUtc.replace('123456', '123457') })), /match/);
  await assert.rejects(run(async () => ({ ...page([]), snapshotId: target })), /match/);
  await assert.rejects(run(async () => page([row('b'.repeat(64)), row('b'.repeat(64))])), /duplicate/);
  await assert.rejects(run(async () => page([], 'repeated')), /cursor repeated/);
  let count = 0;
  const limited = await run(async () => page([], String(++count)));
  assert.equal(count, 11);
  assert.equal(limited.limited, true);
});
