import { alpha, beta, alertId, inboxPage, access } from './alertTriageFixture.mjs';
export { alpha, beta, alertId, access };
export const serverNowUtc = '2026-09-26T13:00:00.123456Z';
export const episodeId = '66666666-6666-4666-8666-666666666666';
export const auditId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export const nativeUtc = value => value === null ? null : value.replace(/(?:\.(\d{1,6}))?Z$/u,
  (_, fraction = '') => `.${fraction.padEnd(7, '0')}+00:00`);
export function notificationStatus(overrides = {}) {
  return { targetId: alpha, alertId: alertId(0), episodeId, revision: 7, state: 'firing', serverNowUtc: nativeUtc(serverNowUtc),
    snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null, snoozed: false, deliverySuppressed: false, ...overrides };
}
export function notificationReceipt(command, overrides = {}) {
  return { operationId: command.operationId, auditId, recordedAtUtc: nativeUtc(serverNowUtc), revision: command.expectedRevision + 1,
    endsAtUtc: command.endsAtUtc ? nativeUtc(command.endsAtUtc) : null, appliedAtUtc: nativeUtc(serverNowUtc), ...overrides };
}
export function snoozeInbox(kind = 'target', target = alpha, { legacy = false, state = 'firing', total = 1, projectionOverrides = {} } = {}) {
  const source = inboxPage({ kind, target, total });
  return { ...source, items: source.items.map(item => ({ ...item, state,
    reason: 'blocking_duration_v1|target=1|reason=source_stale|digest=' + 'a'.repeat(64),
    ...(!legacy ? { episodeId, revision: 7, snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null, snoozed: false } : {}), ...projectionOverrides })) };
}
const micros = value => { const match = /^(.*T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/u.exec(value);
  return BigInt(Date.parse(`${match[1]}Z`)) * 1000n + BigInt((match[2] ?? '').slice(0, 6).padEnd(6, '0')); };
/** Durable target/operation replay model; receipt replay never renews suppression. */
export function notificationBackend() {
  let now = serverNowUtc;
  const statuses = new Map(), operations = new Map();
  const key = (target, alert) => `${target}:${alert}`;
  const stored = (target, alert) => {
    const identity = key(target, alert); if (!statuses.has(identity)) statuses.set(identity, notificationStatus({ targetId: target, alertId: alert }));
    return statuses.get(identity);
  };
  return { operations, statuses, setNow(value) { now = value; },
    read(target, alert) {
      const value = stored(target, alert), snoozed = value.snoozedUntilUtc !== null && micros(value.snoozedUntilUtc) > micros(now);
      return { ...value, serverNowUtc: nativeUtc(now), snoozed, deliverySuppressed: snoozed || Boolean(value.otherSuppression) };
    },
    change(target, alert, action, command, rawBody = JSON.stringify(command)) {
      const operationKey = `${target}:${command.operationId}`, replay = operations.get(operationKey);
      if (replay) {
        if (replay.rawBody !== rawBody || replay.alertId !== alert || replay.action !== action) throw Object.assign(new Error('Operation payload changed'), { status: 409 });
        return replay.receipt;
      }
      const value = stored(target, alert);
      if (value.episodeId !== command.expectedEpisodeId || value.revision !== command.expectedRevision) throw Object.assign(new Error('Current alert changed'), { status: 409 });
      if (action === 'snooze' && (micros(command.endsAtUtc) <= micros(now) || micros(command.endsAtUtc) > micros(now) + 86_400_000_000n))
        throw Object.assign(new Error('Unsupported new snooze expiry'), { status: 400 });
      const next = { ...value, revision: value.revision + 1, snoozedUntilUtc: action === 'snooze' ? nativeUtc(command.endsAtUtc) : null,
        snoozeReason: action === 'snooze' ? command.reason : null, snoozedBy: action === 'snooze' ? 'S-1-5-21-123' : null,
        snoozedAtUtc: action === 'snooze' ? nativeUtc(now) : null };
      statuses.set(key(target, alert), next);
      const receipt = notificationReceipt(command, { revision: next.revision, recordedAtUtc: nativeUtc(now), appliedAtUtc: nativeUtc(now) });
      operations.set(operationKey, { action, alertId: alert, rawBody, receipt }); return receipt;
    } };
}
