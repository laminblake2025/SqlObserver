import test from 'node:test';
import assert from 'node:assert/strict';
import { activityHistoryHref, readRoute, routeHref, formatAddress } from '../src/dashboardModel.ts';
test('hash routes preserve target identity and reject unknown destinations', () => {
  assert.deepEqual(readRoute(routeHref('queries', 'a b')), {page:'queries', target:'a b'});
  assert.deepEqual(readRoute(routeHref('waits', 'a b')), {page:'waits', target:'a b'});
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
test('nullable target addresses never invent a named instance or default port', () => {
  assert.equal(formatAddress({host:'lab',namedInstance:null,tcpPort:1433}), 'lab:1433');
  assert.equal(formatAddress({host:'lab',namedInstance:'SQL'}), 'lab\\SQL');
  assert.equal(formatAddress({host:'lab'}), 'lab:unavailable');
});
