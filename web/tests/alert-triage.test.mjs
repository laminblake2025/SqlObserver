import test from 'node:test';
import assert from 'node:assert/strict';
import { clearApiCache, ApiRequestError } from '../src/apiClient.ts';
import { getActiveAlerts, getFleetActiveAlerts, acknowledgeAlert } from '../src/features/alerts/alertApi.ts';
import { parseActiveAlerts, parseFleetAlerts } from '../src/features/alerts/alertRuntimeParser.ts';
import { validateAlertTriagePage, decodeAlertTriageCursor, alertTriageRows, maximumAlertTriageRows, maximumAlertTriagePages } from '../src/features/alerts/alertTriageModel.ts';
import { alpha, beta, inboxPage, encodeCursor, snapshotFor, alert } from '../component-tests/alertTriageFixture.mjs';
const parsed = (kind, index = 0, changes = {}) => {
  const raw = { ...inboxPage({ kind, index }), ...changes };
  return kind === 'fleet' ? parseFleetAlerts(raw) : parseActiveAlerts(raw, alpha);
};
const receipt = page => ({ page, receivedAtUtc: '2026-03-12T13:00:01Z' });
for (const kind of ['fleet', 'target']) {
  test(`${kind} validates native cutoff and tail with seventh-zero DateTimeOffset cursor`, () => {
    const first = parsed(kind); validateAlertTriagePage(first, kind, alpha);
    const cursor = decodeAlertTriageCursor(first.nextCursor, kind, alpha);
    assert.equal(cursor.alertId, first.items.at(-1).alertId);
    const second = parsed(kind, 100); validateAlertTriagePage(second, kind, alpha, [receipt(first)], first.nextCursor);
    assert.equal(alertTriageRows([receipt(first), receipt(second)], alpha).length, 200);
  });
  test(`${kind} preserves native order and rejects duplicate/out-of-order continuation`, () => {
    const first = parsed(kind), second = parsed(kind, 100);
    for (const items of [[first.items.at(-1)], [second.items[1], second.items[0]]])
      assert.throws(() => validateAlertTriagePage({ ...second, items, nextCursor: undefined }, kind, alpha, [receipt(first)], first.nextCursor));
  });
  test(`${kind} rejects cutoff movement by one microsecond, foreign tail, loops and sparse next pages`, () => {
    const first = parsed(kind);
    assert.throws(() => validateAlertTriagePage({ ...parsed(kind, 100), snapshotUtc: '2026-03-12T13:00:00.123457Z' }, kind, alpha, [receipt(first)], first.nextCursor));
    assert.throws(() => validateAlertTriagePage({ ...first, nextCursor: encodeCursor(kind, { ...alert(99), alertId: alert(98).alertId }, snapshotFor(1)) }, kind, alpha));
    assert.throws(() => validateAlertTriagePage({ ...first, items: first.items.slice(0, 1) }, kind, alpha));
    assert.throws(() => validateAlertTriagePage({ ...parsed(kind, 100), nextCursor: first.nextCursor }, kind, alpha, [receipt(first)], first.nextCursor));
  });
  test(`${kind} permits mutable acknowledged state newer than the cutoff without inventing historical state`, () => {
    const first = parsed(kind), second = parsed(kind, 100);
    validateAlertTriagePage({ ...second, items: second.items.map(item => ({ ...item, state: 'acknowledged', acknowledgedUtc: '2026-03-12T14:00:00Z' })) }, kind, alpha, [receipt(first)], first.nextCursor);
  });
}
test('target and fleet native tie orders differ and are not repaired by sorting received rows', () => {
  const at = alert(0).firedUtc, a = { ...alert(0), firedUtc: at }, b = { ...alert(1), firedUtc: at };
  validateAlertTriagePage(parsed('target', 0, { items: [a, b], nextCursor: null }), 'target', alpha);
  assert.throws(() => validateAlertTriagePage(parsed('target', 0, { items: [b, a], nextCursor: null }), 'target', alpha));
  validateAlertTriagePage(parsed('fleet', 0, { items: [b, a], nextCursor: null }), 'fleet', alpha);
  assert.throws(() => validateAlertTriagePage(parsed('fleet', 0, { items: [a, b], nextCursor: null }), 'fleet', alpha));
});
test('target identity, oversized pages, missing cutoff and invalid standard-base64 cursor are rejected', () => {
  const page = parsed('target');
  for (const changes of [{ targetId: beta }, { snapshotUtc: undefined }, { items: [...page.items, page.items[0]] }, { nextCursor: 'Y3Vyc29yMQ==' }])
    assert.throws(() => validateAlertTriagePage({ ...page, ...changes }, 'target', alpha));
  assert.equal(maximumAlertTriagePages * 100, maximumAlertTriageRows);
});
for (const status of [401, 403]) test(`all alert API paths retain HTTP ${status} for definitive caller denial`, async () => {
  const original = globalThis.fetch; clearApiCache();
  globalThis.fetch = async () => new Response('provider secret', { status, headers: { 'cache-control': 'no-store' } });
  try {
    const signal = new AbortController().signal;
    for (const read of [() => getActiveAlerts(alpha, signal), () => getFleetActiveAlerts(signal),
      () => acknowledgeAlert(alpha, alert(0).alertId, 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', signal)])
      await assert.rejects(read, failure => failure instanceof ApiRequestError && failure.status === status && !failure.message.includes('provider secret'));
  } finally { globalThis.fetch = original; clearApiCache(); }
});
