import test from 'node:test';
import assert from 'node:assert/strict';
import { timelineEventsInWindow, planRegressionTimelineEvents, databaseOptionTimelineEvents, configurationTimelineEvents, supportsChangeTimelineWindow } from '../src/components/dashboardTimelineModel.ts';
import { readRoute, healthSectionHref } from '../src/dashboardModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
const selected = { target, range: 'custom', from, to, compare: true, databaseId: 6 };
const event = (id, timeUtc = '2026-09-26T10:00:00.000Z', extra = {}) => ({ id, timeUtc, kind: 'deadlock', timeBasis: 'occurred', title: 'Synthetic deadlock', detail: 'Loaded evidence', href: '#/deadlocks', ...extra });
const row = (databaseId = 6, extra = {}) => ({ databaseId, queryFingerprint: 'b'.repeat(64), previousPlanFingerprint: 'c'.repeat(64), currentPlanFingerprint: 'd'.repeat(64), previousAverageMilliseconds: '10.000', currentAverageMilliseconds: '40.000', slowdownRatio: '4.000', previousExecutions: '20', currentExecutions: '20', previousComparableRows: 2, currentComparableRows: 3, previousSourceRows: 2, currentSourceRows: 3, lastObservedAtUtc: '2026-09-26T10:00:00.000Z', ...extra });
const response = (items = [row()], extra = {}) => ({ fromUtc: from, toUtc: to, comparisonFromUtc: '2026-09-26T04:00:00.000Z', coverage: 'observed_groups_only', items, ...extra });

test('health section routes validate tab identity while retaining database and fixed investigation scope', () => {
  for (const section of ['summary', 'databases', 'collection']) {
    const href = healthSectionHref(selected, target, section);
    assert.deepEqual(readRoute(href), { page: 'health', target, databaseId: 6, healthSection: section });
    const query = new URLSearchParams(href.split('?')[1]);
    assert.equal(query.get('range'), 'custom');
    assert.equal(query.get('from'), from);
    assert.equal(query.get('to'), to);
    assert.equal(query.get('compare'), '1');
  }
  for (const section of ['', 'invalid', 'Summary', 'constructor']) assert.deepEqual(readRoute(`${healthSectionHref(selected, target, 'summary').split('&healthSection=')[0]}&healthSection=${section}`), { page: 'health', target, databaseId: 6 });
  assert.deepEqual(readRoute(`#/activity?target=${target}&databaseId=6&healthSection=summary`), { page: 'activity', target, databaseId: 6 });
  assert.equal(readRoute(healthSectionHref(selected, '11111111-1111-4111-8111-000000000002', 'summary')).databaseId, undefined);
});

test('change timeline window supports exactly seven days without rounding a one-microsecond overflow', () => {
  const start = '2026-09-26T08:00:00.123456Z';
  assert.equal(supportsChangeTimelineWindow(start, '2026-10-03T08:00:00.123456Z'), true);
  assert.equal(supportsChangeTimelineWindow(start, '2026-10-03T08:00:00.123457Z'), false);
  assert.equal(supportsChangeTimelineWindow(start, '2026-10-03T08:00:00.123455Z'), true);
  assert.equal(supportsChangeTimelineWindow('2026-09-26T08:00:00.1234560Z', '2026-10-03T08:00:00.1234560Z'), true);
  for (const [start, end] of [[from, from], [to, from], ['invalid', to], [from, '2026-02-30T10:00:00Z']]) assert.equal(supportsChangeTimelineWindow(start, end), false);
});

test('configuration run routes normalize a UUID only for the server Summary section and preserve investigation context', () => {
  const runId = 'ABCDEFAB-ABCD-4ABC-8ABC-ABCDEFABCDEF';
  const normalized = runId.toLowerCase();
  const href = healthSectionHref(selected, target, 'summary', runId);
  assert.deepEqual(readRoute(href), { page: 'health', target, databaseId: 6, healthSection: 'summary', configurationRunId: normalized });
  const query = new URLSearchParams(href.split('?')[1]);
  assert.equal(query.get('configurationRun'), normalized);
  assert.equal(query.get('range'), 'custom');
  assert.equal(query.get('from'), from);
  assert.equal(query.get('to'), to);
  assert.equal(query.get('compare'), '1');
  for (const invalid of ['', 'not-a-uuid', 'abcdefab-abcd-4abc-8abc-abcdefabcde']) assert.equal(readRoute(`${healthSectionHref(selected, target, 'summary')}&configurationRun=${invalid}`).configurationRunId, undefined);
  for (const section of ['databases', 'collection']) {
    assert.equal(new URLSearchParams(healthSectionHref(selected, target, section, runId).split('?')[1]).get('configurationRun'), null);
    assert.equal(readRoute(`${healthSectionHref(selected, target, section)}&configurationRun=${runId}`).configurationRunId, undefined);
  }
  assert.equal(readRoute(`#/activity?target=${target}&healthSection=summary&configurationRun=${runId}`).configurationRunId, undefined);
  assert.equal(readRoute(`#/health?target=${target}&configurationRun=${runId}`).configurationRunId, undefined);
});

test('timeline windows include the start and exclude the end at microsecond precision', () => {
  const events = [event('before', '2026-09-26T07:59:59.999999Z'), event('start', from), event('last', '2026-09-26T11:59:59.999999Z'), event('end', to)];
  assert.deepEqual(timelineEventsInWindow(events, from, to).map(item => item.id), ['start', 'last']);
});

test('timeline deduplicates the first valid identity and deterministically orders equal times', () => {
  const events = [event('a', 'not-a-time'), event('z'), event('a'), event('a', '2026-09-26T11:00:00Z'), event('early', '2026-09-26T09:00:00Z')];
  const loaded = timelineEventsInWindow(events, from, to);
  assert.deepEqual(loaded.map(item => item.id), ['early', 'a', 'z']);
  assert.equal(loaded[1], events[2]);
});

test('timeline retains the latest 120 loaded events in chronological order', () => {
  const events = Array.from({ length: 130 }, (_, index) => event(`event-${index}`, new Date(Date.parse(from) + index * 60_000).toISOString())).reverse();
  const loaded = timelineEventsInWindow(events, from, to);
  assert.equal(loaded.length, 120);
  assert.equal(loaded[0].id, 'event-10');
  assert.equal(loaded.at(-1).id, 'event-129');
});

test('timeline rejects malformed timestamps, unsupported metadata and unsafe external evidence links', () => {
  const invalid = [event('date', '2026-02-30T10:00:00Z'), event('offset', '2026-09-26T10:00:00+01:00'), event('local', '2026-09-26T10:00:00'), event('', from), event('kind', from, { kind: 'invented' }), event('basis', from, { timeBasis: 'plan_changed' }), event('title', from, { title: '' }), event('href', from, { href: 'javascript:alert(1)' })];
  assert.deepEqual(timelineEventsInWindow(invalid, from, to), []);
  for (const [start, end] of [[to, from], [from, from], ['invalid', to], [from, 'invalid']]) assert.deepEqual(timelineEventsInWindow([event('valid')], start, end), []);
});

test('plan comparison links preserve fixed windows, database and comparison while selecting the current Query Store plan', () => {
  const [mapped] = planRegressionTimelineEvents(target, selected, response());
  assert.equal(mapped.kind, 'plan_regression');
  assert.equal(mapped.timeBasis, 'last_observed');
  assert.equal(mapped.timeUtc, row().lastObservedAtUtc);
  assert.match(mapped.title, /Plan regression comparison/);
  assert.match(mapped.detail, /does not establish when the plan changed/);
  const query = new URLSearchParams(mapped.href.split('?')[1]);
  assert.equal(query.get('target'), target);
  assert.equal(query.get('databaseId'), '6');
  assert.equal(query.get('queryFingerprint'), row().queryFingerprint);
  assert.equal(query.get('planFingerprint'), row().currentPlanFingerprint);
  assert.equal(query.get('source'), 'query_store');
  assert.equal(query.get('queryView'), 'regressed');
  assert.equal(query.get('queryMetric'), 'duration');
  assert.equal(query.get('compare'), '1');
  assert.equal(query.get('range'), 'custom');
  assert.equal(query.get('from'), from);
  assert.equal(query.get('to'), to);
});

test('plan comparisons scope databases and fix moving windows to the actual API evidence window', () => {
  assert.deepEqual(planRegressionTimelineEvents(target, selected, response([row(5), row(6)])).map(item => item.id.split(':')[1]), ['6']);
  const moving = { target, range: '1h', compare: false };
  const mapped = planRegressionTimelineEvents(target, moving, response([row(5), row(6)]));
  assert.equal(mapped.length, 2);
  assert.equal(new URLSearchParams(mapped[0].href.split('?')[1]).get('from'), from);
  assert.equal(planRegressionTimelineEvents(target, moving, response([row(5), row(6)]), 5).length, 1);
});

test('plan comparison rejects stale target, changed window and unequal or oversized prior periods', () => {
  for (const [scope, result] of [[{ ...selected, target: 'another-server' }, response()], [{ ...selected, from: '2026-09-26T09:00:00Z' }, response()], [selected, response([], { comparisonFromUtc: '2026-09-26T03:00:00Z' })], [selected, response([], { fromUtc: 'invalid' })], [{ target, range: '1h' }, response([], { fromUtc: '2026-09-01T00:00:00Z', comparisonFromUtc: '2026-08-06T12:00:00Z' })]]) assert.deepEqual(planRegressionTimelineEvents(target, scope, result), []);
});

test('plan comparison drops malformed identities and timestamps without inventing plan change times', () => {
  const invalid = [row(0), row(32768), row(6, { queryFingerprint: 'short' }), row(6, { currentPlanFingerprint: 'c'.repeat(64) }), row(6, { lastObservedAtUtc: to }), row(6, { lastObservedAtUtc: '2026-02-30T10:00:00Z' }), row(6, { lastObservedAtUtc: '2026-09-26T10:00:00' })];
  assert.deepEqual(planRegressionTimelineEvents(target, selected, response(invalid)), []);
});

const run = '22222222-2222-4222-8222-222222222222';
const observations = { previousObservedAtUtc: '2026-09-26T07:59:00Z', observedAtUtc: '2026-09-26T09:00:00Z', detectedAtUtc: '2026-09-26T09:00:01Z' };
const option = (databaseId = 6, extra = {}) => ({ ...observations, databaseId, databaseName: databaseId === 6 ? 'Warehouse' : 'Orders', optionKey: 'compatibility_level', previousValue: '150', currentValue: '160', collectionRunId: run, ...extra });
const optionPage = (items = [option()], extra = {}) => ({ targetId: target, fromUtc: from, toUtc: to, semantics: 'detected_between_consecutive_complete_inventory_runs', items, ...extra });
const configuration = (extra = {}) => ({ ...observations, configurationId: 1539, configurationName: 'cost threshold for parallelism', sourceType: 'int', previousConfiguredValue: '5', configuredValue: '50', previousInUseValue: '5', inUseValue: '50', configuredChanged: true, inUseChanged: true, runId: run, ...extra });
const configurationPage = (items = [configuration()], extra = {}) => ({ targetId: target, fromUtc: from, toUtc: to, targetRevision: 1, publicationGeneration: 1, coverage: 'observed_changes_only', nextCursor: null, items, ...extra });

test('database option observations group by run and database with detected-time evidence and bounded database links', () => {
  const loaded = databaseOptionTimelineEvents(target, selected, optionPage([option(5), option(), option(6, { optionKey: 'is_read_only', previousValue: 'false', currentValue: 'true', detectedAtUtc: '2026-09-26T09:00:02Z' })]));
  assert.equal(loaded.length, 1);
  assert.equal(loaded[0].timeBasis, 'detected');
  assert.equal(loaded[0].timeUtc, '2026-09-26T09:00:02Z');
  assert.match(loaded[0].detail, /compatibility level: 150/);
  assert.match(loaded[0].detail, /is read only: false/);
  assert.match(loaded[0].detail, /actual change time is unknown/);
  const query = new URLSearchParams(loaded[0].href.split('?')[1]);
  assert.equal(query.get('databaseId'), '6');
  assert.equal(query.get('from'), from);
  assert.equal(query.get('to'), to);
  assert.equal(query.get('compare'), '1');
  assert.equal(databaseOptionTimelineEvents(target, { ...selected, databaseId: undefined }, optionPage([option(5), option()])).length, 2);
});

test('configuration observations stay server-wide, group a scan, and link to its fixed server summary', () => {
  const loaded = configurationTimelineEvents(target, selected, configurationPage([configuration(), configuration({ configurationId: 1544, configurationName: 'max degree of parallelism', configuredValue: '8', inUseValue: '8' })]), 1);
  assert.equal(loaded.length, 1);
  assert.equal(loaded[0].kind, 'configuration');
  assert.equal(loaded[0].timeBasis, 'detected');
  assert.match(loaded[0].detail, /Server-wide evidence/);
  assert.match(loaded[0].detail, /cost threshold.*max degree/);
  assert.match(loaded[0].detail, /actual change time is unknown/);
  const query = new URLSearchParams(loaded[0].href.split('?')[1]);
  assert.equal(query.get('target'), target);
  assert.equal(query.get('databaseId'), '6');
  assert.equal(query.get('healthSection'), 'summary');
  assert.equal(query.get('from'), from);
  assert.equal(query.get('to'), to);
});

test('change adapters reject foreign targets, stale exact windows and invalid page metadata', () => {
  for (const extra of [{ targetId: 'another-target' }, { fromUtc: '2026-09-26T09:00:00Z' }, { fromUtc: 'invalid' }, { toUtc: from }]) {
    assert.throws(() => databaseOptionTimelineEvents(target, selected, optionPage(undefined, extra)));
    assert.throws(() => configurationTimelineEvents(target, selected, configurationPage(undefined, extra), 1));
  }
  assert.throws(() => databaseOptionTimelineEvents(target, selected, optionPage([], { semantics: 'observed_changes_only' })));
  for (const extra of [{ targetRevision: 2 }, { publicationGeneration: -1 }, { publicationGeneration: 0 }, { nextCursor: 'invalid cursor' }]) assert.throws(() => configurationTimelineEvents(target, selected, configurationPage(undefined, extra), 1));
  assert.deepEqual(configurationTimelineEvents(target, selected, configurationPage([], { publicationGeneration: 0, coverage: 'never_collected' }), 1), []);
});

test('change adapters reject duplicate change identities, invalid rows and detection outside the window', () => {
  assert.throws(() => databaseOptionTimelineEvents(target, selected, optionPage([option(), option()])));
  assert.throws(() => configurationTimelineEvents(target, selected, configurationPage([configuration(), configuration()]), 1));
  for (const extra of [{ databaseId: 0 }, { databaseName: '' }, { optionKey: 'unknown' }, { currentValue: '150' }, { detectedAtUtc: to }, { observedAtUtc: '2026-02-30T09:00:00Z' }]) assert.throws(() => databaseOptionTimelineEvents(target, selected, optionPage([option(6, extra)])));
  for (const extra of [{ configurationId: 0 }, { configurationName: '' }, { configuredChanged: false }, { sourceType: 'unsafe type' }, { detectedAtUtc: to }]) assert.throws(() => configurationTimelineEvents(target, selected, configurationPage([configuration(extra)]), 1));
});

test('change adapters retain latest twenty groups and accept seventh fractional zero without rounding nonzero precision', () => {
  const options = Array.from({ length: 25 }, (_, index) => option(6, { collectionRunId: `22222222-2222-4222-8222-${String(index + 1).padStart(12, '0')}`, detectedAtUtc: new Date(Date.parse(from) + index * 60_000).toISOString() }));
  const loaded = databaseOptionTimelineEvents(target, selected, optionPage(options.reverse()));
  assert.equal(loaded.length, 20);
  assert.equal(loaded[0].timeUtc, '2026-09-26T08:05:00.000Z');
  assert.equal(loaded.at(-1).timeUtc, '2026-09-26T08:24:00.000Z');
  assert.equal(databaseOptionTimelineEvents(target, selected, optionPage([option(6, { detectedAtUtc: '2026-09-26T09:00:01.1234560Z' })])).length, 1);
  assert.throws(() => databaseOptionTimelineEvents(target, selected, optionPage([option(6, { detectedAtUtc: '2026-09-26T09:00:01.1234567Z' })])));
  assert.deepEqual(timelineEventsInWindow([null, event(42), event('null-href', from, { href: null }), event('valid')], from, to).map(item => item.id), ['valid']);
});
