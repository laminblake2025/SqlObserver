// Native deadlock DTOs only. All identities and evidence are synthetic.
export const target = '11111111-1111-4111-8111-111111111111';
export const otherTarget = '44444444-4444-4444-8444-444444444444';
export const firstId = '22222222-2222-4222-8222-222222222222';
export const secondId = '55555555-5555-4555-8555-555555555555';
export const retainedId = '99999999-9999-4999-8999-999999999999';
export const firstAt = '2026-08-24T00:10:00.123456Z';
export const secondAt = '2026-08-24T00:20:00.654321Z';
export const retainedAt = '2026-08-24T00:30:00.123456Z';
export const fromUtc = '2026-08-24T00:00:00.000Z';
export const toUtc = '2026-08-24T01:00:00.000Z';
export const cursor = 'page+/=two';
export const providerText = '<img src="https://deadlock.invalid/provider" onerror="window.deadlockInjected=1">';

export function summary(overrides = {}) {
  return { eventId: firstId, occurredAtUtc: firstAt, fingerprint: 'a'.repeat(64),
    participantCount: 2, relationCount: 1, parseTruncated: false,
    collectedAtUtc: '2026-08-24T00:40:00Z', ...overrides };
}
export function secondSummary(overrides = {}) {
  return summary({ eventId: secondId, occurredAtUtc: secondAt, fingerprint: 'b'.repeat(64), ...overrides });
}
export function eventPage(items = [summary(), secondSummary()], overrides = {}) {
  return { targetId: target, repositoryTimeUtc: '2026-08-24T00:50:00Z', items, nextCursor: null, ...overrides };
}
export function eventDetail(selected = summary(), overrides = {}) {
  return { summary: selected, participants: [{ sessionId: 51, isVictim: true }, { sessionId: 52, isVictim: false }],
    relations: [{ waiterSessionId: 51, blockerSessionId: 52, resourceCategory: 'key', lockMode: 'X' }], ...overrides };
}
export function routeHash(id = target, selection) {
  const params = new URLSearchParams({ target: id, range: 'custom', from: fromUtc, to: toUtc, databaseId: '7', compare: '1' });
  if (selection) { params.set('deadlockEvent', selection.eventId); params.set('deadlockAt', selection.occurredAtUtc); }
  return `#/deadlocks?${params}`;
}
