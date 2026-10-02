import test from 'node:test';
import assert from 'node:assert/strict';
import { readDashboardTimelineSelection, dashboardTimelineSelectionHref, matchesDashboardTimelineSelection } from '../src/components/dashboardTimelineSelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const scope = { target, range: 'custom', from: '2026-09-26T08:00:00.123456Z', to: '2026-09-26T12:00:00.654321Z', databaseId: 6, compare: true };
const selection = { eventId: 'plan:6:query:current', timeUtc: '2026-09-26T10:00:00.123456Z' };

test('timeline selection round-trips its event identity, exact window, database and comparison', () => {
  const href = dashboardTimelineSelectionHref(scope, selection);
  assert.deepEqual(readDashboardTimelineSelection(href, target.toUpperCase()), selection);
  const parameters = new URLSearchParams(href.split('?')[1]);
  assert.equal(parameters.get('from'), scope.from);
  assert.equal(parameters.get('to'), scope.to);
  assert.equal(parameters.get('databaseId'), '6');
  assert.equal(parameters.get('compare'), '1');
  assert.equal(readDashboardTimelineSelection(dashboardTimelineSelectionHref(scope)), undefined);
});

test('timeline selection binds identity and microsecond time rather than substituting a coincident event', () => {
  assert.equal(matchesDashboardTimelineSelection(selection, { id: selection.eventId, timeUtc: '2026-09-26T10:00:00.1234560Z' }), true);
  assert.equal(matchesDashboardTimelineSelection(selection, { id: 'plan:6:other', timeUtc: selection.timeUtc }), false);
  assert.equal(matchesDashboardTimelineSelection(selection, { id: selection.eventId, timeUtc: '2026-09-26T10:00:00.123457Z' }), false);
  assert.equal(matchesDashboardTimelineSelection(selection, { id: selection.eventId, timeUtc: 'invalid' }), false);
});

test('timeline selection rejects ambiguous route parameters and contradictory target or window', () => {
  const href = dashboardTimelineSelectionHref(scope, selection);
  for (const suffix of ['&target=' + target, '&timelineAt=' + selection.timeUtc, '&timelineEvent=plan:other', '&from=' + scope.from, '&databaseId=5', '&compare=0', '&event=deadlock'])
    assert.equal(readDashboardTimelineSelection(href + suffix), null, suffix);
  for (const [key, value] of [['target', 'not-a-guid'], ['range', '1h'], ['timelineEvent', 'unknown:event'], ['timelineAt', scope.to], ['timelineAt', '2026-09-26T08:00:00.123455Z'], ['from', '2026-02-30T08:00:00Z'], ['databaseId', '32768']]) {
    const parameters = new URLSearchParams(href.split('?')[1]); parameters.set(key, value);
    assert.equal(readDashboardTimelineSelection('#/health?' + parameters), null, key);
  }
  assert.equal(readDashboardTimelineSelection(href.replace('#/health?', '#/resources?')), null);
  assert.equal(readDashboardTimelineSelection(href, '11111111-1111-4111-8111-000000000002'), null);
});

test('timeline links require a fixed valid window and include its start but exclude its end', () => {
  assert.deepEqual(readDashboardTimelineSelection(dashboardTimelineSelectionHref(scope, { ...selection, timeUtc: scope.from })), { ...selection, timeUtc: scope.from });
  assert.throws(() => dashboardTimelineSelectionHref(scope, { ...selection, timeUtc: scope.to }), /inside the fixed/);
  assert.throws(() => dashboardTimelineSelectionHref({ ...scope, range: '1h' }, selection), /inside the fixed/);
  assert.throws(() => dashboardTimelineSelectionHref({ ...scope, target: '' }, selection), /authorized server/);
});
