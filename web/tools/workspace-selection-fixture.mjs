// Synthetic URL-selection evidence; isolated from ordinary fixture scenarios.
const cookie = (request, name) => request.headers.cookie?.split(';').some(value => value.trim() === name + '=1');
export const deadlockSelectionIds = ['33333333-3333-4333-8333-333333333333', '33333333-3333-4333-8333-333333333334', '33333333-3333-4333-8333-333333333335'];
export const deadlockSelectionTimes = ['2026-09-26T11:00:00.123456Z', '2026-09-26T10:00:00.654321Z', '2026-09-26T09:00:00.000001Z'];
const listingStartedUtc = '2026-09-30T12:00:00.000Z';
export function handleWorkspaceSelectionFixture(url, response, request, targets, send) {
  const admin = cookie(request, 'adminSelectionFixture'), deadlocks = cookie(request, 'deadlockSelectionFixture');
  if (admin && url.pathname === '/api/v1/me') {
    const targetId = url.searchParams.get('targetId'), roles = ['Viewer', 'TargetAdministrator', 'SecurityAdministrator'];
    send(response, { active: true, targetId, grantedRoles: roles, allTargetRoles: roles, targetRoles: targetId ? roles : [] }); return true;
  }
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if (!route) return false;
  const target = targets.find(item => item.instanceId === route[1]);
  if (!target) return false;
  const targetId = target.instanceId, surface = route[2];
  if (deadlocks && (surface === 'deadlocks' || surface.startsWith('deadlocks/'))) {
    const targetOffset = targets.indexOf(target) * 10;
    const exact = deadlockSelectionIds.map((eventId, index) => ({ eventId: eventId.slice(0, -3) + String(333 + index + targetOffset), fingerprint: 'abc'[index].repeat(64), occurredAtUtc: deadlockSelectionTimes[index], participantCount: 2, relationCount: 2, parseTruncated: false, collectedAtUtc: listingStartedUtc }));
    // Native continuation is emitted only after a full 25-row page. Keep the
    // third exact occurrence off that page with distinct intervening evidence.
    const fillers = Array.from({ length: 23 }, (_, index) => ({ ...exact[1],
      eventId: `44444444-4444-4444-8444-${String(targetOffset + index + 1).padStart(12, '0')}`,
      fingerprint: 'd'.repeat(64), occurredAtUtc: `2026-09-26T10:00:00.${String(654320 - index).padStart(6, '0')}Z` }));
    const summaries = [exact[0], exact[1], ...fillers, exact[2]];
    if (surface === 'deadlocks') {
      const cursor = url.searchParams.get('cursor');
      const from = url.searchParams.get('fromUtc'), to = url.searchParams.get('toUtc');
      const micros = value => BigInt(Date.parse(value.slice(0, 19) + 'Z')) * 1000n + BigInt((value.split('.')[1]?.replace(/(?:Z|\+00:00)$/, '') ?? '').padEnd(6, '0'));
      const rows = summaries.filter(item => micros(item.occurredAtUtc) >= micros(from) && micros(item.occurredAtUtc) < micros(to));
      const tail = rows[24];
      const next = rows.length > 25 ? Buffer.from(JSON.stringify([targetId, tail.occurredAtUtc, tail.eventId, listingStartedUtc, from, to])).toString('base64') : null;
      if (cursor && cursor !== next) { send(response, {}, 400); return true; }
      const items = cursor ? rows.slice(25) : rows.slice(0, 25);
      send(response, { targetId, repositoryTimeUtc: listingStartedUtc, items, nextCursor: cursor ? null : next }); return true;
    }
    const summary = summaries.find(item => surface === 'deadlocks/' + item.eventId);
    if (!summary) { send(response, {}, 404); return true; }
    const index = Math.max(0, exact.indexOf(summary)), offset = targets.indexOf(target) * 100;
    const victim = [51, 81, 91][index] + offset, blocker = [72, 82, 92][index] + offset;
    send(response, { summary, participants: [{ sessionId: victim, isVictim: true }, { sessionId: blocker, isVictim: false }], relations: [{ blockerSessionId: victim, waiterSessionId: blocker, resourceCategory: 'key', lockMode: 'X' }, { blockerSessionId: blocker, waiterSessionId: victim, resourceCategory: 'key', lockMode: 'X' }] }); return true;
  }
  const collection = /^alerts\/(rules|maintenance|destinations)$/.exec(surface);
  if (!admin || !collection) return false;
  if (request.method !== 'GET') { send(response, {}, 405); return true; }
  const cursor = url.searchParams.get('cursor');
  if (cursor && cursor !== 'cGFnZTI=') { send(response, {}, 400); return true; }
  const index = cursor ? 1 : 0, suffix = String(targets.indexOf(target) * 100 + index + 1).padStart(12, '0');
  const rule = { ruleId: `66666666-6666-4666-8666-${suffix}`, name: `SYNTHETIC rule ${index + 1}`, kind: 'metric_threshold', metricId: 'engine.sql_scheduler_cpu_percent', comparison: 'greater_than', threshold: 80 + index, hysteresis: 5, confirmationCount: 2, confirmationSeconds: 0, evaluationSeconds: 30, clearConfirmationCount: 2, enabled: true, revision: index + 1, updatedAtUtc: listingStartedUtc };
  const maintenance = { windowId: `77777777-7777-4777-8777-${suffix}`, startsAtUtc: `2026-09-30T${index ? '09' : '10'}:00:00.000Z`, endsAtUtc: `2026-09-30T${index ? '10' : '11'}:00:00.000Z`, reason: `SYNTHETIC maintenance ${index + 1}`, revision: index + 1, cancelledAtUtc: null };
  const destination = { destinationId: `88888888-8888-4888-8888-${suffix}`, kind: index ? 'windows-event-log' : 'https-webhook', enabled: true, approved: index === 0, revision: index + 1 };
  send(response, { targetId, listingStartedUtc, items: [collection[1] === 'rules' ? rule : collection[1] === 'maintenance' ? maintenance : destination], hasMore: !cursor, nextCursor: cursor ? null : 'cGFnZTI=' }); return true;
}
