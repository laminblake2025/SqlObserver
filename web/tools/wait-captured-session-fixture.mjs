// Native captured request metadata only; never SQL text or Query Store attribution.
const cookie = (request, name) => request.headers.cookie?.split(';').map(x => x.trim()).find(x => x.startsWith(name + '='))?.slice(name.length + 1);
export const waitCaptureUtc = '2026-10-02T11:30:00.123456Z';
export const waitCaptureId = (targetIndex = 0) => `67676767-6767-4676-8676-${String(targetIndex + 1).padStart(12, '0')}`;
export const waitSessionIdentity = sessionId => sessionId.toString(16).padStart(64, '0');
const utcTicks = value => {
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6})(?:0)?)?Z$/.exec(value ?? '');
  if (!match) throw new Error('Synthetic capture bounds must be native UTC instants.');
  return BigInt(Date.parse(match[1] + 'Z')) * 1000n + BigInt((match[2] ?? '').padEnd(6, '0'));
};
export function waitCaptureRows(targetIndex = 0) {
  const row = (sessionId, waitType, databaseId = 5) => ({ identity: waitSessionIdentity(sessionId), sessionId, requestId: 0,
    databaseId, databaseName: (databaseId === 5 ? 'Orders' : 'Warehouse') + (targetIndex ? ' 02' : ''),
    login: 'synthetic_dba', clientHost: 'synthetic.invalid', application: 'SYNTHETIC request metadata',
    status: waitType ? 'suspended' : 'running', command: 'SELECT', isUser: true, waitType, blocker: waitType?.startsWith('LCK_M_') ? 50 : null,
    cpuMs: '120', memoryBytes: '1048576', reads: '10', writes: '0', logicalReads: '100', elapsedMs: '12000',
    queryId: 'd'.repeat(64), queryState: 'available', engineStartup: '2026-10-01T00:00:00',
    sessionLogin: '2026-10-02T10:00:00', requestStart: '2026-10-02T11:00:00', delta: null });
  return [row(51, 'LCK_M_X'), row(61, 'PAGEIOLATCH_SH'), row(62, 'CXCONSUMER'), row(63, 'SLEEP_TASK'),
    row(64, null), row(72, 'LCK_M_S'), row(81, 'LCK_M_U', 6)];
}
export function handleWaitCapturedSessionFixture(url, response, request, targets, send) {
  if (cookie(request, 'waitCapturedSessionFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/live(?:\/(history|query))?$/.exec(url.pathname);
  if (!route) return false;
  const index = targets.findIndex(target => target.instanceId === route[1]);
  if (index < 0) { send(response, {}, 404); return true; }
  if (request.method !== 'GET') { send(response, {}, 405); return true; }
  if (route[2] === 'query') { send(response, {}, 403); return true; }
  const mode = cookie(request, 'waitCapturedSessionMode') ?? 'current';
  const snapshotId = waitCaptureId(mode === 'expired' ? index + 100 : index);
  const observedUtc = mode === 'expired' ? '2026-10-01T11:30:00.123456Z' : waitCaptureUtc;
  if (mode === 'error') { send(response, {}, 503); return true; }
  if (route[2] === 'history') {
    const from = utcTicks(url.searchParams.get('from')), to = utcTicks(url.searchParams.get('to'));
    const captures = mode === 'empty' || utcTicks(observedUtc) < from || utcTicks(observedUtc) > to ? [] :
      [{ id: snapshotId, observedUtc, truncated: mode === 'truncated' }];
    // Native history includes its exact upper bound; consumers adapt to half-open windows.
    if (mode === 'boundary') captures.push({ id: '68686868-6868-4686-8686-686868686868', observedUtc: url.searchParams.get('to'), truncated: false });
    send(response, captures); return true;
  }
  const requested = url.searchParams.get('snapshot');
  if (mode === 'empty' || requested && requested !== snapshotId) {
    send(response, { snapshotId: null, observedUtc: null, repositoryTimeUtc: '2026-10-02T12:00:00.000Z', state: 'unavailable', truncated: false, rows: [], databases: [], nextCursor: null }); return true;
  }
  const cursor = url.searchParams.get('cursor');
  if (mode === 'bounded') {
    const pageIndex = cursor ? Number(Buffer.from(cursor, 'base64').toString().split(':')[1]) : 0;
    if (!Number.isInteger(pageIndex) || pageIndex < 0 || pageIndex > 20) { send(response, {}, 400); return true; }
    const base = waitCaptureRows(index)[0];
    send(response, { snapshotId, observedUtc: waitCaptureUtc, repositoryTimeUtc: '2026-10-02T12:00:00.000Z', state: 'available', truncated: false,
      rows: Array.from({ length: pageIndex === 10 ? 12 : 50 }, (_, offset) => ({ ...base, sessionId: 100 + pageIndex * 50 + offset, identity: waitSessionIdentity(100 + pageIndex * 50 + offset) })),
      databases: [{ id: 5, name: 'Orders' }], nextCursor: Buffer.from('bounded:' + (pageIndex + 1)).toString('base64') }); return true;
  }
  if (cursor !== null && cursor !== 'd2FpdDoy') { send(response, {}, 400); return true; }
  const databaseId = url.searchParams.get('databaseId');
  const rows = waitCaptureRows(index).filter(row => !databaseId || row.databaseId === Number(databaseId))
    .map(row => mode === 'expired' ? { ...row, sessionLogin: '2026-10-01T10:00:00', requestStart: '2026-10-01T11:00:00' } : row);
  const pageRows = cursor ? rows.slice(5) : rows.slice(0, 5);
  send(response, { snapshotId, observedUtc, repositoryTimeUtc: '2026-10-02T12:00:00.000Z',
    state: mode === 'stale' ? 'stale' : 'available', truncated: mode === 'truncated',
    rows: pageRows,
    databases: [{ id: 5, name: 'Orders' + (index ? ' 02' : '') }, { id: 6, name: 'Warehouse' + (index ? ' 02' : '') }], nextCursor: cursor || rows.length <= 5 ? null : 'd2FpdDoy' }); return true;
}
