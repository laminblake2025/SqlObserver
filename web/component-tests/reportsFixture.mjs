export const target = '11111111-1111-4111-8111-111111111111';
export const otherTarget = '44444444-4444-4444-8444-444444444444';
export const runId = '22222222-2222-4222-8222-222222222222';
export const otherRunId = '55555555-5555-4555-8555-555555555555';
export const postPath = id => `/api/v1/observation-targets/${id}/reports`;
const kindValues = { 'instance-health': 0, 'performance-window': 1, 'incident-evidence': 2, 'capacity-readiness': 3 };
const roundTripUtc = value => `${value.slice(0, 19)}.${(value.match(/\.(\d{1,6})Z$/)?.[1] ?? '').padEnd(7, '0')}+00:00`;
export function reportRunForRequest(request, overrides = {}) {
  const canonical = request.fromUtc === undefined ? { reportKind: request.reportKind }
    : { fromUtc: roundTripUtc(request.fromUtc), reportKind: request.reportKind, toUtc: roundTripUtc(request.toUtc) };
  return reportRun({ kind: kindValues[request.reportKind],
    parameterDigest: createHash('sha256').update(JSON.stringify(canonical)).digest('hex'), ...overrides });
}
export const reportRun = (overrides = {}) => ({ runId, targetId: target, targetRevision: 7, kind: 0,
  definitionVersion: 1, parameterDigest: createHash('sha256').update('{"reportKind":"instance-health"}').digest('hex'),
  snapshotUtc: '2026-08-24T13:00:00Z', expiresAtUtc: '2026-08-25T13:00:00Z', state: 'finalized', ...overrides });
import { createHash } from 'node:crypto';
