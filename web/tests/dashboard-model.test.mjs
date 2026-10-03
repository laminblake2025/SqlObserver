import test from 'node:test';
import assert from 'node:assert/strict';
import { activityHistoryHref, readRoute, routeHref, formatAddress, invalidQuerySelectionMessage } from '../src/dashboardModel.ts';
import { readQueryViewSelection } from '../src/features/queries/queryViewModel.ts';
test('hash routes preserve target identity and reject unknown destinations', () => {
  assert.deepEqual(readRoute(routeHref('queries', 'a b')), {page:'queries', target:'a b'});
  assert.deepEqual(readRoute(routeHref('waits', 'a b')), {page:'waits', target:'a b'});
  assert.deepEqual(readRoute(routeHref('resources', 'a b')), {page:'resources', target:'a b'});
  assert.deepEqual(readRoute(routeHref('rules', 'a b')), {page:'rules', target:'a b'});
  assert.deepEqual(readRoute(routeHref('maintenance', 'a b')), {page:'maintenance', target:'a b'});
  assert.deepEqual(readRoute(routeHref('destinations', 'a b')), {page:'destinations', target:'a b'});
  assert.deepEqual(readRoute(routeHref('permissions', 'a b')), {page:'permissions', target:'a b'});
  assert.deepEqual(readRoute('#/not-a-page'), {page:'overview',target:''});
  assert.deepEqual(readRoute('#/alerts?target=target-2'), {page:'alerts',target:'target-2'});
  assert.deepEqual(readRoute('#/constructor'), {page:'overview',target:''});
});
test('deadlock activity links preserve the target and exact UTC event time', () => {
  const occurredAtUtc = '2026-08-24T00:00:00.1234567Z';
  const eventId = 'f321555e-329f-421b-485c-cf796aa0f475';
  const href = activityHistoryHref('target-2', occurredAtUtc, eventId);
  assert.deepEqual(readRoute(href), {page:'activity', target:'target-2', activityAtUtc:occurredAtUtc, activityEventId:eventId});
  assert.deepEqual(readRoute('#/activity?target=target-2&at=not-a-timestamp'), {page:'activity', target:'target-2'});
});
test('deadlock activity links carry the selected custom investigation window', () => {
  const scope = {target:'target-2',range:'custom',from:'2026-09-21T01:00:00.000Z',to:'2026-09-21T03:00:00.000Z',compare:true};
  const href = activityHistoryHref('target-2','2026-09-21T02:00:00.000Z',undefined,scope);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.equal(params.get('range'),'custom');
  assert.equal(params.get('from'),scope.from);
  assert.equal(params.get('to'),scope.to);
  assert.equal(params.get('compare'),'1');
  assert.equal(readRoute(href).activityAtUtc,'2026-09-21T02:00:00.000Z');
});

test('native zero-offset deadlock links restore Activity with exact fractional event time', () => {
  const eventId = 'f321555e-329f-421b-485c-cf796aa0f475';
  for (const fraction of ['123456', '1234560']) {
    const at = `2026-08-24T00:00:00.${fraction}+00:00`;
    assert.deepEqual(readRoute(activityHistoryHref('target-2', at, eventId)), {
      page: 'activity', target: 'target-2', activityAtUtc: at.replace('+00:00', 'Z'), activityEventId: eventId,
    });
  }
});
test('nullable target addresses never invent a named instance or default port', () => {
  assert.equal(formatAddress({host:'lab',namedInstance:null,tcpPort:1433}), 'lab:1433');
  assert.equal(formatAddress({host:'lab',namedInstance:'SQL'}), 'lab\\SQL');
  assert.equal(formatAddress({host:'lab'}), 'lab:unavailable');
});

const queryTarget = 'abcdefab-abcd-4abc-8abc-abcdefabcdef';
const queryId = 'a'.repeat(64), planId = 'b'.repeat(64);
const queryHref = `#/queries?target=${queryTarget}&databaseId=6&queryFingerprint=${queryId}`;

test('exact query URLs normalize native identities and keep ranking and selected sources independent', () => {
  assert.deepEqual(readRoute(`#/queries?target=${queryTarget.toUpperCase()}&databaseId=6&queryFingerprint=${queryId.toUpperCase()}&planFingerprint=${planId.toUpperCase()}&source=query_store&querySource=plan_cache`),
    { page: 'queries', target: queryTarget, databaseId: 6, queryFingerprint: queryId, planFingerprint: planId, querySource: 'query_store' });
  assert.deepEqual(readRoute(`${queryHref}&source=plan_cache&querySource=query_store`),
    { page: 'queries', target: queryTarget, databaseId: 6, queryFingerprint: queryId, querySource: 'plan_cache' });
  assert.deepEqual(readRoute(`${queryHref}&planFingerprint=${planId}`),
    { page: 'queries', target: queryTarget, databaseId: 6, queryFingerprint: queryId, planFingerprint: planId });
  assert.equal(readQueryViewSelection(`${queryHref}&source=plan_cache&querySource=query_store`).sourceFilter, 'query_store');
  assert.equal(readRoute(`${queryHref}&querySource=invalid`).querySelectionInvalid, undefined,
    'An invalid ranking filter is represented by its own model, not the exact query selection.');
});

test('duplicate, malformed and contradictory exact query parameters refuse automatic selection', () => {
  for (const suffix of [`&queryFingerprint=${queryId}`, '&queryFingerprint=short', '&planFingerprint=',
    '&planFingerprint=invalid', `&planFingerprint=${planId}&planFingerprint=${planId}`,
    '&source=query_store&source=query_store', '&source=mixed', '&source=',
    `&planFingerprint=${planId}&source=plan_cache`]) {
    const route = readRoute(`${queryHref}${suffix}`);
    assert.equal(route.querySelectionInvalid, true, suffix);
    assert.equal(route.queryFingerprint, undefined, suffix);
    assert.equal(route.planFingerprint, undefined, suffix);
    assert.equal(route.querySource, undefined, suffix);
  }
  for (const value of ['', 'short', 'g'.repeat(64), 'a'.repeat(65), `${queryId}?ignored`])
    assert.equal(readRoute(`#/queries?target=${queryTarget}&databaseId=6&queryFingerprint=${value}`).querySelectionInvalid, true);
  assert.equal(invalidQuerySelectionMessage, 'The query selection in this URL is invalid. Select a query from the ranking.');
});

test('exact query selection requires one valid target and database binding, without selecting a first duplicate', () => {
  for (const binding of [`target=${queryTarget}`, `target=${queryTarget}&databaseId=6&databaseId=6`,
    `target=${queryTarget}&databaseId=6&databaseId=7`, `target=${queryTarget}&databaseId=0`,
    `target=${queryTarget}&databaseId=32768`, `target=${queryTarget}&databaseId=06`,
    `target=${queryTarget}&databaseId=invalid`, `target=${queryTarget}&target=${queryTarget}&databaseId=6`,
    'databaseId=6', 'target=&databaseId=6', 'target=not-a-guid&databaseId=6',
    'target=00000000-0000-0000-0000-000000000000&databaseId=6']) {
    const route = readRoute(`#/queries?${binding}&queryFingerprint=${queryId}`);
    assert.equal(route.querySelectionInvalid, true, binding);
    assert.equal(route.queryFingerprint, undefined, binding);
  }
  assert.equal(readRoute(`#/queries?target=${queryTarget}&databaseId=32767&queryFingerprint=${queryId}`).querySelectionInvalid, undefined);
});

test('orphan exact fields are invalid while ranking-only and other-screen URLs have no query refusal', () => {
  for (const field of [`planFingerprint=${planId}`, 'source=query_store'])
    assert.equal(readRoute(`#/queries?target=${queryTarget}&databaseId=6&${field}`).querySelectionInvalid, true);
  assert.deepEqual(readRoute(`#/queries?target=${queryTarget}&querySource=plan_cache&queryView=cache`),
    { page: 'queries', target: queryTarget });
  assert.equal(readRoute(`#/waits?target=${queryTarget}&databaseId=6&queryFingerprint=bad&source=bad`).querySelectionInvalid, undefined);
});
