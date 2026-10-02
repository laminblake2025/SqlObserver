// Opt-in synthetic retained metadata/current inbox. No connected VM or SQL execution.
import { waitCaptureRows } from './wait-captured-session-fixture.mjs';
const cookie = (request, name) => request.headers.cookie?.split(';').map(value => value.trim()).find(value => value.startsWith(name + '='))?.slice(name.length + 1);
export const retainedEvidenceNow = '2026-10-02T12:00:00.000Z';
export const retainedCaptureTimes = ['2026-10-02T11:45:00.123456Z', '2026-10-02T11:20:00.234567Z', '2026-10-02T10:30:00.345678Z'];
export const retainedCaptureId = (targetIndex = 0, captureIndex = 0) => `b1717171-7171-4717-8717-${String(100 * (targetIndex + 1) + captureIndex).padStart(12, '0')}`;
export const retainedAlertId = (targetIndex = 0, rowIndex = 0) => `a2727272-7272-4727-8727-${String(100 * (targetIndex + 1) + rowIndex).padStart(12, '0')}`;
const ticks = value => {
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6})(?:0)?)?(?:Z|\+00:00)$/.exec(value ?? '');
  if (!match) throw new Error('Synthetic retained bounds must be native UTC.');
  return BigInt(Date.parse(match[1] + 'Z')) * 1000n + BigInt((match[2] ?? '').padEnd(6, '0'));
};
function captures(index, mode) {
  const ordinary = retainedCaptureTimes.map((observedUtc, i) => ({ id: retainedCaptureId(index, i), observedUtc, truncated: i === 1 }));
  if (mode !== 'history-bound') return ordinary;
  // The actual history API may return more than the selector's 100-capture bound.
  return [ordinary[0], ...Array.from({ length: 100 }, (_, i) => ({ id: retainedCaptureId(index, 10 + i), observedUtc: new Date(Date.parse('2026-10-02T11:44:00Z') - i * 1000).toISOString(), truncated: false })), ordinary[1], ordinary[2]];
}
const unavailable = () => ({ snapshotId: null, observedUtc: null, repositoryTimeUtc: retainedEvidenceNow, state: 'unavailable', truncated: false, rows: [], databases: [], nextCursor: null });
function alertRows(index) {
  const row = (i, name, firedUtc, state = 'firing') => ({ alertId: retainedAlertId(index, i), ruleId: '73737373-7373-4737-8737-737373737373',
    ruleName: `SYNTHETIC ${name} ${index + 1}`, state, firstObservedUtc: '2026-10-02T10:50:00.000Z', firedUtc,
    value: 7 + i, reason: 'Synthetic current-inbox row, not a historical transition feed.', deliverySuppressed: false });
  return [row(0, 'fired blocking', '2026-10-02T11:05:00.123456Z'), row(1, 'window start', '2026-10-02T11:00:00.000000Z'),
    row(2, 'before window', '2026-10-02T10:59:59.999999Z'), row(3, 'window end', '2026-10-02T12:00:00.000000Z'),
    row(4, 'paged acknowledged', '2026-10-02T11:40:00.234567Z', 'acknowledged'), row(5, 'pending without fired time', null, 'pending')];
}
export function handleDashboardRetainedEvidenceFixture(url, response, request, targets, send) {
  if (cookie(request, 'retainedEvidenceFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/(activity\/live(?:\/(history|query))?|alerts\/active)$/.exec(url.pathname);
  if (!route) return false;
  const index = targets.findIndex(target => target.instanceId === route[1]);
  if (index < 0) { send(response, {}, 404); return true; }
  if (request.method !== 'GET') { send(response, {}, 405); return true; }
  if (route[2] === 'alerts/active') {
    const mode = cookie(request, 'retainedAlertMode') ?? 'current';
    if (mode === 'error') { send(response, {}, 503); return true; }
    const cursor = url.searchParams.get('cursor'), rows = alertRows(index);
    if (mode === 'bounded') {
      const page = cursor ? Number(Buffer.from(cursor, 'base64').toString().split(':')[1]) : 1;
      if (!Number.isInteger(page) || page < 1 || page > 6) { send(response, {}, 400); return true; }
      send(response, { targetId: targets[index].instanceId, snapshotUtc: retainedEvidenceNow,
        items: [{ ...rows[0], alertId: retainedAlertId(index, 10 + page), ruleName: `SYNTHETIC bounded fired ${page} ${index + 1}` }],
        nextCursor: Buffer.from('alerts:' + (page + 1)).toString('base64') }); return true;
    }
    if (cursor !== null && cursor !== 'YWxlcnRzOjI=') { send(response, {}, 400); return true; }
    const body = { targetId: targets[index].instanceId, snapshotUtc: retainedEvidenceNow,
      items: mode === 'empty' ? [] : cursor ? [...(mode === 'duplicate' ? [{ ...rows[0], alertId: rows[0].alertId.toUpperCase() }] : []), rows[4], rows[5]] : rows.slice(0, 4),
      nextCursor: mode === 'empty' ? null : cursor && mode !== 'cycle' ? null : 'YWxlcnRzOjI=' };
    if (mode === 'foreign') body.targetId = targets[(index + 1) % targets.length].instanceId;
    if (mode === 'snapshot-shift' && cursor) body.snapshotUtc = '2026-10-02T12:00:00.000001Z';
    send(response, body); return true;
  }
  const mode = cookie(request, 'retainedCaptureMode') ?? 'current';
  if (mode === 'error') { send(response, {}, 503); return true; }
  if (route[3] === 'query') { send(response, {}, 403); return true; }
  const all = captures(index, mode);
  if (route[3] === 'history') {
    const from = ticks(url.searchParams.get('from')), to = ticks(url.searchParams.get('to'));
    send(response, mode === 'empty' ? [] : all.filter(item => ticks(item.observedUtc) >= from && ticks(item.observedUtc) <= to)
      .map(item => item.id === retainedCaptureId(index, 1) ? { ...item, id: item.id.toUpperCase() } : item)); return true;
  }
  const requested = url.searchParams.get('snapshot');
  const snapshot = requested ? all.find(item => item.id.toLowerCase() === requested.toLowerCase()) : all[0];
  if (mode === 'empty' || !snapshot) { send(response, unavailable()); return true; }
  const cursor = url.searchParams.get('cursor');
  if (cursor !== null && cursor !== 'Y2FwdHVyZToy') { send(response, {}, 400); return true; }
  const database = url.searchParams.get('databaseId'), selectedIndex = retainedCaptureTimes.indexOf(snapshot.observedUtc);
  const rows = waitCaptureRows(index).map(row => ({ ...row, databaseName: `${row.databaseName} / capture ${selectedIndex < 0 ? 'bounded' : selectedIndex + 1}`,
    application: 'SYNTHETIC exact retained capture ' + snapshot.id }))
    .filter(row => !database || row.databaseId === Number(database));
  const shiftedTicks = ticks(snapshot.observedUtc) + 1n;
  const shiftedUtc = new Date(Number(shiftedTicks / 1000000n) * 1000).toISOString().slice(0, 19) + '.' + String(shiftedTicks % 1000000n).padStart(6, '0') + 'Z';
  send(response, { snapshotId: snapshot.id, observedUtc: mode === 'time-shift' ? shiftedUtc : snapshot.observedUtc,
    repositoryTimeUtc: retainedEvidenceNow, state: 'available', truncated: snapshot.truncated,
    rows: cursor ? rows.slice(5) : rows.slice(0, 5), databases: [{ id: 5, name: 'Orders' }, { id: 6, name: 'Warehouse' }],
    nextCursor: cursor || rows.length <= 5 ? null : 'Y2FwdHVyZToy' }); return true;
}
