import assert from 'node:assert/strict';
import test from 'node:test';
import { readBackupAvailabilitySelection, backupAvailabilitySelectionHref, clearBackupAvailabilitySelectionHref,
  retainBackupAvailabilitySelectionHref, retainedBackupAvailabilitySelectionHref, retainBackupAvailabilityRawPin } from '../src/features/operations/backupAvailabilitySelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001', other = '11111111-1111-4111-8111-000000000002';
const selected = { runId: '22222222-2222-4222-8222-000000000001', sampleTargetRevision: 2 };
const scope = { target, range: 'custom', from: '2026-10-04T10:00:00.000001Z', to: '2026-10-04T11:00:00Z', compare: false };
function replace(href, key, value) {
  const [path, query] = href.split('?'), params = new URLSearchParams(query);
  params.set(key, value);
  return `${path}?${params}`;
}
for (const kind of ['backup', 'availability']) {
  const run = kind === 'backup' ? 'backupRun' : 'availabilityRun', revision = kind === 'backup' ? 'backupRevision' : 'availabilityRevision';
  test(`${kind}: exact run/sample revision is owned by its server and category`, () => {
    const href = backupAvailabilitySelectionHref(scope, kind, selected);
    assert.deepEqual(readBackupAvailabilitySelection(href, kind, target), selected);
    assert.equal(readBackupAvailabilitySelection(backupAvailabilitySelectionHref(scope, kind), kind), undefined);
    assert.equal(readBackupAvailabilitySelection(href, kind, other), null);
    assert.equal(readBackupAvailabilitySelection(replace(href, 'check', 'agent'), kind), null);
    assert.equal(readBackupAvailabilitySelection(href, kind === 'backup' ? 'availability' : 'backup'), undefined);
  });
  test(`${kind}: invalid and repeated identities never select a replacement`, () => {
    const href = backupAvailabilitySelectionHref(scope, kind, selected);
    for (const candidate of [href + `&${revision}=2`, href + `&${run}=${selected.runId}`, href.replace(`&${revision}=2`, ''),
      replace(href, run, '00000000-0000-0000-0000-000000000000'), replace(href, revision, '02'),
      replace(href, revision, '9007199254740992'), replace(href, revision, '0')])
      assert.equal(readBackupAvailabilitySelection(candidate, kind, target), null, candidate);
  });
  test(`${kind}: malformed shared contexts remain invalid until explicitly corrected`, () => {
    const href = backupAvailabilitySelectionHref(scope, kind, selected);
    for (const candidate of [href + '&from=' + scope.from, href + '&target=' + target, href + '&compare=1&compare=0',
      replace(href, 'databaseId', 'bad'), replace(href, 'to', scope.from), replace(href, 'to', '2026-10-12T11:00:00Z'),
      replace(href, 'from', '2026-10-04T10:00:00.0000001Z'), replace(href, 'from', '2026-02-30T10:00:00Z')])
      assert.equal(readBackupAvailabilitySelection(candidate, kind, target), null, candidate);
    assert.throws(() => backupAvailabilitySelectionHref({ ...scope, to: scope.from }, kind, selected));
  });
  test(`${kind}: equivalent microsecond bounds and database/compare changes preserve raw pin`, () => {
    const current = backupAvailabilitySelectionHref(scope, kind, selected);
    const next = backupAvailabilitySelectionHref({ ...scope, from: '2026-10-04T10:00:00.0000010Z', to: '2026-10-04T11:00:00.000000Z', databaseId: 5, compare: true }, kind);
    assert.deepEqual(readBackupAvailabilitySelection(retainBackupAvailabilitySelectionHref(next, current), kind, target), selected);
    assert.equal(readBackupAvailabilitySelection(retainBackupAvailabilitySelectionHref(next, current + `&${revision}=3`), kind), null);
  });
  test(`${kind}: actual target/window/Live/category changes retire this source pin`, () => {
    const current = backupAvailabilitySelectionHref(scope, kind, selected);
    for (const next of [backupAvailabilitySelectionHref({ ...scope, target: other }, kind),
      backupAvailabilitySelectionHref({ ...scope, to: '2026-10-04T11:00:00.000001Z' }, kind),
      backupAvailabilitySelectionHref({ target, range: '1h', compare: false }, kind),
      backupAvailabilitySelectionHref(scope, kind === 'backup' ? 'availability' : 'backup')])
      assert.equal(readBackupAvailabilitySelection(retainBackupAvailabilitySelectionHref(next, current), kind), undefined);
  });
  test(`${kind}: Live investigation fixes received bounds and retains invalid raw pins`, () => {
    const live = { target, range: '1h', compare: false }, window = { fromUtc: scope.from, toUtc: scope.to };
    const fixed = retainedBackupAvailabilitySelectionHref(live, kind, window, selected);
    const query = new URLSearchParams(fixed.split('?')[1]);
    assert.equal(query.get('range'), 'custom'); assert.equal(query.get('from'), scope.from); assert.equal(query.get('to'), scope.to);
    assert.deepEqual(readBackupAvailabilitySelection(fixed, kind, target), selected);
    const invalid = backupAvailabilitySelectionHref(live, kind, selected) + `&${revision}=3`;
    const retainedInvalid = retainBackupAvailabilityRawPin(retainedBackupAvailabilitySelectionHref(live, kind, window), invalid, kind);
    assert.equal(readBackupAvailabilitySelection(retainedInvalid, kind, target), null);
    assert.equal(new URLSearchParams(retainedInvalid.split('?')[1]).getAll(revision).length, 2);
  });
  test(`${kind}: clearing removes only this pin and keeps invalid context visible`, () => {
    const invalid = backupAvailabilitySelectionHref(scope, kind, selected) + '&from=broken&other=retained';
    const cleared = clearBackupAvailabilitySelectionHref(invalid, kind), query = new URLSearchParams(cleared.split('?')[1]);
    assert.equal(readBackupAvailabilitySelection(cleared, kind), undefined);
    assert.equal(query.getAll('from').length, 2); assert.equal(query.get('other'), 'retained');
  });
}
