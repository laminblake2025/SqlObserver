import test from 'node:test';
import assert from 'node:assert/strict';
import { buildInvestigationSummary } from '../src/features/health/investigationSummaryModel.ts';
import { overviewHref } from '../src/features/overview/overviewModel.ts';
import { databaseJumpHref, queryJumpHref } from '../src/components/commandPaletteModel.ts';

const targetId = '11111111-1111-4111-8111-000000000001';
const otherTarget = '11111111-1111-4111-8111-000000000002';
const run = 'abcdefab-abcd-4abc-8abc-abcdefabcdef';
const fromUtc = '2026-09-26T08:00:00.123456Z';
const toUtc = '2026-09-26T12:00:00.123456Z';
const middle = '2026-09-26T10:00:00.123456Z';
const scope = { target: targetId, range: 'custom', from: fromUtc, to: toUtc, databaseId: 5, compare: true };
const issue = (extra = {}) => ({ targetId, server: 'SQL 01', title: 'Blocking observed', detail: '3 blocked sessions observed', destination: 'activity', priority: 1, observedAtUtc: middle, ...extra });
const plan = (extra = {}, databaseId = 5) => ({ id: `plan:${databaseId}:${'b'.repeat(64)}:${'c'.repeat(64)}`, kind: 'plan_regression', timeBasis: 'last_observed', timeUtc: middle,
  title: 'Plan regression comparison', detail: 'Observed duration 10 → 40 ms. Correlation only; plan-switch time is unknown.',
  href: queryJumpHref(scope, targetId, databaseId, 'b'.repeat(64), 'c'.repeat(64), 'query_store'), ...extra });
const configuration = (extra = {}, id = run) => ({ id: `configuration:${id}`, kind: 'configuration', timeBasis: 'detected', timeUtc: '2026-09-26T09:52:00.123456Z',
  title: 'Server configuration detected', detail: 'Configured 5 → 50; actual change time unknown.',
  href: `${overviewHref(scope, 'health')}&healthSection=summary&configurationRun=${id}`, ...extra });
const options = (extra = {}, databaseId = 5) => ({ id: `database-options:${run}:${databaseId}`, kind: 'database_options', timeBasis: 'detected', timeUtc: '2026-09-26T09:55:00.123456Z',
  title: `Database ${databaseId} options detected`, detail: 'Compatibility 150 → 160 detected between observations.',
  href: databaseJumpHref(scope, targetId, databaseId), ...extra });
const summarize = (extra = {}) => buildInvestigationSummary({ targetId, scope, fromUtc, toUtc, issues: [], events: [], ...extra });

test('plan comparisons are promoted beside exceptions and attach observed context without replacing source semantics', () => {
  const result = summarize({ issues: [issue(), issue({ title: 'Backup faults observed', priority: 0, destination: 'operations' })], events: [plan(), configuration(), options()] });
  assert.deepEqual(result.items.map(item => item.title), ['Backup faults observed', 'Plan regression comparison', 'Blocking observed']);
  const comparison = result.items[1];
  assert.equal(comparison.timeBasis, 'last_observed');
  assert.equal(comparison.databaseId, 5);
  assert.deepEqual(comparison.contexts.map(context => context.event.kind), ['database_options', 'configuration']);
  assert.equal(comparison.contexts[1].relationship, 'Detected 8 min before this last observed query group');
  assert.equal(comparison.contexts[1].event, result.items[2].contexts[1].event);
  assert.match(comparison.detail, /plan-switch time is unknown/);
  assert.equal(comparison.href, plan().href);
  const backupQuery = new URLSearchParams(result.items[0].href.split('?')[1]);
  assert.equal(backupQuery.get('to'), middle);
  assert.equal(backupQuery.get('from'), '2026-09-26T09:00:00.123Z');
  const query = new URLSearchParams(result.items[2].href.split('?')[1]);
  assert.equal(query.get('from'), fromUtc);
  assert.equal(query.get('to'), toUtc);
  assert.equal(query.get('databaseId'), '5');
  assert.equal(query.get('compare'), '1');
});

test('temporal associations use inclusive 15-minute proximity and half-open microsecond window bounds', () => {
  const result = summarize({ events: [plan(),
    configuration({ timeUtc: '2026-09-26T09:45:00.123456Z' }),
    configuration({ timeUtc: '2026-09-26T09:45:00.123455Z' }, 'abcdefab-abcd-4abc-8abc-abcdefabcdee'),
    options({ timeUtc: '2026-09-26T10:15:00.123456Z' }),
    options({ timeUtc: '2026-09-26T10:15:00.123457Z' }, 6),
    plan({ id: `plan:5:${'d'.repeat(64)}:${'e'.repeat(64)}`, timeUtc: toUtc,
      href: queryJumpHref(scope, targetId, 5, 'd'.repeat(64), 'e'.repeat(64), 'query_store') }),
  ] });
  assert.equal(result.items.length, 1);
  assert.deepEqual(result.items[0].contexts.map(context => context.event.timeUtc).sort(), ['2026-09-26T09:45:00.123456Z', '2026-09-26T10:15:00.123456Z']);
  assert.match(result.items[0].contexts.find(context => context.event.kind === 'database_options').relationship, /after/);
  assert.equal(summarize({ events: [plan({ timeUtc: fromUtc })] }).items.length, 1);
});

test('latest Overview snapshots outside or missing history time stay explicit without temporal associations or false pinned activity', () => {
  const result = summarize({ issues: [issue({ observedAtUtc: toUtc }), issue({ title: 'Untimed exception', observedAtUtc: null })], events: [configuration({ timeUtc: '2026-09-26T11:59:00.123456Z' })] });
  assert.deepEqual(result.items.map(item => item.windowRelation), ['outside_window', 'unknown']);
  for (const item of result.items) {
    assert.equal(item.contexts.length, 0);
    const query = new URLSearchParams(item.href.split('?')[1]);
    assert.equal(query.get('at'), null);
    assert.equal(query.get('from'), fromUtc);
    assert.equal(query.get('to'), toUtc);
  }
});

test('outside-window backup findings keep the real latest assessment anchor while remaining uncorrelated with historical changes', () => {
  const assessed = '2026-09-27T10:00:00.654321Z';
  const result = summarize({ issues: [issue({ title: 'Backup faults observed', destination: 'operations', observedAtUtc: assessed }),
    issue({ title: 'Backup policy checks failed', destination: 'operations', observedAtUtc: assessed })], events: [configuration()] });
  for (const item of result.items) {
    assert.equal(item.windowRelation, 'outside_window');
    assert.equal(item.timeUtc, assessed);
    assert.equal(item.contexts.length, 0);
    const query = new URLSearchParams(item.href.split('?')[1]);
    assert.equal(query.get('to'), assessed);
    assert.equal(query.get('from'), '2026-09-27T09:00:00.654Z');
    assert.equal(query.get('databaseId'), '5');
    assert.equal(query.get('compare'), '1');
  }
});

test('foreign targets, databases, windows and contradictory event/link identities cannot become findings or related context', () => {
  const original = plan();
  const invalid = [
    plan({ href: original.href.replace(targetId, otherTarget) }),
    plan({ href: original.href.replace(encodeURIComponent(toUtc), encodeURIComponent('2026-09-26T12:00:00.123457Z')) }),
    plan({ href: `${original.href}&databaseId=5` }),
    plan({ href: `${original.href}&queryFingerprint=${'b'.repeat(64)}` }),
    plan({ href: original.href.replace('compare=1', 'compare=0') }),
    plan({ href: original.href.replace('source=query_store', 'source=plan_cache') }),
    plan({ id: `plan:5:${'a'.repeat(64)}:${'c'.repeat(64)}` }),
    plan({}, 6),
    configuration({ href: configuration().href.replace('databaseId=5', 'databaseId=6') }),
    configuration({ href: configuration().href.replace(run, 'abcdefab-abcd-4abc-8abc-abcdefabcdee') }),
    configuration({ href: `${configuration().href}&configurationRun=${run}` }),
    options({}, 6),
    options({ id: 'database-options:------------------------------------:5' }),
    configuration({ timeBasis: 'occurred' }),
  ];
  const result = summarize({ events: invalid, issues: [issue({ targetId: otherTarget })] });
  assert.deepEqual(result.items, []);
  assert.equal(result.rejectedEvidence, invalid.length + 1);
});

test('all-database plan context excludes options of another database while retaining server-wide configuration', () => {
  const allScope = { ...scope, databaseId: undefined };
  const result = summarize({ scope: allScope, events: [plan(), options({}, 6), configuration({ href: `${overviewHref(allScope, 'health')}&healthSection=summary&configurationRun=${run}` })] });
  assert.equal(result.items.length, 1);
  assert.deepEqual(result.items[0].contexts.map(context => context.event.kind), ['configuration']);
});

test('first stable identities win and bounds stay five findings and three nearest change observations', () => {
  const plans = Array.from({ length: 8 }, (_, index) => {
    const query = (index + 1).toString(16).repeat(64);
    return plan({ id: `plan:5:${query}:${'c'.repeat(64)}`, href: queryJumpHref(scope, targetId, 5, query, 'c'.repeat(64), 'query_store') });
  });
  const changes = Array.from({ length: 5 }, (_, index) => configuration({ timeUtc: `2026-09-26T09:${59 - index}:00.123456Z` }, `abcdefab-abcd-4abc-8abc-abcdefabcde${index}`));
  const result = summarize({ issues: [issue(), issue({ detail: 'duplicate must not replace original' })], events: [...plans, ...changes, plan({ ...plans[0], id: plans[0].id.toUpperCase(), detail: 'duplicate uppercase' })] });
  assert.equal(result.totalFindings, 9);
  assert.equal(result.items.length, 5);
  assert.equal(result.omittedFindings, 4);
  assert.equal(result.rejectedEvidence, 1);
  assert.equal(result.items[0].detail, plans[0].detail);
  assert.equal(result.items[0].contexts.length, 3);
  assert.deepEqual(result.items[0].contexts.map(context => context.event.timeUtc), changes.slice(0, 3).map(change => change.timeUtc));
});

test('UTC precision normalizes only a .NET trailing seventh zero and rejects malformed metadata', () => {
  assert.equal(summarize({ events: [plan({ timeUtc: '2026-09-26T10:00:00.1234560Z' })] }).items.length, 1);
  const invalid = [plan({ timeUtc: '2026-09-26T10:00:00.1234561Z' }), plan({ timeUtc: '2026-02-30T10:00:00Z' }), plan({ timeUtc: '2026-09-26T10:00:00+01:00' }), plan({ detail: null }), null];
  assert.equal(summarize({ events: invalid }).items.length, 0);
  assert.equal(summarize({ issues: [issue({ observedAtUtc: 'bad' }), issue({ priority: NaN }), issue({ destination: 'javascript:alert(1)' })] }).items.length, 0);
});

test('contradictory global context or oversized input fails explicitly rather than emitting an all-clear summary', () => {
  for (const extra of [{ targetId: otherTarget }, { toUtc: fromUtc }, { scope: { ...scope, from: '2026-09-26T08:00:00.123457Z' } },
    { scope: { ...scope, databaseId: 0 } }, { events: Array(121).fill(plan()) }, { issues: Array(101).fill(issue()) }])
    assert.throws(() => summarize(extra), /does not match/);
});
