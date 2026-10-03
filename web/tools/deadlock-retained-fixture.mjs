// Synthetic native DTOs for owned loopback frontend qualification only.
export const deadlockFixtureId = (index, seed = 0) => `33333333-3333-4333-8333-${String(seed + index + 1).padStart(12, '0')}`;
export function nativeDeadlockCursor(targetId, tail, cutoff, window) {
  return Buffer.from(JSON.stringify([targetId, tail.occurredAtUtc, tail.eventId, cutoff, window.fromUtc, window.toUtc]), 'utf8').toString('base64');
}
export function deadlockRetainedPage(url, targetId) {
  const incoming = url.searchParams.get('cursor');
  const window = { fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') };
  const decoded = incoming ? JSON.parse(Buffer.from(incoming, 'base64').toString('utf8')) : undefined;
  if (decoded && (decoded[0] !== targetId || decoded[4] !== window.fromUtc || decoded[5] !== window.toUtc)) throw new Error('Fixture cursor context mismatch.');
  const middle = Math.floor((Date.parse(window.fromUtc) + (Date.parse(window.toUtc) - Date.parse(window.fromUtc)) / 2) / 60_000) * 60_000;
  const seed = Math.floor(middle / 60_000) * 1000;
  const start = decoded ? Number(decoded[2].slice(-12)) - seed : 0;
  const cutoff = decoded?.[3] ?? new Date(Date.parse(window.toUtc) + 60_000).toISOString();
  const count = start < 50 ? 25 : 1;
  const items = Array.from({ length: count }, (_, row) => {
    const index = start + row;
    return { eventId: deadlockFixtureId(index, seed), occurredAtUtc: new Date(middle - index * 1000).toISOString(),
      fingerprint: String(index % 10).repeat(64), participantCount: 2, relationCount: 2,
      parseTruncated: false, collectedAtUtc: cutoff };
  });
  return { targetId, repositoryTimeUtc: cutoff, items,
    nextCursor: start < 50 ? nativeDeadlockCursor(targetId, items.at(-1), cutoff, window) : null };
}
export function deadlockRetainedDetail(summary) {
  return { summary, participants: [{ sessionId: 51, isVictim: true }, { sessionId: 72, isVictim: false }],
    relations: [{ blockerSessionId: 51, waiterSessionId: 72, resourceCategory: 'key', lockMode: 'X' },
      { blockerSessionId: 72, waiterSessionId: 51, resourceCategory: 'key', lockMode: 'X' }] };
}
const received = new Map();
export function handleDeadlockRetainedFixture(req, res, url, send) {
  if (!req.headers.cookie?.includes('deadlockRetainedFixture=1')) return false;
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/deadlocks(?:\/([^/]+))?$/.exec(url.pathname);
  if (!match) return false;
  if (req.method !== 'GET') throw new Error('Unexpected deadlock fixture method.');
  if (!match[2]) {
    const page = deadlockRetainedPage(url, match[1]);
    for (const item of page.items) received.set(`${match[1]}:${item.eventId}`, item);
    send(res, page);
  } else {
    const summary = received.get(`${match[1]}:${match[2]}`);
    send(res, summary ? deadlockRetainedDetail(summary) : {}, summary ? 200 : 404);
  }
  return true;
}
