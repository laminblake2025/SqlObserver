import test from 'node:test';
import assert from 'node:assert/strict';
import { alertJumpHref, alertSelectionFromHash, databaseJumpHref, matchingAlerts, matchingDatabases, queryJumpHref } from '../src/components/commandPaletteModel.ts';
import { readRoute } from '../src/dashboardModel.ts';
import { loadFleetAlertJump } from '../src/features/alerts/fleetAlertJump.ts';

const scope = { target: 'server-prior', range: 'custom', from: '2026-09-24T00:00:00Z', to: '2026-09-25T00:00:00Z', compare: true };
const alerts = [
  { targetId: 'target-a', alertId: 'alert-a', targetName: 'Payments', ruleName: 'High connections', state: 'firing' },
  { targetId: 'target-b', alertId: 'alert-b', targetName: 'Warehouse', ruleName: 'Collector health', state: 'acknowledged' },
];

test('alert jump preserves the shared time range and selects an exact fleet alert', () => {
  const href = alertJumpHref(scope, alerts[0]);
  assert.match(href, /^#\/alerts\?/);
  assert.equal(new URLSearchParams(href.split('?')[1]).get('target'), null);
  assert.deepEqual(alertSelectionFromHash(href), { targetId: 'target-a', alertId: 'alert-a' });
  assert.equal(new URLSearchParams(href.split('?')[1]).get('range'), 'custom');
  assert.equal(new URLSearchParams(href.split('?')[1]).get('compare'), '1');
  assert.equal(alertSelectionFromHash('#/alerts?alert=alert-a'), undefined);
});

test('alert search matches authorized loaded rows by server, rule, target or alert ID', () => {
  assert.deepEqual(matchingAlerts(alerts, '  WAREhouse '), [alerts[1]]);
  assert.deepEqual(matchingAlerts(alerts, 'connections'), [alerts[0]]);
  assert.deepEqual(matchingAlerts(alerts, 'alert-b'), [alerts[1]]);
  assert.deepEqual(matchingAlerts(alerts, 'target-a'), [alerts[0]]);
  assert.deepEqual(matchingAlerts(alerts, 'missing'), []);
});

test('database jump preserves time context and selects its exact server and database', () => {
  const href = databaseJumpHref(scope, 'target-b', 42);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.deepEqual(readRoute(href), { page: 'health', target: 'target-b', databaseId: 42 });
  assert.equal(params.get('range'), 'custom');
  assert.equal(params.get('from'), scope.from);
  assert.equal(params.get('to'), scope.to);
  assert.equal(params.get('compare'), '1');
  assert.deepEqual(readRoute('#/health?target=target-b&databaseId=0'), { page: 'health', target: 'target-b' });
  assert.deepEqual(readRoute('#/health?target=target-b&databaseId=32768'), { page: 'health', target: 'target-b' });
});

test('database search matches bounded loaded names and IDs', () => {
  const databases = [{ databaseId: 5, name: 'Orders' }, { databaseId: 42, name: 'Warehouse' }];
  assert.deepEqual(matchingDatabases(databases, ' WARE '), [databases[1]]);
  assert.deepEqual(matchingDatabases(databases, '5'), [databases[0]]);
  assert.deepEqual(matchingDatabases(databases, 'missing'), []);
});

test('query hash jump preserves time context and only accepts an exact database and hash', () => {
  const hash = 'A'.repeat(64);
  const href = queryJumpHref(scope, 'target-b', 42, hash);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.deepEqual(readRoute(href), { page: 'queries', target: 'target-b', databaseId: 42, queryFingerprint: hash.toLowerCase() });
  assert.equal(params.get('range'), 'custom');
  assert.equal(params.get('from'), scope.from);
  assert.equal(params.get('to'), scope.to);
  assert.deepEqual(readRoute('#/queries?target=target-b&databaseId=42&queryFingerprint=abcd'), { page: 'queries', target: 'target-b', databaseId: 42 });
  assert.deepEqual(readRoute(`#/queries?target=target-b&databaseId=0&queryFingerprint=${hash}`), { page: 'queries', target: 'target-b' });
});

test('a Query Store plan jump preserves its exact query, plan, and time window', () => {
  const query = 'A'.repeat(64);
  const plan = 'B'.repeat(64);
  const href = queryJumpHref(scope, 'target-b', 42, query, plan);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.deepEqual(readRoute(href), {
    page: 'queries', target: 'target-b', databaseId: 42,
    queryFingerprint: query.toLowerCase(), planFingerprint: plan.toLowerCase(),
  });
  assert.equal(params.get('from'), scope.from);
  assert.equal(params.get('to'), scope.to);
  assert.deepEqual(readRoute(`#/queries?target=target-b&databaseId=42&queryFingerprint=${query}&planFingerprint=short`),
    { page: 'queries', target: 'target-b', databaseId: 42, queryFingerprint: query.toLowerCase() });
  assert.throws(() => queryJumpHref(scope, 'target-b', 42, query, 'short'));
  const selected = queryJumpHref(scope, 'target-b', 42, query, plan, 'query_store');
  assert.equal(readRoute(selected).querySource, 'query_store');
  assert.throws(() => queryJumpHref(scope, 'target-b', 42, query, plan, 'plan_cache'));
  const cache = queryJumpHref(scope, 'target-b', 42, query, undefined, 'plan_cache');
  assert.equal(readRoute(cache).querySource, 'plan_cache');
});

test('an alert jump follows bounded cursor pages and selects the exact target and alert', async () => {
  const cursors = [];
  const pages = [
    { items: [alerts[0]], nextCursor: 'page-2' },
    { items: [alerts[0], alerts[1]], nextCursor: 'page-3' },
  ];
  const page = await loadFleetAlertJump(async cursor => {
    cursors.push(cursor);
    return pages[cursors.length - 1];
  }, 'target-b:alert-b');
  assert.deepEqual(cursors, [undefined, 'page-2']);
  assert.deepEqual(page.items, alerts);
  assert.equal(page.found, true);
  assert.equal(page.nextCursor, 'page-3');
});

test('an alert jump stops at its page limit without selecting an unrelated alert', async () => {
  let calls = 0;
  const page = await loadFleetAlertJump(async () => {
    calls += 1;
    return { items: [alerts[0]], nextCursor: `page-${calls + 1}` };
  }, 'target-b:alert-b', 2);
  assert.equal(calls, 2);
  assert.equal(page.found, false);
  assert.equal(page.nextCursor, 'page-3');
});
