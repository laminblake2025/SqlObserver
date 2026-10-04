// Synthetic native DTOs and the repository's nested standard-base64 cursor envelope. No backend or connected evidence.
export const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
export const fromUtc = '2026-01-15T12:00:00.123456Z', toUtc = '2026-01-15T13:00:00.123456Z';
export const providerText = 'SECRET_PROJECTION_PROVIDER <img src="https://projection.invalid" onerror="window.projectionInjected=true">';
export const runId = (generation, kind = 'ag-databases', target = alpha) => `${target === beta ? '44444444' : '33333333'}-3333-4333-8333-${String(generation * 100 + (kind === 'backups' ? 2 : kind.startsWith('ag-') ? 3 : 1)).padStart(12, '0')}`;
export const paths = {
  backups: '/api/v1/observation-targets/:id/backups', 'tempdb-summary': '/api/v1/observation-targets/:id/tempdb',
  'tempdb-files': '/api/v1/observation-targets/:id/tempdb/files', 'ag-replicas': '/api/v1/observation-targets/:id/availability-groups/replicas',
  'ag-databases': '/api/v1/observation-targets/:id/availability-groups/databases',
  'backup-assessment': '/api/v2/observation-targets/:id/health/backups',
  'availability-replicas': '/api/v2/observation-targets/:id/availability-groups/replicas',
  'availability-databases': '/api/v2/observation-targets/:id/availability-groups/databases',
};
export const legacyKinds = ['backups', 'tempdb-summary', 'tempdb-files', 'ag-replicas', 'ag-databases'];
export const collectorKind = kind => kind.startsWith('tempdb-') ? 'tempdb' : kind.startsWith('ag-') ? 'availability' : kind;
export function routeHash(target = alpha, check = 'availability') {
  return `#/operations?${new URLSearchParams({ target, range: 'custom', from: fromUtc, to: toUtc, databaseId: '7', check })}`;
}
const fingerprint = (item, salt) => (BigInt(item.generation) * 1000n + BigInt(item.index) * 100n + BigInt(salt)).toString(16).padStart(64, '0');
export function cursorFor(item) {
  const inner = { Kind: item.kind.startsWith('tempdb') ? 'tempdb' : item.kind === 'ag-replicas' ? 'ag.replicas'
    : item.kind === 'ag-databases' ? 'ag.databases' : item.kind,
    RunId: runId(item.generation, item.kind, item.target), Revision: item.revision, From: null, To: null, A: '', B: '', C: '', D: '' };
  if (item.kind.startsWith('tempdb')) inner.A = String(item.generation * 1000 + item.index * 50 + 50);
  else if (item.kind === 'backups') { inner.A = '2026-01-15T12:00:00.0000000+00:00'; inner.B = String(1000 - item.index * 50 - 50); inner.C = fingerprint(item, 50); inner.D = '1'; }
  else { inner.A = fingerprint(item, 50); inner.B = fingerprint(item, item.kind === 'ag-replicas' ? 550 : 650); }
  const tie = Buffer.from(JSON.stringify(inner)).toString('base64');
  const snapshot = `2026-01-15T13:${String(item.generation).padStart(2, '0')}:00.1234560+00:00`;
  return Buffer.from(`${item.target}|${snapshot}|${tie}`).toString('base64');
}
export function decodeCursor(encoded, expectedTarget, expectedKind) {
  const [target, snapshot, tie, extra] = Buffer.from(encoded, 'base64').toString('utf8').split('|');
  const inner = JSON.parse(Buffer.from(tie ?? '', 'base64').toString('utf8'));
  const actualKind = inner.Kind === 'tempdb' ? 'tempdb-files' : inner.Kind === 'ag.replicas' ? 'ag-replicas'
    : inner.Kind === 'ag.databases' ? 'ag-databases' : inner.Kind;
  if (extra || target !== expectedTarget || actualKind !== expectedKind || !Number.isFinite(Date.parse(snapshot)) ||
    !Number.isSafeInteger(inner.Revision) || inner.Revision < 1 || inner.From !== null || inner.To !== null) throw new Error('Invalid native fixture cursor');
  const generation = Math.floor(Number(inner.RunId.slice(-12)) / 100);
  const index = actualKind === 'tempdb-files' ? (Number(inner.A) - generation * 1000) / 50
    : actualKind === 'backups' ? (1000 - Number(inner.B)) / 50 : Math.floor(Number(BigInt(`0x${inner.A}`) % 1000n) / 100) + 1;
  if (![1, 2].includes(index)) throw new Error('Invalid native fixture cursor position');
  return { target, kind: actualKind, generation, revision: inner.Revision, index, snapshot, inner };
}
export function projection(item) {
  const observedAtUtc = `2026-01-15T13:${String(item.generation).padStart(2, '0')}:00.123456Z`;
  const count = item.index < 2 ? 50 : 10;
  const offsets = Array.from({ length: count }, (_, index) => index + 1);
  const common = { targetId: item.target, targetRevision: item.revision, runId: runId(item.generation, item.kind, item.target), evidence: { observedAtUtc }, observedAtUtc,
    state: 'Complete', items: [], totalBytes: null, usedBytes: null, logTotalBytes: null, logUsedBytes: null,
    coverageFromUtc: null, coverageToUtc: null, coverage: null, visibilityScope: null, hasMore: item.kind !== 'tempdb-summary' && item.index < 2, nextCursor: null, truncated: false };
  if (common.hasMore) common.nextCursor = cursorFor(item);
  if (item.kind === 'tempdb-summary') return { ...common, hasMore: true, nextCursor: cursorFor(item),
    totalBytes: 110 * (9007199254000 + item.generation * 1000) + 2605, usedBytes: 110 * (4007199253000 + item.generation * 200) + 2605,
    logTotalBytes: 5120, logUsedBytes: 1280 };
  if (item.kind === 'tempdb-files') return { ...common, items: offsets.map(offset => ({
    fileId: item.generation * 1000 + item.index * 50 + offset, sizeBytes: 9007199254000 + item.generation * 1000 + offset,
    usedBytes: 4007199253000 + item.generation * 200 + offset, freeBytes: 5000000001000 + item.generation * 800,
    state: 'Online' })) };
  if (item.kind === 'backups') return { ...common, items: offsets.map(offset => ({ databaseFingerprint: fingerprint(item, offset), kind: 'Full',
    coverage: 'Complete', sourceLocalFinish: '2026-01-15T12:00:00', sourceTimeUnknown: false, lastFinishUtc: '2026-01-15T12:00:00Z',
    sizeBytes: 2048 + item.index * 50 + offset, backupSetId: 1000 - item.index * 50 - offset,
    copyOnly: false, hasChecksum: true, isDamaged: false })) };
  // Native AG truncation is page-relative; visibility is the first returned row's scope.
  const visibilityScope = ['PrimaryAllKnown', 'SecondaryLocalOnly', 'ResolvingLocalOnly'][item.index];
  if (item.kind === 'ag-replicas') return { ...common, truncated: common.hasMore, visibilityScope, items: offsets.map(offset => ({
    groupFingerprint: fingerprint(item, offset), replicaFingerprint: fingerprint(item, 500 + offset), role: item.index === 0 ? 'PRIMARY' : item.index === 1 ? 'SECONDARY' : 'RESOLVING',
    operationalState: 'ONLINE', connectedState: 'CONNECTED', visibilityScope, stateAvailable: true })) };
  if (item.kind === 'ag-databases') return { ...common, truncated: common.hasMore, visibilityScope, items: offsets.map(offset => ({
    groupFingerprint: fingerprint(item, offset), databaseFingerprint: fingerprint(item, 600 + offset), synchronizationState: 'SYNCHRONIZED',
    databaseState: 'ONLINE', visibilityScope, stateAvailable: true })) };
  if (item.kind === 'backup-assessment') return { contractVersion: 2, targetId: item.target, targetRevision: item.revision, atUtc: item.atUtc,
    runId: null, sourceObservedAtUtc: null, completedAtUtc: null, receivedAtUtc: null, sourceZoneId: null, historyLowerBoundLocal: null,
    coverage: 'not_collected', evidenceStatus: 'not_collected', sourceRows: 0, outputItems: 0, retainedItems: 0, databaseCount: 0,
    collectorEnabled: true, action: null, items: [], hasMore: false, nextCursor: null };
  if (item.kind.startsWith('availability-')) return { schemaVersion: 2, targetId: item.target, targetRevision: item.revision, atUtc: item.atUtc,
    runId: null, sourceObservedAtUtc: null, completedAtUtc: null, state: 'NoData', evidenceStatus: 'not_collected', sourceRows: 0,
    outputItems: 0, retainedItems: 0, lossKind: 'none', truncated: false, collectorEnabled: true, items: [], hasMore: false, nextCursor: null };
  throw new Error(`Unregistered fixture kind ${item.kind}`);
}
