import { createHash } from 'node:crypto';

export const target = '11111111-1111-4111-8111-000000000001';
export const otherTarget = '11111111-1111-4111-8111-000000000002';
export const thread = 'aaaaaaaa-aaaa-4aaa-8aaa-000000000001';
export const otherThread = 'aaaaaaaa-aaaa-4aaa-8aaa-000000000002';
export const fromUtc = '2026-09-29T12:00:00.000001Z';
export const toUtc = '2026-09-29T13:00:00.000002Z';
export const nowUtc = '2026-10-02T12:00:00.000Z';
export const diagnosticId = 'dddddddd-dddd-4ddd-8ddd-000000000001';
export const sourceRunId = 'eeeeeeee-eeee-4eee-8eee-000000000001';
export const tombstone = 'f'.repeat(64);
export const inertText = '<img src="https://invalid.example/incident" onerror="window.incidentInjected=true"><script>window.incidentInjected=true</script>';
export const packetIds = [1, 2, 3, 4].map(index => `cccccccc-cccc-4ccc-8ccc-${String(index).padStart(12, '0')}`);
export const incidentHref = (id = target, selected = thread, extra = {}) => `#/health?${new URLSearchParams({
  target: id, range: 'custom', from: fromUtc, to: toUtc, databaseId: '5', compare: '1',
  ...(selected ? { incident: selected } : {}), ...extra })}`;

function packet(index, id, revision, visibilityState = 'complete', overrides = {}) {
  const result = { packetId: packetIds[index], occurredAtUtc: `2026-09-29T12:${String(20 + index * 10).padStart(2, '0')}:00.123456Z`,
    evidenceKind: 'metric', sourceRunId: null, sourceSha256: 'a'.repeat(64), identitySha256: 'b'.repeat(64),
    sourceCutoffSha256: 'c'.repeat(64), sourceCutoffUtc: null, visibilityState, confidence: visibilityState === 'complete' ? 0.125 : null,
    ...overrides };
  result.evidence = { references: visibilityState === 'unavailable' ? [] : [{ type: 'health', identity: diagnosticId,
    occurredAtUtc: result.occurredAtUtc }], tombstones: [], packetId: result.packetId, targetId: id, targetRevision: revision,
    trigger: 'metric', algorithm: 'evidence-v1', generation: 1, state: result.visibilityState, truncated: false,
    evidenceKind: result.evidenceKind, sourceDigest: result.sourceSha256, sourceCutoffSha256: result.sourceCutoffSha256,
    sourceCutoffUtc: result.sourceCutoffUtc, sourceRunId: result.sourceRunId };
  return result;
}
export function savedIncident(scenario = 'rich', id = target, selected = id === target ? thread : otherThread) {
  const revision = id === target ? 7 : 8;
  const complete = packet(0, id, revision);
  const partial = packet(1, id, revision, 'partial', { sourceRunId, sourceCutoffUtc: '2026-09-29T12:25:00.654321Z' });
  partial.evidence.truncated = true; partial.evidence.tombstones = [tombstone];
  partial.evidence.references = [{ type: 'metric', identity: 'metric:engine.cpu:retained-sample-001', occurredAtUtc: partial.occurredAtUtc }];
  const unavailable = packet(2, id, revision, 'unavailable');
  const unknown = packet(3, id, revision, 'unsupported');
  unknown.evidence = { schemaVersion: 99, providerText: inertText, opaqueRows: Array.from({ length: 80 }, (_, index) => `Uninterpreted saved row ${index}: ${'x'.repeat(64)}`) };
  return { targetId: id, targetRevision: revision, threadId: selected,
    operationId: 'bbbbbbbb-bbbb-4bbb-8bbb-000000000001', fromUtc, toUtc,
    savedAtUtc: '2026-09-29T14:00:00.000003Z', title: id === target ? 'SYNTHETIC saved investigation' : 'SYNTHETIC other investigation',
    notes: scenario === 'inert' ? inertText : 'Synthetic retained notes. These packets record source metadata, not root-cause conclusions.',
    evidenceTruncated: scenario === 'truncated', evidence: scenario === 'empty' ? [] : scenario === 'known' ? [complete]
      : scenario === 'unknown' || scenario === 'inert' ? [unknown] : scenario === 'partial' ? [partial, unavailable] : [complete, partial, unavailable, unknown] };
}
export function summaryPage(id = target, older = false) {
  const base = savedIncident('empty', id);
  return { targetId: id, snapshotUtc: '2026-10-02T12:00:00.000Z',
    items: Array.from({ length: older ? 1 : 20 }, (_, index) => ({ targetId: id, targetRevision: base.targetRevision,
      threadId: `aaaaaaaa-aaaa-4aaa-8aaa-${String(100 - (older ? 20 : index)).padStart(12, '0')}`,
      fromUtc, toUtc, savedAtUtc: '2026-09-29T14:00:00.000003Z', title: `SYNTHETIC saved page ${older ? 20 : index}`,
      evidenceCount: index, evidenceTruncated: index === 0 })), hasMore: !older, nextCursor: older ? null : 'native.snapshot' };
}
export function reportReceipt(incident, runId, snapshotUtc = nowUtc) {
  return { runId, targetId: incident.targetId, targetRevision: incident.targetRevision, kind: 4, definitionVersion: 1,
    snapshotUtc, expiresAtUtc: new Date(Date.parse(snapshotUtc) + 24 * 3600 * 1000).toISOString(),
    parameterDigest: createHash('sha256').update(JSON.stringify({ reportKind: 'saved-investigation', threadId: incident.threadId.toLowerCase() })).digest('hex'),
    state: 'finalized' };
}
export function incidentBackend() {
  const incidents = new Map([[`${target}:${thread}`, savedIncident()], [`${otherTarget}:${otherThread}`, savedIncident('rich', otherTarget)]]);
  const operations = new Map(); const reportRequests = []; const saveRequests = [];
  return { incidents, operations, reportRequests, saveRequests, now: nowUtc,
    acceptReport(id, body) {
      const incident = incidents.get(`${id}:${body.threadId}`);
      if (!incident) throw new Error('Report fixture requires an existing target-bound saved investigation.');
      reportRequests.push({ id, body });
      const existing = operations.get(body.operationId);
      if (existing) {
        if (existing.id !== id || JSON.stringify(existing.body) !== JSON.stringify(body)) throw new Error('A report operation cannot change its native payload.');
        return existing.receipt;
      }
      const runId = `77777777-7777-4777-8777-${String(operations.size + 1).padStart(12, '0')}`;
      const receipt = reportReceipt(incident, runId, this.now); operations.set(body.operationId, { id, body, receipt }); return receipt;
    },
    acceptSave(id, body) {
      saveRequests.push({ id, body });
      const value = { ...savedIncident('empty', id), threadId: id === target ? thread : otherThread,
        targetRevision: body.expectedTargetRevision, operationId: body.operationId, fromUtc: body.fromUtc, toUtc: body.toUtc,
        title: body.title, notes: body.notes, savedAtUtc: this.now };
      incidents.set(`${id}:${value.threadId}`, value); return value;
    } };
}
