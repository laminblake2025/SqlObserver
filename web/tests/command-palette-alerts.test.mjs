import test from 'node:test';
import assert from 'node:assert/strict';
import { alertJumpHref, alertSelectionFromHash, databaseJumpHref, matchingAlerts, matchingDatabases, queryJumpHref, targetAlertHref } from '../src/components/commandPaletteModel.ts';
import { readRoute } from '../src/dashboardModel.ts';
import { loadFleetAlertJump } from '../src/features/alerts/fleetAlertJump.ts';

const scope = { target: 'server-prior', range: 'custom', from: '2026-09-24T00:00:00Z', to: '2026-09-25T00:00:00Z', compare: true };
const targetA = '11111111-1111-4111-8111-000000000001', targetB = '11111111-1111-4111-8111-000000000002';
const alertA = '44444444-4444-4444-8444-444444444444', alertB = '66666666-6666-4666-8666-666666666666';
const snapshotUtc = '2026-09-30T12:00:00.000Z';
const alerts = [
  { targetId: targetA, alertId: alertA, targetName: 'Payments', ruleName: 'High connections', state: 'firing' },
  { targetId: targetB, alertId: alertB, targetName: 'Warehouse', ruleName: 'Collector health', state: 'acknowledged' },
];

test('alert jump preserves the shared time range and selects an exact fleet alert', () => {
  const href = alertJumpHref(scope, alerts[0]);
  assert.match(href, /^#\/alerts\?/);
  assert.equal(new URLSearchParams(href.split('?')[1]).get('target'), null);
  assert.deepEqual(alertSelectionFromHash(href), { targetId: targetA, alertId: alertA });
  assert.equal(new URLSearchParams(href.split('?')[1]).get('range'), 'custom');
  assert.equal(new URLSearchParams(href.split('?')[1]).get('compare'), '1');
  assert.equal(alertSelectionFromHash(`#/alerts?alert=${alertA}`), undefined);
});

test('alert URL selection is restricted to alerts, normalized GUIDs and consistent unambiguous target identity', () => {
  const upperTarget = 'ABCDEFAB-ABCD-4ABC-8ABC-ABCDEFABCDEF', upperAlert = 'FEDCBAFE-FEDC-4FED-8FED-FEDCBAFEDCBA';
  const expected = { targetId: upperTarget.toLowerCase(), alertId: upperAlert.toLowerCase() };
  assert.deepEqual(alertSelectionFromHash(`#/alerts?alertTarget=${upperTarget}&alert=${upperAlert}`), expected);
  assert.deepEqual(alertSelectionFromHash(`#/alerts?target=${upperTarget}&alert=${upperAlert}`), expected);
  assert.deepEqual(alertSelectionFromHash(`#/alerts?target=${upperTarget}&alertTarget=${upperTarget.toLowerCase()}&alert=${upperAlert}`), expected);
  for (const href of [`#/health?alertTarget=${targetA}&alert=${alertA}`, `#/queries?target=${targetA}&alert=${alertA}`, `#/alerts?target=not-a-guid&alert=${alertA}`, `#/alerts?alertTarget=${targetA}&alert=invalid`, `#/alerts?target=${targetA}&alertTarget=${targetB}&alert=${alertA}`, `#/alerts?target=${targetA}&target=${targetA}&alert=${alertA}`, `#/alerts?alertTarget=${targetA}&alertTarget=${targetA}&alert=${alertA}`, `#/alerts?target=${targetA}&alert=${alertA}&alert=${alertA}`]) assert.equal(alertSelectionFromHash(href), undefined);
});

test('target alert links preserve same-server database and time context but clear database on another server', () => {
  const selected = { ...scope, target: targetA, databaseId: 6 };
  const href = targetAlertHref(selected, targetA, alertA);
  assert.deepEqual(alertSelectionFromHash(href), { targetId: targetA, alertId: alertA });
  const query = new URLSearchParams(href.split('?')[1]);
  assert.equal(query.get('databaseId'), '6');
  assert.equal(query.get('from'), scope.from);
  assert.equal(query.get('to'), scope.to);
  assert.equal(query.get('compare'), '1');
  assert.equal(query.get('alertTarget'), null);
  assert.equal(new URLSearchParams(targetAlertHref(selected, targetB, alertB).split('?')[1]).get('databaseId'), null);
});

test('alert search matches authorized loaded rows by server, rule, target or alert ID', () => {
  assert.deepEqual(matchingAlerts(alerts, '  WAREhouse '), [alerts[1]]);
  assert.deepEqual(matchingAlerts(alerts, 'connections'), [alerts[0]]);
  assert.deepEqual(matchingAlerts(alerts, alertB), [alerts[1]]);
  assert.deepEqual(matchingAlerts(alerts, targetA), [alerts[0]]);
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
    { items: [alerts[0]], nextCursor: 'page-2', snapshotUtc },
    { items: [alerts[0], alerts[1]], nextCursor: 'page-3', snapshotUtc },
  ];
  const page = await loadFleetAlertJump(async cursor => {
    cursors.push(cursor);
    return pages[cursors.length - 1];
  }, `${targetB}:${alertB}`);
  assert.deepEqual(cursors, [undefined, 'page-2']);
  assert.deepEqual(page.items, alerts);
  assert.equal(page.found, true);
  assert.equal(page.nextCursor, 'page-3');
  assert.equal(page.snapshotUtc, snapshotUtc);
});

test('an alert jump stops at its page limit without selecting an unrelated alert', async () => {
  let calls = 0;
  const page = await loadFleetAlertJump(async () => {
    calls += 1;
    return { items: [alerts[0]], nextCursor: `page-${calls + 1}`, snapshotUtc };
  }, `${targetB}:${alertB}`, 2);
  assert.equal(calls, 2);
  assert.equal(page.found, false);
  assert.equal(page.nextCursor, 'page-3');
});

test('fleet alert restore deduplicates case-insensitive identity and rejects mixed snapshots or cursor loops', async () => {
  const uppercase = { ...alerts[0], targetId: 'ABCDEFAB-ABCD-4ABC-8ABC-ABCDEFABCDEF', alertId: 'FEDCBAFE-FEDC-4FED-8FED-FEDCBAFEDCBA' };
  const pages = [{ items: [uppercase], nextCursor: 'next', snapshotUtc }, { items: [{ ...uppercase, targetId: uppercase.targetId.toLowerCase(), alertId: uppercase.alertId.toLowerCase() }, alerts[1]], snapshotUtc }];
  let index = 0;
  const page = await loadFleetAlertJump(async () => pages[index++], `${targetB}:${alertB}`);
  assert.equal(page.items.length, 2);
  assert.equal(page.items[0], uppercase);
  assert.equal((await loadFleetAlertJump(async () => ({ items: [uppercase], snapshotUtc }), `${uppercase.targetId.toLowerCase()}:${uppercase.alertId.toLowerCase()}`)).found, true);
  let calls = 0;
  await assert.rejects(loadFleetAlertJump(async () => ({ items: [], nextCursor: 'same', snapshotUtc: calls++ ? '2026-09-30T12:01:00Z' : snapshotUtc }), `${targetB}:${alertB}`), /snapshot changed/);
  await assert.rejects(loadFleetAlertJump(async () => ({ items: [], nextCursor: 'same', snapshotUtc }), `${targetB}:${alertB}`), /repeated a cursor/);
  await assert.rejects(loadFleetAlertJump(async () => ({ items: Array(101).fill(alerts[0]), snapshotUtc }), undefined), /row limit/);
});
