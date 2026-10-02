// Isolated synthetic report receipts. Never imported by production assets.
import { createHash, randomUUID } from 'node:crypto';
const sessions = new Map();
const cookie = (request, name) => request.headers.cookie?.split(';').map(x => x.trim())
  .find(x => x.startsWith(name + '='))?.slice(name.length + 1);
const numericKinds = { 'instance-health': 0, 'performance-window': 1, 'incident-evidence': 2, 'capacity-readiness': 3 };
function digest(body) {
  const roundTrip = value => `${value.slice(0, 19)}.${(value.match(/\.(\d{1,6})Z$/)?.[1] ?? '').padEnd(7, '0')}+00:00`;
  return createHash('sha256').update(JSON.stringify(body.fromUtc === undefined ? { reportKind: body.reportKind }
    : { fromUtc: roundTrip(body.fromUtc), reportKind: body.reportKind, toUtc: roundTrip(body.toUtc) })).digest('hex');
}
export async function handleReportWorkflowFixture(url, response, request, targets, send) {
  if (cookie(request, 'reportWorkflowFixture') !== '1') return false;
  const sessionId = cookie(request, 'reportWorkflowSession');
  if (!sessionId || !/^[0-9a-f-]{36}$/i.test(sessionId)) return false;
  if (!sessions.has(sessionId)) sessions.set(sessionId, { observations: [], runs: new Map() });
  const session = sessions.get(sessionId);
  if (url.pathname === '/__reports-workflow') {
    send(response, { observations: session.observations, runs: [...session.runs.values()].map(value => value.receipt) });
    return true;
  }
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/reports\/?$/.exec(url.pathname);
  if (!match || request.method !== 'POST') return false;
  const target = targets.find(item => item.instanceId === match[1]);
  if (!target) { send(response, {}, 404); return true; }
  let raw = '';
  for await (const chunk of request) {
    raw += chunk.toString('utf8');
    if (Buffer.byteLength(raw) > 16 * 1024) { send(response, {}, 413); return true; }
  }
  let body;
  try { body = JSON.parse(raw); } catch { send(response, {}, 400); return true; }
  if (!Object.hasOwn(numericKinds, body.reportKind) || !/^[0-9a-f-]{36}$/i.test(body.operationId ?? '')) {
    send(response, {}, 400); return true;
  }
  session.observations.push({ targetId: target.instanceId, raw, body });
  const key = `${target.instanceId}:${body.operationId}`, previous = session.runs.get(key);
  if (previous && previous.raw !== raw) { send(response, {}, 409); return true; }
  if (previous) { send(response, previous.receipt); return true; }
  const receipt = { runId: randomUUID(), targetId: target.instanceId, targetRevision: target.configurationRevision,
    kind: numericKinds[body.reportKind], definitionVersion: 1, parameterDigest: digest(body), state: 'finalized',
    snapshotUtc: '2026-10-02T12:00:00.000001Z', expiresAtUtc: '2026-10-03T12:00:00.000001Z' };
  session.runs.set(key, { raw, receipt });
  if (cookie(request, 'reportWorkflowMode') === 'uncertain' && session.observations.length === 1) {
    // The request committed but its success receipt was truncated in transit.
    response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' });
    response.end('{"provider password=synthetic-secret":');
  } else send(response, receipt);
  return true;
}
