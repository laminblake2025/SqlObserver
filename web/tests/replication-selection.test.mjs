import assert from 'node:assert/strict';
import test from 'node:test';
import { readReplicationSelection, replicationSelectionHref, retainedReplicationSelectionHref,
  clearReplicationSelectionHref, retainReplicationSelectionHref } from '../src/features/operations/replicationSelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const other = '11111111-1111-4111-8111-000000000002';
const selection = { runId: '22222222-2222-4222-8222-000000000001', sampleTargetRevision: 2 };
const scope = { target, range: 'custom', from: '2026-10-04T10:00:00.000001Z', to: '2026-10-04T11:00:00Z', compare: false };

test('exact replication receipt and historical source revision round trip in their owning screen', () => {
  assert.deepEqual(readReplicationSelection(replicationSelectionHref(scope, selection), target), selection);
  assert.equal(readReplicationSelection(replicationSelectionHref(scope), target), undefined);
  assert.equal(readReplicationSelection(replicationSelectionHref(scope, selection), other), null);
});

test('malformed, repeated and partial identities remain explicit invalid selections', () => {
  const href = replicationSelectionHref(scope, selection);
  for (const candidate of [href + '&replicationRevision=2', href.replace('&replicationRevision=2', ''),
    href.replace(selection.runId, '00000000-0000-0000-0000-000000000000'),
    href.replace('replicationRevision=2', 'replicationRevision=9007199254740992'),
    href.replace('replicationRevision=2', 'replicationRevision=02'), href.replace('check=replication', 'check=agent')])
    assert.equal(readReplicationSelection(candidate, target), null, candidate);
});

test('invalid time contexts never request a replacement receipt', () => {
  const href = replicationSelectionHref(scope, selection);
  const withField = (field, value) => {
    const [path, query] = href.split('?');
    const params = new URLSearchParams(query);
    params.set(field, value);
    return `${path}?${params}`;
  };
  for (const candidate of [href + '&from=' + scope.from, href + '&databaseId=bad',
    withField('to', scope.from), withField('to', '2026-10-12T11:00:00Z'),
    withField('from', '2026-10-04T10:00:00.0000001Z')])
    assert.equal(readReplicationSelection(candidate, target), null, candidate);
  assert.throws(() => replicationSelectionHref({ ...scope, to: scope.from }, selection));
});

test('equivalent microsecond windows and unrelated controls preserve raw valid or invalid pins', () => {
  const current = replicationSelectionHref(scope, selection);
  const equivalent = { ...scope, from: '2026-10-04T10:00:00.0000010Z', to: '2026-10-04T11:00:00.000000Z', databaseId: 5, compare: true };
  assert.deepEqual(readReplicationSelection(retainReplicationSelectionHref(replicationSelectionHref(equivalent), current), target), selection);
  const invalid = current + '&replicationRevision=3';
  assert.equal(readReplicationSelection(retainReplicationSelectionHref(replicationSelectionHref(equivalent), invalid), target), null);
});

test('true target, time-window or Live changes clear only this receipt pin', () => {
  const current = replicationSelectionHref(scope, selection);
  for (const next of [{ ...scope, target: other }, { ...scope, to: '2026-10-04T11:00:00.000001Z' },
    { target, range: '1h', compare: false }])
    assert.equal(readReplicationSelection(retainReplicationSelectionHref(replicationSelectionHref(next), current)), undefined);
  const invalid = current + '&from=broken';
  const cleared = clearReplicationSelectionHref(invalid);
  assert.equal(readReplicationSelection(cleared), undefined);
  assert.equal(new URLSearchParams(cleared.split('?')[1]).getAll('from').length, 2);
});

test('investigating a Live receipt promotes its actual received window for reload', () => {
  const href = retainedReplicationSelectionHref({ target, range: '1h', compare: false },
    { fromUtc: scope.from, toUtc: scope.to }, selection);
  assert.deepEqual(readReplicationSelection(href, target), selection);
  const query = new URLSearchParams(href.split('?')[1]);
  assert.equal(query.get('range'), 'custom');
  assert.equal(query.get('from'), scope.from);
  assert.equal(query.get('to'), scope.to);
});
