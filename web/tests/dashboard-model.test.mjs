import test from 'node:test';
import assert from 'node:assert/strict';
import { readRoute, routeHref, formatAddress } from '../src/dashboardModel.ts';
test('hash routes preserve target identity and reject unknown destinations', () => {
  assert.deepEqual(readRoute(routeHref('queries', 'a b')), {page:'queries', target:'a b'});
  assert.deepEqual(readRoute('#/not-a-page'), {page:'overview',target:''});
  assert.deepEqual(readRoute('#/alerts?target=target-2'), {page:'alerts',target:'target-2'});
  assert.deepEqual(readRoute('#/constructor'), {page:'overview',target:''});
});
test('nullable target addresses never invent a named instance or default port', () => {
  assert.equal(formatAddress({host:'lab',namedInstance:null,tcpPort:1433}), 'lab:1433');
  assert.equal(formatAddress({host:'lab',namedInstance:'SQL'}), 'lab\\SQL');
  assert.equal(formatAddress({host:'lab'}), 'lab:unavailable');
});
