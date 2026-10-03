export const alpha = '11111111-1111-4111-8111-000000000001';
export const beta = '11111111-1111-4111-8111-000000000002';
export const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
export const labels = { jobs: 'Job inventory', backfill: 'Backfill status', 'host/status': 'Host status', 'host/metrics': 'Host metrics', 'replication/status': 'Replication status', 'replication/evidence': 'Replication evidence', 'diagnostics/search': 'Diagnostics search', incidents: 'Incidents', 'evidence-packets': 'Evidence packets' };
export const limits = { jobs: 100, backfill: 100, 'host/status': 200, 'host/metrics': 200, 'replication/status': 200, 'replication/evidence': 200, 'diagnostics/search': 100, incidents: 100, 'evidence-packets': 200 };
export const malicious = '<img src="https://analytics.invalid/native" onerror="window.analyticsInjected=true">';
export const rowId = (generation, index) => `33333333-3333-4333-8333-${String(generation * 100000 + index).padStart(12, '0')}`;
export const decode = value => JSON.parse(Buffer.from(value, 'base64url').toString('utf8'));
export const encode = value => Buffer.from(JSON.stringify(value), 'utf8').toString('base64url');
export function registered(url) {
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/analytics\/(jobs|backfill|host\/status|host\/metrics|replication\/status|replication\/evidence|diagnostics\/search|incidents|evidence-packets)$/u.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1]) || [...url.searchParams.keys()].some(key => !['fromUtc', 'toUtc', 'limit', 'cursor'].includes(key))) return undefined;
  const surface = match[2], limit = Number(url.searchParams.get('limit'));
  if (limit !== limits[surface]) return undefined;
  const start = url.searchParams.get('fromUtc'), end = url.searchParams.get('toUtc');
  if (Boolean(start) !== Boolean(end) || start && (!Number.isFinite(Date.parse(start)) || Date.parse(end) <= Date.parse(start))) return undefined;
  const cursor = url.searchParams.get('cursor'); let continuation;
  if (cursor) {
    try { continuation = decode(cursor); } catch { return undefined; }
    if (continuation.kind !== 'm10.surface.v1' || continuation.targetId !== match[1] || continuation.surface !== surface || continuation.fromUtc !== start || continuation.toUtc !== end) return undefined;
  }
  return { targetId: match[1], surface, limit, fromUtc: start ?? from, toUtc: end ?? to, cursor, continuation };
}
function at(item, index) {
  // Job inventory is intentionally older than the selected evidence window.
  const origin = item.surface === 'jobs' || item.surface === 'backfill' ? Date.parse('2026-09-01T10:00:00Z') : Date.parse(item.fromUtc);
  return new Date(origin + (index + 1) * 1000).toISOString();
}
function row(item, generation, revision, index) {
  const id = rowId(generation, index), time = at(item, index), targetRevision = revision;
  switch (item.surface) {
    case 'jobs': case 'backfill': return { jobId: id, jobKind: item.surface === 'backfill' ? 'backfill' : 'rollup', status: 'succeeded', requestedAtUtc: time, startedAtUtc: null, completedAtUtc: null, attempt: 1, targetRevision };
    case 'host/status': return { hostId: id, targetRevision, bindingRevision: 1, hostName: `Synthetic host ${index}`, bindingState: 'active', capabilityState: 'available', observedAtUtc: new Date(Date.parse(item.toUtc) - (index + 1) * 1000).toISOString(), generation };
    case 'host/metrics': return { observedAtUtc: time, runId: id, metricKey: 'host.cpu.percent', value: index % 100, dimensions: { processor: `Synthetic CPU ${index}` }, targetRevision };
    case 'replication/status': case 'replication/evidence': return { observedAtUtc: time, runId: id, role: 'distributor', synchronizationState: 'running', sendQueueBytes: null, redoQueueBytes: null, pendingCommands: index, latencySeconds: null, visibilityScope: 1, coverage: 'complete', visibilityGap: null, stateAvailable: true, targetRevision };
    case 'diagnostics/search': return { occurredAtUtc: time, eventId: id, eventKind: 'collector', severity: 1, safeMetadata: JSON.stringify({ description: malicious, synthetic: true }), collectedAtUtc: time, targetRevision };
    case 'incidents': return { threadId: id, openedAtUtc: time, closedAtUtc: null, currentGeneration: generation };
    default: return { occurredAtUtc: time, packetId: id, evidenceKind: 'correlation', sourceRunId: null, sourceDigest: 'a'.repeat(64), confidence: null, visibilityState: 'complete', targetRevision };
  }
}
export function nativeValue(item, context) {
  const anchor = item.continuation, generation = anchor?.generation ?? context.generation, revision = anchor?.targetRevision ?? context.revision;
  const offset = anchor ? Number(anchor.tieId.slice(-12)) - generation * 100000 + 1 : 0;
  const total = context.total ?? item.limit * 2 + 2, count = Math.min(item.limit, Math.max(0, total - offset));
  const items = Array.from({ length: item.surface === 'incidents' ? Math.min(3, item.limit) : count }, (_, index) => row(item, generation, revision, offset + index));
  if (item.surface === 'incidents') return { targetId: item.targetId, fromUtc: item.fromUtc, toUtc: item.toUtc, items, state: items.length ? 'complete' : 'no_data' };
  const snapshotUtc = anchor?.snapshotUtc ?? new Date(Date.parse('2026-10-03T13:00:00Z') + generation * 1000).toISOString();
  const cutoffUtc = anchor?.sourceCutoffUtc ?? snapshotUtc, last = items.at(-1);
  const tieAt = last?.requestedAtUtc ?? last?.observedAtUtc ?? last?.occurredAtUtc;
  const tieId = last?.jobId ?? last?.hostId ?? last?.runId ?? last?.eventId ?? last?.packetId;
  const nextCursor = count === item.limit ? encode({ kind: 'm10.surface.v1', surface: item.surface, targetId: item.targetId, targetRevision: revision, generation, snapshotUtc, sourceCutoffUtc: cutoffUtc, fromUtc: item.fromUtc, toUtc: item.toUtc, filters: {}, tieAt, tieId, tieMetric: item.surface === 'host/metrics' ? last.metricKey : null, tieDimensions: item.surface === 'host/metrics' ? last.dimensions : null, tieFingerprint: item.surface.startsWith('replication/') ? 'b'.repeat(64) : null }) : null;
  return { targetId: item.targetId, surface: item.surface, fromUtc: item.fromUtc, toUtc: item.toUtc, items, state: items.length ? 'complete' : 'no_data', nextCursor, cutoffUtc, snapshotUtc, generation, targetRevision: revision };
}
