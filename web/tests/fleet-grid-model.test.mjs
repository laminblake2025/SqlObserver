import test from 'node:test';
import assert from 'node:assert/strict';
import { fleetServerRows } from '../src/features/overview/fleetGridModel.ts';

const value = (number, state = 'current') => ({ value: number, state, observedAtUtc: '2026-09-24T12:00:00Z' });
const evidence = (targetId, overrides = {}) => ({
  targetId, displayName: targetId, collectionState: 'current', lastObservedUtc: '2026-09-24T12:00:00Z',
  activeAlerts: value(0), blockedSessions: value(0), deadlocks: value(0),
  issues: [], resources: [], series: [], gaps: [], ...overrides,
});
const snapshot = (rows) => ({
  refreshedAtUtc: '2026-09-24T12:00:00Z', fromUtc: '2026-09-23T12:00:00Z',
  toUtc: '2026-09-24T12:00:00Z', targetId: null, excludedTargets: 0,
  targets: rows.map(([targetId, displayName]) => ({ targetId, displayName, lifecycle: 'active' })),
  evidence: rows.map(([targetId]) => evidence(targetId)),
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
      { label: 'host.cpu.percent', value: 91.2, unit: '%', state: 'stale', observedAtUtc: '2026-09-20T00:00:00Z' },
      { label: 'host.memory.available_bytes', value: 4.25, unit: 'GiB', state: 'observed', observedAtUtc: '2026-09-24T11:00:00Z' },
      { label: 'Wait · LCK_M_X', value: 2.5, unit: 'seconds since prior sample', state: 'partial', observedAtUtc: '2026-09-24T12:00:00Z' },
      { label: 'Backup evidence', value: 0, unit: 'records', state: 'complete', observedAtUtc: '2026-09-24T12:00:00Z' },
    ],
  })];
  const { signals } = fleetServerRows(fleet, '')[0];
  assert.deepEqual([signals.cpu.text, signals.cpu.tone], ['—', 'unknown']);
  assert.equal(signals.memory.text, '4.3 GiB');
  assert.equal(signals.waits.tone, 'partial');
  assert.equal(signals.blocking.tone, 'unknown');
  assert.equal(signals.backups.text, '0 records');
  assert.match(signals.backups.detail, /does not establish RPO health/);
  assert.equal(signals.ha.tone, 'unknown');
});
