export const alpha = '11111111-1111-4111-8111-000000000001';
export const beta = '11111111-1111-4111-8111-000000000002';
export const firstWindow = { fromUtc: '2026-01-15T12:00:00.123456Z', toUtc: '2026-01-15T13:00:00.123456Z' };
export const secondWindow = { fromUtc: '2026-01-15T12:30:00.123456Z', toUtc: '2026-01-15T13:30:00.123456Z' };
export function nativePle(item, value = 111, nodes = [0, 1]) {
  const observed = new Date(Date.parse(item.fromUtc) + 600000).toISOString();
  const bucket = new Date(Math.floor(Date.parse(item.fromUtc) / 3600000) * 3600000).toISOString();
  const selectedNodeId = item.nodeId ?? nodes[0] ?? null;
  const point = { bucketStartUtc: bucket, sampleCount: 2, minimumSeconds: value - 1, averageSeconds: value, maximumSeconds: value + 1 };
  return { targetId: item.target, fromUtc: item.fromUtc, toUtc: item.toUtc,
    semantics: 'numa_node_page_life_expectancy_gauge', unit: 'seconds', revisionScope: 'current', bucketSeconds: 3600,
    selectedNodeId, visibilityGapCount: 0, nodes: nodes.map(nodeId => ({ nodeId, sampleCount: 2, lastObservedAtUtc: observed })),
    points: selectedNodeId !== null && nodes.includes(selectedNodeId) ? [point] : [], aggregatePoints: [point] };
}
export function nativeFileTrend(item, value = 111) {
  return { targetId: item.target, databaseId: 7, fileId: 1, fromUtc: item.fromUtc, toUtc: item.toUtc,
    semantics: 'file_counter_delta', revisionScope: 'current', bucketSeconds: 3600,
    points: [{ bucketStartUtc: new Date(Math.floor(Date.parse(item.fromUtc) / 3600000) * 3600000).toISOString(),
      sourceRows: 2, comparableRows: 1, baselineRows: 1, resetRows: 0, gapRows: 0, legacyRows: 0,
      readOperations: '10', readStallMilliseconds: String(value * 10), writeOperations: '20', writeStallMilliseconds: String(value * 20) }] };
}
export function nativeOverview(item, value = 111) {
  const displayName = item.target === alpha ? 'SYNTHETIC Alpha' : 'SYNTHETIC Beta';
  const observedAtUtc = new Date(Date.parse(item.fromUtc) + 600000).toISOString();
  const measurement = { value, state: 'current', observedAtUtc };
  return { targetId: item.target, fromUtc: item.fromUtc, toUtc: item.toUtc,
    refreshedAtUtc: '2026-01-15T14:00:00.123456Z', excludedTargets: 0,
    targets: [{ targetId: item.target, displayName, lifecycle: 'active', environment: null, tags: [], groupingRevision: 0, canManageGrouping: false }],
    evidence: [{ targetId: item.target, displayName, collectionState: 'current', lastObservedUtc: observedAtUtc,
      activeAlerts: measurement, blockedSessions: measurement, deadlocks: { ...measurement, state: 'observed' }, gaps: [], resources: [], issues: [], backupSummary: null,
      series: [{ targetId: item.target, label: 'Memory grants pending', metric: 'engine.memory_grants_pending', unit: 'queries', state: 'observed', dimension: null,
        points: [{ timeUtc: observedAtUtc, value, samples: 2 }] }] }] };
}
