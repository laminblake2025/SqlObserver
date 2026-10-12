// Opt-in synthetic maintenance evidence. No write endpoints or production imports.
const cookieValue = (request, name) => request.headers.cookie?.split(';').map(value => value.trim()).find(value => value.startsWith(name + '='))?.slice(name.length + 1);
export const calendarListingUtc = '2026-10-02T12:00:00.000Z';
export function calendarFixtureRows(targetIndex = 0) {
  const row = (index, startsAtUtc, endsAtUtc, reason, cancelledAtUtc = null) => ({
    windowId: '99999999-9999-4999-8999-' + String(targetIndex * 100 + index).padStart(12, '0'),
    startsAtUtc, endsAtUtc, reason: `SYNTHETIC SQL ${String(targetIndex + 1).padStart(2, '0')} ${reason}`,
    revision: 1, cancelledAtUtc,
  });
  return [
    row(1, '2026-11-01T05:30:00.000Z', '2026-11-01T07:30:00.000Z', 'DST overlap'),
    row(2, '2026-10-31T20:00:00.000Z', '2026-11-01T03:00:00.000Z', 'cancelled overlap', '2026-10-01T12:00:00.000Z'),
    row(3, '2026-10-30T23:00:00.000Z', '2026-11-02T00:00:00.000Z', 'multi-day delivery suppression'),
    row(4, '2026-10-20T10:00:00.000Z', '2026-10-20T11:00:00.000Z', 'single-day delivery suppression'),
    row(5, '2026-09-29T10:00:00.000Z', '2026-09-29T11:00:00.000Z', 'previous-month maintenance'),
    row(6, '2026-12-02T10:00:00.000Z', '2026-12-02T11:00:00.000Z', 'later-page maintenance'),
  ].sort((left,right)=>Date.parse(right.startsAtUtc)-Date.parse(left.startsAtUtc)||left.windowId.localeCompare(right.windowId));
}
export function handleMaintenanceCalendarFixture(url, response, request, targets, send) {
  if (cookieValue(request, 'maintenanceCalendarFixture') !== '1') return false;
  if (url.pathname === '/api/v1/me') {
    const targetId = url.searchParams.get('targetId'), roles = cookieValue(request, 'maintenanceCalendarRole') === 'viewer' ? ['Viewer'] : ['Viewer', 'TargetAdministrator'];
    send(response, { active: true, targetId, grantedRoles: roles, allTargetRoles: roles, targetRoles: targetId ? roles : [] });
    return true;
  }
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/maintenance$/.exec(url.pathname);
  if (!route) return false;
  const targetIndex = targets.findIndex(target => target.instanceId === route[1]);
  if (targetIndex < 0) { send(response, {}, 404); return true; }
  if (request.method !== 'GET') { send(response, {}, 405); return true; }
  const cursor = url.searchParams.get('cursor');
  if (cursor !== null && cursor !== 'Y2FsZW5kYXI6Mg==') { send(response, {}, 400); return true; }
  const mode = cookieValue(request, 'maintenanceCalendarMode');
  if (mode === 'error') { send(response, {}, 503); return true; }
  const rows = calendarFixtureRows(targetIndex), empty = mode === 'empty';
  send(response, { targetId: route[1], listingStartedUtc: calendarListingUtc,
    items: empty ? [] : cursor ? rows.slice(4) : rows.slice(0, 4),
    hasMore: !empty && cursor === null, nextCursor: !empty && cursor === null ? 'Y2FsZW5kYXI6Mg==' : null });
  return true;
}
