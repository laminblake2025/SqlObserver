import assert from 'node:assert/strict';
import test from 'node:test';
import { notificationCommand, parseAlertNotificationState, parseAlertSnoozeProjection, validNotificationReason } from '../src/features/alerts/alertSnoozeModel.ts';
import { changeAlertNotifications, getAlertNotificationState, parseAlertNotificationReceipt } from '../src/features/alerts/alertSnoozeApi.ts';
import { parseActiveAlerts } from '../src/features/alerts/alertRuntimeParser.ts';
import { alpha, alertId, episodeId, notificationStatus, notificationReceipt, notificationBackend, snoozeInbox } from '../component-tests/alertSnoozeFixture.mjs';
const operationId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const current = () => parseAlertNotificationState(notificationStatus(), alpha, alertId(0));

test('durations preserve repository microseconds and bind the exact native episode/revision/reason', () => {
  for (const [minutes, end] of [[15, '2026-09-26T13:15:00.123456Z'], [60, '2026-09-26T14:00:00.123456Z'],
    [480, '2026-09-26T21:00:00.123456Z'], [1440, '2026-09-27T13:00:00.123456Z']]) {
    assert.deepEqual(notificationCommand(current(), 'snooze', minutes, ' Approved investigation ', operationId), {
      operationId, expectedEpisodeId: episodeId, expectedRevision: 7, reason: ' Approved investigation ', endsAtUtc: end });
  }
  assert.deepEqual(notificationCommand(current(), 'unsnooze', 60, 'Investigation complete', operationId), {
    operationId, expectedEpisodeId: episodeId, expectedRevision: 7, reason: 'Investigation complete' });
});
test('safe reason validation follows native UTF8 byte, control and valid Unicode bounds', () => {
  assert.equal(validNotificationReason('é'.repeat(256)), true);
  for (const reason of ['', '  ', 'a'.repeat(513), 'é'.repeat(257), 'unsafe\nreason', '\u0085', '\ud800'])
    assert.equal(validNotificationReason(reason), false);
  assert.equal(validNotificationReason('<img src=x onerror=alert(1)>'), true, 'Markup remains inert text, not executable HTML');
});
test('current receipt verifies identities, revision, exact expiry and server-owned effective suppression', () => {
  assert.equal(current().serverNowUtc, '2026-09-26T13:00:00.123456Z');
  const valid = notificationStatus({ snoozedUntilUtc: '2026-09-26T14:00:00.1234560+00:00', snoozedAtUtc: notificationStatus().serverNowUtc,
    snoozeReason: 'Investigation', snoozedBy: 'S-1-5-21-123', snoozed: true, deliverySuppressed: true });
  assert.equal(parseAlertNotificationState(valid, alpha, alertId(0)).snoozed, true);
  for (const overrides of [{ targetId: alertId(0) }, { alertId: alpha }, { episodeId: null }, { revision: 0 },
    { state: 'critical' }, { serverNowUtc: '2026-02-30T13:00:00Z' }, { snoozedUntilUtc: notificationStatus().serverNowUtc },
    { snoozedUntilUtc: '2026-09-26T14:00:00.1234561+00:00' }, { deliverySuppressed: false }, { snoozeReason: null }])
    assert.throws(() => parseAlertNotificationState({ ...valid, ...overrides }, alpha, alertId(0)));
});
test('legacy inbox rows do not invent episode fences; new nullable native fields parse safely', () => {
  const legacy = parseActiveAlerts(snoozeInbox('target', alpha, { legacy: true }), alpha).items[0];
  assert.equal(legacy.episodeId, undefined); assert.equal(legacy.revision, undefined);
  assert.equal(parseActiveAlerts(snoozeInbox(), alpha).items[0].episodeId, episodeId);
  assert.equal(parseAlertSnoozeProjection({ episodeId: null, revision: null, snoozed: null }).episodeId, undefined);
  assert.throws(() => parseAlertSnoozeProjection({ episodeId, revision: null }));
});
test('mutation receipt verifies original immutable operation/end without claiming current effective state', () => {
  const command = notificationCommand(current(), 'snooze', 15, 'Investigation', operationId), receipt = notificationReceipt(command);
  assert.equal(parseAlertNotificationReceipt(receipt, command).endsAtUtc, command.endsAtUtc);
  for (const changes of [{ operationId: alpha }, { auditId: 'bad' }, { revision: 7 }, { endsAtUtc: null },
    { endsAtUtc: '2026-09-26T13:15:00.123457Z' }, { appliedAtUtc: 'yesterday' }])
    assert.throws(() => parseAlertNotificationReceipt({ ...receipt, ...changes }, command));
});
test('durable replay after expiry preserves original receipt and never renews snooze or changes alert state', () => {
  const backend = notificationBackend(), command = notificationCommand(current(), 'snooze', 15, 'Investigation', operationId);
  const original = backend.change(alpha, alertId(0), 'snooze', command);
  backend.setNow('2026-09-26T13:16:00.123456Z');
  assert.deepEqual(backend.change(alpha, alertId(0), 'snooze', command), original);
  const readback = backend.read(alpha, alertId(0));
  assert.equal(readback.snoozed, false); assert.equal(readback.state, 'firing'); assert.equal(backend.operations.size, 1);
});
test('native API routes serialize exact commands and verify readback separately', async () => {
  const originalFetch = globalThis.fetch, calls = [], command = notificationCommand(current(), 'snooze', 15, 'Investigation', operationId);
  globalThis.fetch = async (url, options) => { calls.push({ url: String(url), options });
    return new Response(JSON.stringify(options.method === 'POST' ? notificationReceipt(command) : notificationStatus()), { headers: { 'content-type': 'application/json' } }); };
  try {
    await changeAlertNotifications(alpha, alertId(0), 'snooze', command, new AbortController().signal);
    await getAlertNotificationState(alpha, alertId(0), new AbortController().signal);
    assert.deepEqual(JSON.parse(calls[0].options.body), command);
    assert.equal(calls[0].options.headers.get('content-type'), 'application/json');
    assert.equal(calls[1].options.method, 'GET');
    assert.equal(calls[0].url, `/api/v1/observation-targets/${alpha}/alerts/${alertId(0)}/snooze`);
    assert.equal(calls[1].url, calls[0].url);
  } finally { globalThis.fetch = originalFetch; }
});
for (const status of [401, 403, 409, 503]) test(`notification API preserves HTTP ${status} for denial/conflict/transient ownership decisions`, async () => {
  const originalFetch = globalThis.fetch; globalThis.fetch = async () => new Response('{}', { status });
  try {
    const command = notificationCommand(current(), 'unsnooze', 15, 'Complete', operationId), signal = new AbortController().signal;
    await assert.rejects(() => getAlertNotificationState(alpha, alertId(0), signal), error => error.status === status);
    await assert.rejects(() => changeAlertNotifications(alpha, alertId(0), 'unsnooze', command, signal), error => error.status === status);
  } finally { globalThis.fetch = originalFetch; }
});
