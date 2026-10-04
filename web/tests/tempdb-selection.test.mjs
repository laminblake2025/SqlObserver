import test from 'node:test';
import assert from 'node:assert/strict';
import { readTempDbSelection, tempDbSelectionHref, clearTempDbSelectionHref, retainedTempDbSelectionHref, retainTempDbSelectionHref } from '../src/features/operations/tempDbSelectionModel.ts';
const target = '11111111-1111-4111-8111-000000000001';
const runId = '22222222-2222-4222-8222-000000000001';
const from = '2026-10-03T08:00:00.000001Z', to = '2026-10-03T12:00:00.000001Z';
const scope = { target, range: 'custom', from, to, compare: false };
const selection = { runId, sampleTargetRevision: 3 };
const base = tempDbSelectionHref(scope);
const pinned = tempDbSelectionHref(scope, selection);

test('TempDB selection restores an exact tuple and distinguishes absent and malformed pins', () => {
  assert.equal(readTempDbSelection(base, target), undefined);
  assert.deepEqual(readTempDbSelection(pinned, target), selection);
  assert.deepEqual(readTempDbSelection(pinned.replace(runId, runId.toUpperCase()), target.toUpperCase()), selection);
  for (const suffix of [`&tempDbRun=${runId}`, '&tempDbRevision=3', `&tempDbRun=${runId}&tempDbRevision=0`,
    `&tempDbRun=${runId}&tempDbRevision=03`, `&tempDbRun=${runId}&tempDbRevision=9007199254740992`,
    '&tempDbRun=00000000-0000-0000-0000-000000000000&tempDbRevision=3', '&tempDbRun=wrong&tempDbRevision=3']) {
    assert.equal(readTempDbSelection(base + suffix, target), null);
  }
  assert.equal(readTempDbSelection(pinned + '&tempDbRevision=3'), null);
  assert.equal(readTempDbSelection(pinned + `&tempDbRun=${runId}`), null);
});

test('TempDB pin rejects ambiguous owner and window rather than selecting a substitute', () => {
  for (const hash of [pinned.replace('operations', 'resources'), pinned.replace('check=tempdb', 'check=backups'),
    pinned + '&check=tempdb', pinned + `&target=${target}`, pinned + '&range=custom', pinned + `&from=${from}`,
    pinned + '&databaseId=6&databaseId=6', pinned + '&compare=1&compare=0', pinned.replace(encodeURIComponent(from), encodeURIComponent('2026-02-30T08:00:00Z')),
    pinned.replace(encodeURIComponent(to), encodeURIComponent(from)), pinned.replace(encodeURIComponent(to), encodeURIComponent('2026-10-11T12:00:00Z'))]) {
    assert.equal(readTempDbSelection(hash, target), null, hash);
  }
  assert.equal(readTempDbSelection(pinned, runId), null);
});

test('TempDB pin remains explicit during compatible database/compare context changes', () => {
  const next = tempDbSelectionHref({ ...scope, databaseId: 6, compare: true });
  assert.deepEqual(readTempDbSelection(retainTempDbSelectionHref(next, pinned)), selection);
  const invalid = base + `&tempDbRun=${runId}&tempDbRevision=3&tempDbRevision=4`;
  const retained = retainTempDbSelectionHref(next, invalid);
  assert.equal(readTempDbSelection(retained), null);
  assert.deepEqual(new URLSearchParams(retained.split('?')[1]).getAll('tempDbRevision'), ['3', '4']);
});

test('TempDB pins drop when the owner or effective window changes', () => {
  for (const next of [tempDbSelectionHref({ ...scope, target: runId }), tempDbSelectionHref({ ...scope, range: '1h', from: undefined, to: undefined }),
    tempDbSelectionHref({ ...scope, from: '2026-10-03T08:00:00.000002Z' }), base.replace('check=tempdb', 'check=agent')]) {
    assert.equal(retainTempDbSelectionHref(next, pinned), next);
  }
  const native = tempDbSelectionHref({ ...scope, from: from.replace('Z', '0Z'), to: to.replace('Z', '0Z') });
  assert.deepEqual(readTempDbSelection(retainTempDbSelectionHref(native, pinned)), selection);
});

test('selecting a live receipt fixes its exact received window and clear keeps that window', () => {
  const live = { target, range: '1h', compare: false };
  const href = retainedTempDbSelectionHref(live, { fromUtc: from, toUtc: to }, selection);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.equal(params.get('range'), 'custom'); assert.equal(params.get('from'), from); assert.equal(params.get('to'), to);
  assert.deepEqual(readTempDbSelection(href), selection);
  assert.equal(readTempDbSelection(tempDbSelectionHref(scope)), undefined);
  assert.throws(() => tempDbSelectionHref({ ...scope, to: from }, selection));
  assert.throws(() => tempDbSelectionHref(scope, { runId, sampleTargetRevision: 0 }));
});

test('clearing an invalid pin leaves malformed or repeated window context explicit for correction', () => {
  const invalid = pinned + '&from=bad&tempDbRun=bad';
  const cleared = clearTempDbSelectionHref(invalid);
  const params = new URLSearchParams(cleared.split('?')[1]);
  assert.equal(readTempDbSelection(cleared), undefined);
  assert.deepEqual(params.getAll('from'), [from, 'bad']);
  assert.equal(params.has('tempDbRun'), false); assert.equal(params.has('tempDbRevision'), false);
});
