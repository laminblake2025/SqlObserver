import test from 'node:test';
import assert from 'node:assert/strict';
import { fleetServerRows, groupFleetServerRows } from '../src/features/overview/fleetGridModel.ts';

const value = (number, state = 'current') => ({ value: number, state, observedAtUtc: '2026-09-24T12:00:00Z' });
const evidence = (targetId, overrides = {}) => ({
  targetId, displayName: targetId, collectionState: 'current', lastObservedUtc: '2026-09-24T12:00:00Z',
  activeAlerts: value(0), blockedSessions: value(0), deadlocks: value(0),
  issues: [], resources: [], series: [], gaps: [], ...overrides,
});
const snapshot = (rows) => ({
  refreshedAtUtc: '2026-09-24T12:00:00Z', fromUtc: '2026-09-23T12:00:00Z',
  toUtc: '2026-09-24T12:00:00Z', targetId: null, excludedTargets: 0,
  targets: rows.map(([targetId, displayName]) => ({ targetId, displayName, lifecycle: 'active', environment: null, tags: [], groupingRevision: 0, canManageGrouping: false })),
  evidence: rows.map(([targetId]) => evidence(targetId)),
});

test('fleet grouping uses persisted environment and tags without losing severity order', () => {
  const fleet = snapshot([['a', 'Alpha'], ['b', 'Beta'], ['c', 'Charlie']]);
  fleet.targets[0].environment = 'production'; fleet.targets[0].tags = ['critical', 'east'];
  fleet.targets[1].environment = 'staging'; fleet.targets[1].tags = ['east'];
  fleet.evidence[2].issues = [{ priority: 0, title: 'Damaged backup' }];
  const rows = fleetServerRows(fleet, '');
  assert.deepEqual(groupFleetServerRows(rows, 'environment').map(group => [group.label, group.rows.map(row => row.target.targetId)]),
    [['Unassigned', ['c']], ['production', ['a']], ['staging', ['b']]]);
  assert.deepEqual(groupFleetServerRows(rows, 'tag').map(group => [group.label, group.rows.map(row => row.target.targetId)]),
    [['Untagged', ['c']], ['critical', ['a']], ['east', ['a', 'b']]]);
  assert.deepEqual(fleetServerRows(fleet, 'east').map(row => row.target.targetId), ['a', 'b']);
  assert.deepEqual(fleetServerRows(fleet, 'production').map(row => row.target.targetId), ['a']);
});

test('fleet grid ranks observed exceptions before inventory order without treating gaps as healthy', () => {
  const fleet = snapshot([['a', 'Alpha'], ['b', 'Beta'], ['c', 'Charlie'], ['d', 'Delta']]);
  fleet.evidence = [
    evidence('a'),
    evidence('b', { blockedSessions: value(5) }),
    evidence('c', { issues: [{ priority: 0, title: 'Damaged backup' }] }),
    evidence('d', { gaps: ['SQL core unavailable'], activeAlerts: value(null, 'unavailable') }),
  ];
  assert.deepEqual(fleetServerRows(fleet, '').map(row => row.target.targetId), ['c', 'b', 'd', 'a']);
  assert.equal(fleetServerRows(fleet, '')[1].signals.blocking.tone, 'attention');
  assert.equal(fleetServerRows(fleet, '')[2].signals.blocking.text, '0');
  assert.equal(fleetServerRows(fleet, ' beta ')[0].target.targetId, 'b');
});

test('signal cells distinguish source evidence from health and hide stale numeric readings', () => {
  const fleet = snapshot([['a', 'Alpha']]);
  fleet.evidence = [evidence('a', {
    blockedSessions: value(null, 'unavailable'),
    resources: [
      { label: 'host.cpu.percent', value: 91.2, unit: '%', state: 'observed', observedAtUtc: '2026-09-24T12:00:00Z' },
      { label: 'host.memory.available_bytes', value: 99, unit: 'GiB', state: 'observed', observedAtUtc: '2026-09-24T12:00:00Z' },
      { label: 'SQL scheduler CPU · selected window', value: 68.5, unit: '%', state: 'stale', observedAtUtc: '2026-09-20T00:00:00Z' },
      { label: 'OS available memory via SQL · selected window', value: 4.25, unit: 'GiB', state: 'observed', observedAtUtc: '2026-09-24T11:00:00Z' },
      { label: 'Wait · LCK_M_X', value: 2.5, unit: 'seconds since prior sample', state: 'partial', observedAtUtc: '2026-09-24T12:00:00Z' },
      { label: 'Backup evidence', value: 0, unit: 'records', state: 'complete', observedAtUtc: '2026-09-24T12:00:00Z' },
    ],
  })];
  const { signals } = fleetServerRows(fleet, '')[0];
  assert.deepEqual([signals.cpu.text, signals.cpu.tone], ['—', 'unknown']);
  assert.equal(signals.memory.text, '4.3 GiB');
  assert.equal(signals.waits.tone, 'partial');
  assert.equal(signals.blocking.tone, 'unknown');
  assert.equal(signals.backups.text, '—');
  assert.match(signals.backups.detail, /No current backup policy summary/);
  assert.equal(signals.ha.tone, 'unknown');
  fleet.evidence[0].resources.find(item => item.label === 'SQL scheduler CPU · selected window').state = 'observed';
  assert.equal(fleetServerRows(fleet, '')[0].signals.cpu.text, '68.5 %');
});

test('backup and HA dots surface confirmed exceptions only with usable source evidence', () => {
  const fleet = snapshot([['a', 'Alpha']]);
  fleet.evidence = [evidence('a', {
    issues: [{ title: 'Damaged backup' }, { title: 'Availability replica disconnected' }],
    resources: [
      { label: 'Backup evidence', value: 3, unit: 'records', state: 'complete', observedAtUtc: '2026-09-24T12:00:00Z' },
      { label: 'Availability replicas', value: 2, unit: 'replicas', state: 'partial', observedAtUtc: '2026-09-24T12:00:00Z' },
    ],
  })];
  const signals = fleetServerRows(fleet, '')[0].signals;
  assert.equal(signals.backups.tone, 'unknown');
  assert.equal(signals.backups.text, '—');
  assert.equal(signals.ha.tone, 'attention');
  assert.match(signals.ha.detail, /Availability replica disconnected/);

  fleet.evidence[0].resources[0].state = 'stale';
  fleet.evidence[0].resources[1].value = null;
  const unavailable = fleetServerRows(fleet, '')[0].signals;
  assert.equal(unavailable.backups.tone, 'unknown');
  assert.equal(unavailable.ha.tone, 'unknown');
});
