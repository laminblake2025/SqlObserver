// Isolated synthetic, revision-aware maintenance edits. Never used by production.
const cookie = (request, name) => request.headers.cookie?.split(';').map(x => x.trim()).find(x => x.startsWith(name + '='))?.slice(name.length + 1);
const sessions = new Map();
export const editStartsUtc = '2026-11-01T10:00:00.123456Z';
export const editEndsUtc = '2026-11-01T11:00:00.654321Z';
export const maintenanceEditId = (targetIndex = 0, itemIndex = 1) => `78787878-7878-4787-8787-${String(targetIndex * 100 + itemIndex).padStart(12, '0')}`;
export function maintenanceEditRows(targetIndex = 0) {
  return [
    { windowId: maintenanceEditId(targetIndex), startsAtUtc: editStartsUtc, endsAtUtc: editEndsUtc, reason: 'SYNTHETIC precise maintenance A', revision: 7, cancelledAtUtc: null },
    { windowId: maintenanceEditId(targetIndex, 2), startsAtUtc: '2026-10-31T10:00:00.000Z', endsAtUtc: '2026-10-31T11:00:00.000Z', reason: 'SYNTHETIC maintenance B', revision: 4, cancelledAtUtc: null },
    { windowId: maintenanceEditId(targetIndex, 3), startsAtUtc: '2026-10-30T10:00:00.000Z', endsAtUtc: '2026-10-30T11:00:00.000Z', reason: 'SYNTHETIC cancelled maintenance', revision: 2, cancelledAtUtc: '2026-10-01T12:00:00.000Z' },
  ];
}
export async function handleMaintenanceEditFixture(url, response, request, targets, send) {
  if (cookie(request, 'maintenanceEditFixture') !== '1') return false;
  const viewer = cookie(request, 'maintenanceEditRole') === 'viewer';
  if (url.pathname === '/api/v1/me') {
    const targetId = url.searchParams.get('targetId'), roles = viewer ? ['Viewer'] : ['Viewer', 'TargetAdministrator'];
    send(response, { active: true, targetId, grantedRoles: roles, allTargetRoles: roles, targetRoles: targetId ? roles : [] }); return true;
  }
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/maintenance(?:\/([^/]+))?$/.exec(url.pathname);
  if (!route) return false;
  const targetIndex = targets.findIndex(target => target.instanceId === route[1]);
  if (targetIndex < 0) { send(response, {}, 404); return true; }
  const key = (cookie(request, 'maintenanceEditSession') ?? 'default') + ':' + route[1];
  if (!sessions.has(key)) sessions.set(key, { rows: maintenanceEditRows(targetIndex), operations: new Map() });
  const session = sessions.get(key);
  if (request.method === 'GET' && !route[2]) {
    send(response, { targetId: route[1], listingStartedUtc: '2026-10-02T12:00:00.000Z', items: session.rows, hasMore: false, nextCursor: null }); return true;
  }
  if (request.method !== 'PUT' || !route[2]) { send(response, {}, 405); return true; }
  if (viewer) { send(response, {}, 403); return true; }
  let body; try { let raw = ''; for await (const part of request) raw += part; body = JSON.parse(raw); } catch { send(response, {}, 400); return true; }
  const row = session.rows.find(item => item.windowId === route[2]);
  if (!row || row.cancelledAtUtc || body.windowId !== row.windowId || !/^[0-9a-f-]{36}$/i.test(body.operationId ?? '')) { send(response, {}, 400); return true; }
  const previous = session.operations.get(body.operationId);
  if (previous && previous.payload !== JSON.stringify(body)) { send(response, {}, 409); return true; }
  if (previous?.result) { send(response, previous.result); return true; }
  const mode = cookie(request, 'maintenanceEditMode') ?? 'current';
  if (body.expectedRevision !== row.revision || mode === 'conflict') { send(response, {}, 409); return true; }
  if (mode === 'retry' && !previous) { session.operations.set(body.operationId, { payload: JSON.stringify(body) }); send(response, {}, 500); return true; }
  Object.assign(row, { startsAtUtc: body.startsAtUtc, endsAtUtc: body.endsAtUtc, reason: body.reason, revision: row.revision + 1 });
  const result = { auditId: '89898989-8989-4989-8989-898989898989', recordedAtUtc: '2026-10-02T12:00:01.000Z' };
  session.operations.set(body.operationId, { payload: JSON.stringify(body), result }); send(response, result); return true;
}
