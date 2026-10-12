import test from 'node:test';
import assert from 'node:assert/strict';
import { parseOverviewBackupSummary, backupSummaryExpired } from '../src/features/overview/overviewBackupSummary.ts';
import { fleetServerRows } from '../src/features/overview/fleetGridModel.ts';
import { getOverview } from '../src/features/overview/overviewApi.ts';
import { backupAssessmentHref, issueHref, readOverviewScope } from '../src/features/overview/overviewModel.ts';
import { overviewBackupFixture } from '../tools/overview-backup-fixture.mjs';

const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const at = '2026-09-30T12:00:00.000001Z', cutoff = '2026-09-30T12:00:00.0000019Z', expiry = '2026-09-30T12:02:00.000001Z';
const counts = (overrides = {}) => ({ withinThreshold: 1, policyViolated: 0, indeterminate: 0, notRequired: 0, disabled: 0, unknownReasons: [], ...overrides });
const summary = (overrides = {}) => ({ targetId: id, assessedAtUtc: at, validUntilUtc: expiry, status: 'within_policy', evidenceStatus: 'retained', coverage: 'complete',
  databaseCount: 1, collectorEnabled: true, full: counts(), log: counts({ withinThreshold: 0, notRequired: 1 }), observedFaultDatabases: 0, ...overrides });
const snapshot = (backup = summary()) => ({ refreshedAtUtc: cutoff, fromUtc: '2026-09-30T11:00:00Z', toUtc: '2026-09-30T12:00:00Z', targetId: id,
  targets: [{ targetId: id, displayName: 'SQL 1', lifecycle: 'active', environment: null, tags: [], groupingRevision: 0, canManageGrouping: false }], excludedTargets: 0,
  evidence: [{ targetId: id, displayName: 'SQL 1', collectionState: 'current', lastObservedUtc: at, activeAlerts: { value: 0, state: 'current' },
    blockedSessions: { value: 0, state: 'current' }, deadlocks: { value: 0, state: 'current' }, issues: [], resources: [], series: [], gaps: [], backupSummary: backup }] });

test('separate full/log accounting with canonical UTC and an exact half-open expiry', () => {
  const value = parseOverviewBackupSummary(summary(), id, cutoff);
  assert.equal(value.full.withinThreshold, 1); assert.equal(value.log.notRequired, 1);
  assert.equal(parseOverviewBackupSummary(value, id, '2026-09-30T12:02:00.000000Z').status, 'within_policy');
  assert.throws(() => parseOverviewBackupSummary(value, id, expiry));
  assert.equal(backupSummaryExpired(value, Date.parse('2026-09-30T12:02:00.000Z')), false);
  assert.equal(backupSummaryExpired(value, Date.parse('2026-09-30T12:02:00.001Z')), true);
  assert.equal(parseOverviewBackupSummary(null, id, cutoff), null);
  assert.equal(parseOverviewBackupSummary(undefined, id, cutoff), null);
});

test('unknown causes stay counted even beside confirmed violations and observed faults', () => {
  const value = summary({ status: 'policy_violated', full: counts({ withinThreshold: 0, policyViolated: 1 }),
    log: counts({ withinThreshold: 0, indeterminate: 1, unknownReasons: [{ reason: 'other_replica_evidence_required', count: 1 }] }), observedFaultDatabases: 1 });
  parseOverviewBackupSummary(value, id, cutoff);
  const signal = fleetServerRows(snapshot(value), '', Date.parse(at))[0].signals.backups;
  assert.equal(signal.tone, 'attention'); assert.equal(signal.text, '1 failed · 1 unknown');
  assert.match(signal.detail, /Full: 0 within limit, 1 failed/);
  assert.match(signal.detail, /other replica evidence required: 1/);
});

test('observed damage stays red when age is unknown, without inventing policy failure', () => {
  const value = summary({ status: 'indeterminate', full: counts({ withinThreshold: 0, indeterminate: 1,
    unknownReasons: [{ reason: 'timestamp_qualification_unavailable', count: 1 }] }), observedFaultDatabases: 1 });
  parseOverviewBackupSummary(value, id, cutoff);
  const signal = fleetServerRows(snapshot(value), '', Date.parse(at))[0].signals.backups;
  assert.equal(signal.tone, 'attention'); assert.equal(signal.text, '1 with faults · 1 unknown');
  assert.match(signal.detail, /timestamp qualification unavailable/);
});

test('healthy, disabled, collector-off, unavailable, missing and expired cache states stay distinct', () => {
  const zero = counts({ withinThreshold: 0 });
  const cases = [
    [summary(), 'Within policy', 'observed'],
    [summary({ status: 'disabled', full: counts({ withinThreshold: 0, disabled: 1 }), log: counts({ withinThreshold: 0, disabled: 1 }) }), 'Policies disabled', 'unknown'],
    [summary({ status: 'indeterminate', collectorEnabled: false }), 'Unknown', 'unknown'],
    [summary({ status: 'indeterminate', evidenceStatus: 'evidence_not_retained', coverage: 'failed', full: zero, log: zero }), 'Unknown', 'unknown'],
    [null, '—', 'unknown'],
  ];
  for (const [value, text, tone] of cases) {
    parseOverviewBackupSummary(value, id, cutoff);
    const signal = fleetServerRows(snapshot(value), '', Date.parse(at))[0].signals.backups;
    assert.equal(signal.text, text); assert.equal(signal.tone, tone);
  }
  assert.equal(fleetServerRows(snapshot(), '', Date.parse(expiry) + 1)[0].signals.backups.tone, 'unknown');
});

test('enabled policy success does not conceal disabled checks on other databases', () => {
  const value = summary({ databaseCount: 2, full: counts({ disabled: 1 }), log: counts({ withinThreshold: 0, notRequired: 1, disabled: 1 }) });
  parseOverviewBackupSummary(value, id, cutoff);
  const signal = fleetServerRows(snapshot(value), '', Date.parse(at))[0].signals.backups;
  assert.equal(signal.tone, 'partial'); assert.equal(signal.text, 'Within policy · 2 disabled');
});

const corruptions = {
  target: v => { v.targetId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'; },
  extra: v => { v.databaseFingerprint = 'private identity'; },
  missing: v => { delete v.log; },
  status: v => { v.status = 'healthy'; },
  dishonest: v => { v.collectorEnabled = false; },
  coverage: v => { v.coverage = 'partial'; },
  count: v => { v.full.withinThreshold = 2; },
  negative: v => { v.full.disabled = -1; },
  faults: v => { v.observedFaultDatabases = 2; },
  reason: v => { v.full.unknownReasons = [{ reason: 'made_up', count: 1 }]; },
  accounting: v => { v.full.unknownReasons = [{ reason: 'policy_not_configured', count: 1 }]; },
  duplicate: v => { v.full.unknownReasons = Array(2).fill({ reason: 'policy_not_configured', count: 1 }); },
  unavailableCounts: v => { v.evidenceStatus = 'evidence_not_retained'; v.coverage = 'failed'; v.status = 'indeterminate'; },
  future: v => { v.assessedAtUtc = '2026-09-30T12:00:00.000002Z'; },
  expired: v => { v.validUntilUtc = at; },
  lifetime: v => { v.validUntilUtc = '2026-09-30T12:02:00.000002Z'; },
  zone: v => { v.assessedAtUtc = '2026-09-30T12:00:00+00:00'; },
  precision: v => { v.assessedAtUtc = cutoff; },
};
for (const [name, corrupt] of Object.entries(corruptions)) test(`rejects ${name} corruption before a grid verdict`, () => {
  const value = summary(); corrupt(value);
  assert.throws(() => parseOverviewBackupSummary(value, id, cutoff));
});

test('Overview fetch validates the new aggregate before exposing it to the grid', async () => {
  const original = globalThis.fetch;
  const window = { fromUtc: snapshot().fromUtc, toUtc: snapshot().toUtc };
  try {
    globalThis.fetch = async () => Response.json(snapshot());
    const value = await getOverview(id, window, new AbortController().signal);
    assert.equal(value.evidence[0].backupSummary.status, 'within_policy');
    globalThis.fetch = async () => Response.json(snapshot(summary({ observedFaultDatabases: 999 })));
    await assert.rejects(() => getOverview(id, window, new AbortController().signal), /invalid backup/);
  } finally { globalThis.fetch = original; }
});

test('backup drill-down restores the exact assessment cutoff, even from a different history window', () => {
  const scope = { target: '', range: 'custom', from: '2026-09-01T00:00:00Z', to: '2026-09-02T00:00:00Z', compare: false };
  const link = backupAssessmentHref(scope, id, at);
  assert.ok(link.startsWith('#/operations?'));
  assert.equal(readOverviewScope(link).to, at); assert.equal(readOverviewScope(link).target, id);
  assert.equal(readOverviewScope(backupAssessmentHref(scope, id, null)).range, '1h');
  assert.equal(readOverviewScope(issueHref(scope, id, 'operations', at, 'Backup policy checks failed')).to, at);
});

test('Overview accepts its published 1 MiB bound, including valid fleet bodies above 256 KiB', async () => {
  const original = globalThis.fetch;
  const body = snapshot(), window = { fromUtc: body.fromUtc, toUtc: body.toUtc };
  try {
    body.evidence[0].gaps = ['x'.repeat(270000)];
    globalThis.fetch = async () => Response.json(body);
    assert.equal((await getOverview(id, window, new AbortController().signal)).evidence[0].gaps.length, 1);
    body.evidence[0].gaps = ['x'.repeat(1048576)];
    await assert.rejects(() => getOverview(id, window, new AbortController().signal), /Overview evidence exceeded/);
  } finally { globalThis.fetch = original; }
});

test('synthetic mounted states obey the same aggregate wire contract', () => {
  for (let index = 0; index < 10; index++) {
    parseOverviewBackupSummary(overviewBackupFixture(id, at, index), id, cutoff);
    parseOverviewBackupSummary(overviewBackupFixture(id, at, index, true), id, cutoff);
  }
});
