import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mergeManagedTarget, readServerManagementSelection, serverSettingsHref } from '../src/features/targets/serverManagementSelection.ts';
import { readOverviewScope } from '../src/features/overview/overviewModel.ts';

const id = 'abcdef12-abcd-4abc-8def-abcdef123456';
const other = '11111111-1111-4111-8111-111111111111';
test('server settings URLs restore exactly one native identity and retain shared investigation scope', () => {
  const scope = { target: id, range: 'custom', from: '2026-09-26T08:00:00.123456Z',
    to: '2026-09-26T12:00:00.654321Z', databaseId: 7, compare: true };
  const href = serverSettingsHref(scope, id.toUpperCase());
  assert.deepEqual(readServerManagementSelection(href), { state: 'selected', instanceId: id });
  assert.deepEqual(readOverviewScope(href), scope);
  const switched = serverSettingsHref(scope, other);
  assert.equal(readOverviewScope(switched).databaseId, undefined);
  assert.equal(readOverviewScope(switched).from, scope.from);
  assert.equal(readServerManagementSelection('#/servers?target=' + id), undefined);
  assert.equal(readServerManagementSelection(href.replace('#/servers', '#/health')), undefined);
});
test('missing, duplicate, empty and malformed settings identities refuse selection rather than picking a server', () => {
  for (const query of ['serverSettings=1', `target=${id}&serverSettings=`, `target=${id}&serverSettings=2`,
    `target=${id}&serverSettings=1&serverSettings=1`, `target=${id}&target=${other}&serverSettings=1`,
    'target=00000000-0000-0000-0000-000000000000&serverSettings=1',
    `target=%20${id}&serverSettings=1`, `target=${id}-extra&serverSettings=1`]) {
    assert.equal(readServerManagementSelection('#/servers?' + query).state, 'invalid');
  }
});
test('management receipts replace only their loaded identity, preserve newer revisions, and remove terminal retired rows', () => {
  const current = [{ instanceId: id.toUpperCase(), configurationRevision: 2, lifecycle: 'active', displayName: 'Old' },
    { instanceId: other, configurationRevision: 5, lifecycle: 'active', displayName: 'Other' }];
  const updated = { ...current[0], instanceId: id, configurationRevision: 3, displayName: 'Changed' };
  assert.deepEqual(mergeManagedTarget(current, updated), [updated, current[1]]);
  assert.deepEqual(mergeManagedTarget(current, { ...updated, instanceId: '22222222-2222-4222-8222-222222222222' }), current);
  assert.deepEqual(mergeManagedTarget(current, { ...updated, configurationRevision: 1 }), current);
  assert.deepEqual(mergeManagedTarget(current, { ...updated, lifecycle: 'retired' }), [current[1]]);
  assert.equal(current[0].displayName, 'Old');
});
