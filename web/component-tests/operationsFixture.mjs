// Explicit native DTOs. Synthetic evidence only; no backend or connected data.
export const target = '11111111-1111-4111-8111-111111111111';
export const otherTarget = '44444444-4444-4444-8444-444444444444';
export const runId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
export const fromUtc = '2026-08-25T11:00:00.000Z';
export const toUtc = '2026-08-25T13:00:00.000Z';
export const observedAtUtc = '2026-08-25T12:00:00.000Z';
// Native v1 snapshot cursor: standard-base64 outer and PascalCase inner tie key.
export const projectionCursor = (fileId, id = target) => Buffer.from(`${id}|${observedAtUtc}|${Buffer.from(JSON.stringify({
  Kind: 'tempdb', RunId: runId, Revision: 4, From: null, To: null, A: String(fileId), B: '', C: '', D: '',
})).toString('base64')}`).toString('base64');
export const cursor = projectionCursor(50);
export const lastCursor = projectionCursor(100);
export const providerText = '<img src="https://operations.invalid/provider" onerror="window.operationsInjected=1">';
export const paths = {
  backups: '/api/v1/observation-targets/:id/backups', agent: '/api/v1/observation-targets/:id/sql-agent/failures',
  'tempdb-summary': '/api/v1/observation-targets/:id/tempdb', 'tempdb-files': '/api/v1/observation-targets/:id/tempdb/files',
  'ag-replicas': '/api/v1/observation-targets/:id/availability-groups/replicas',
  'ag-databases': '/api/v1/observation-targets/:id/availability-groups/databases',
  'backup-assessment': '/api/v2/observation-targets/:id/health/backups',
  'availability-replicas': '/api/v2/observation-targets/:id/availability-groups/replicas',
  'availability-databases': '/api/v2/observation-targets/:id/availability-groups/databases',
};
export function requestPath(kind, id = target, nextCursor) {
  const query = new URLSearchParams({ limit: '50' });
  if (nextCursor !== undefined) query.set('cursor', nextCursor);
  if (kind === 'agent') { query.set('fromUtc', fromUtc); query.set('toUtc', toUtc); }
  if (kind === 'backup-assessment' || kind.startsWith('availability-')) query.set('atUtc', toUtc);
  return `${paths[kind].replace(':id', id)}?${query}`;
}
export function routeHash(id = target, tab = 'backups') {
  return `#/operations?${new URLSearchParams({ target: id, databaseId: '7', range: 'custom', from: fromUtc, to: toUtc, compare: '1', check: tab })}`;
}
export function operational(kind, id = target, overrides = {}) {
  return { targetId: id, targetRevision: 4, runId, evidence: { observedAtUtc }, observedAtUtc, state: 'Complete',
    items: [], hasMore: false, nextCursor: null, truncated: false,
    ...(kind === 'agent' ? { coverageFromUtc: fromUtc, coverageToUtc: toUtc } : {}),
    ...(kind.startsWith('ag-') ? { visibilityScope: 'PrimaryAllKnown' } : {}), ...overrides,
    ...((overrides.state === 'NoData') ? { evidence: null } : {}) };
}
export function tempFile(fileId = 1, overrides = {}) {
  return { fileId, sizeBytes: 1024, usedBytes: 256, freeBytes: 768, state: 'Online', ...overrides };
}
export function agentRow(historyInstanceId = 101) {
  return { jobId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', historyInstanceId, stepId: 0, runStatus: 0,
    failureKind: 'Failed', retryAttempt: 0, durationSeconds: 12, firstObservedAtUtc: observedAtUtc,
    sourceLocalStart: '2026-08-25T07:58:00', failureFingerprint: 'a'.repeat(64), contentAvailable: false,
    isJobOutcome: true, countsAsJobFailure: true };
}
export function backupAssessment(id = target, overrides = {}) {
  return { contractVersion: 2, targetId: id, targetRevision: 4, atUtc: toUtc, runId: null,
    sourceObservedAtUtc: null, completedAtUtc: null, receivedAtUtc: null, sourceZoneId: null,
    historyLowerBoundLocal: null, coverage: 'not_collected', evidenceStatus: 'not_collected',
    sourceRows: 0, outputItems: 0, retainedItems: 0, databaseCount: 0, collectorEnabled: true,
    action: null, items: [], hasMore: false, nextCursor: null, ...overrides };
}
export function availability(id = target, overrides = {}) {
  return { schemaVersion: 2, targetId: id, atUtc: toUtc, targetRevision: 4, runId: null,
    sourceObservedAtUtc: null, completedAtUtc: null, state: 'NoData', evidenceStatus: 'not_collected',
    sourceRows: 0, outputItems: 0, retainedItems: 0, lossKind: 'none', truncated: false,
    collectorEnabled: true, items: [], hasMore: false, nextCursor: null, ...overrides };
}
export function replica(overrides = {}) {
  return { groupFingerprint: 'a'.repeat(64), replicaFingerprint: 'b'.repeat(64),
    observerReplicaFingerprint: 'b'.repeat(64), replicaIsLocal: true, visibilityScope: 'SecondaryLocalOnly',
    stateObserved: false, role: null, operationalState: null, connectedState: null, ...overrides };
}
export function fixture(id = target) {
  return {
    backups: operational('backups', id, { items: [{ databaseFingerprint: 'a'.repeat(64), kind: 'Full',
      coverage: 'Complete', lastFinishUtc: '2026-08-25T10:00:00Z', sizeBytes: 4096, backupSetId: 1,
      copyOnly: false, hasChecksum: true, isDamaged: false }] }),
    agent: operational('agent', id, { items: [agentRow()] }),
    'tempdb-summary': operational('tempdb-summary', id, { totalBytes: 1024, usedBytes: 256, logTotalBytes: 512, logUsedBytes: 0 }),
    'tempdb-files': operational('tempdb-files', id, { items: [tempFile()] }),
    'ag-replicas': operational('ag-replicas', id, { items: [{ groupFingerprint: 'a'.repeat(64), replicaFingerprint: 'b'.repeat(64),
      role: 'PRIMARY', operationalState: 'ONLINE', connectedState: 'CONNECTED', visibilityScope: 'PrimaryAllKnown', stateAvailable: true }] }),
    'ag-databases': operational('ag-databases', id, { items: [{ groupFingerprint: 'a'.repeat(64), databaseFingerprint: 'c'.repeat(64),
      synchronizationState: 'SYNCHRONIZED', databaseState: 'ONLINE', visibilityScope: 'PrimaryAllKnown', stateAvailable: true }] }),
    'backup-assessment': backupAssessment(id), 'availability-replicas': availability(id), 'availability-databases': availability(id),
  };
}
