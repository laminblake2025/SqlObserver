import assert from 'node:assert/strict';
import test from 'node:test';
import { readResourceSelection, resourceSelectionHref, retainResourceSelectionHref } from '../src/features/resources/resourceSelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const other = '11111111-1111-4111-8111-000000000002';
const volume = { sampleTargetRevision: 2, volumeKey: 'a'.repeat(64) };
const scope = { target, range: 'custom', from: '2026-10-03T10:00:00Z', to: '2026-10-03T11:00:00Z', compare: true, databaseId: 5 };

test('exact historical volume revision/key coexists with file, NUMA and inspection identity', () => {
  const selection = { volume, file: { databaseId: 5, fileId: 2 }, numaNodeId: 0, inspectionUtc: '2026-10-03T10:30:00Z' };
  const decoded = readResourceSelection(resourceSelectionHref(scope, selection), target);
  assert.deepEqual(decoded.volume, volume);
  assert.deepEqual(decoded.file, selection.file);
  assert.equal(decoded.numaNodeId, 0);
  assert.equal(decoded.inspectionUtc, '2026-10-03T10:30:00.000Z');
});

test('ambiguous partial or unsafe volume identities never choose substitutes', () => {
  const href = resourceSelectionHref(scope, { volume });
  for (const hash of [href + '&volumeRevision=2', href.replace('&volumeRevision=2', ''), href.replace('volumeKey=' + 'a'.repeat(64), 'volumeKey=path'), href.replace('volumeRevision=2', 'volumeRevision=9007199254740992')]) {
    const decoded = readResourceSelection(hash, target);
    assert.equal(decoded.volume, undefined);
    assert.equal(decoded.invalidVolume, true);
    assert.equal(decoded.issues.length > 0, true);
  }
});

test('time and database changes retain exact volume identity, target change drops it', () => {
  const current = resourceSelectionHref(scope, { volume });
  const nextScope = { ...scope, range: '6h', from: undefined, to: undefined, databaseId: 8 };
  const next = retainResourceSelectionHref(resourceSelectionHref(nextScope, {}), current);
  assert.deepEqual(readResourceSelection(next, target).volume, volume);
  const changed = retainResourceSelectionHref(resourceSelectionHref({ ...nextScope, target: other }, {}), current);
  assert.equal(readResourceSelection(changed, other).volume, undefined);
  assert.equal(readResourceSelection(current, other).volume, undefined);
});

test('invalid shared time context fences volume requests and explicit clearing removes identity', () => {
  const current = resourceSelectionHref(scope, { volume });
  const invalid = readResourceSelection(current + '&from=2026-10-03T10:00:00Z', target);
  assert.equal(invalid.invalidContext, true);
  assert.equal(invalid.invalidVolume, true);
  assert.equal(invalid.volume, undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(scope, {}), target).volume, undefined);
});
