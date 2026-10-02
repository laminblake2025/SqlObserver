import { createHash } from 'node:crypto';

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

// Model the native target+operation idempotency key. A receipt may be lost or
// invalid after materialization; repeating the exact payload returns that run.
export function reportBackend() {
  const operations = new Map();
  return {
    operations,
    accept(targetId, body, rawBody = JSON.stringify(body)) {
      const identity = `${targetId}:${body.operationId}`;
      const previous = operations.get(identity);
      if (previous) {
        if (previous.rawBody !== rawBody) throw new Error('An operation was replayed with a different request payload.');
        return previous;
      }
      const receipt = reportRunForRequest(body, { targetId,
        runId: operations.size === 0 ? runId : `77777777-7777-4777-8777-${String(operations.size + 1).padStart(12, '0')}` });
      const accepted = { body: JSON.parse(rawBody), rawBody, receipt };
      operations.set(identity, accepted);
      return accepted;
    },
  };
}
