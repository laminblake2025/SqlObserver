import test from 'node:test';
import assert from 'node:assert/strict';
import { alertJumpHref, alertSelectionFromHash, matchingAlerts } from '../src/components/commandPaletteModel.ts';
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
