import test from 'node:test';
import assert from 'node:assert/strict';
import { activityHistoryHref, readRoute, routeHref, formatAddress } from '../src/dashboardModel.ts';
test('hash routes preserve target identity and reject unknown destinations', () => {
  assert.deepEqual(readRoute(routeHref('queries', 'a b')), {page:'queries', target:'a b'});
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
test('nullable target addresses never invent a named instance or default port', () => {
  assert.equal(formatAddress({host:'lab',namedInstance:null,tcpPort:1433}), 'lab:1433');
  assert.equal(formatAddress({host:'lab',namedInstance:'SQL'}), 'lab\\SQL');
  assert.equal(formatAddress({host:'lab'}), 'lab:unavailable');
});
