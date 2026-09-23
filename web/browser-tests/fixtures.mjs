// Synthetic, bounded API evidence for browser behavior. Never a live-server certification.
export const targetA = '11111111-1111-4111-8111-111111111111';
export const targetB = '22222222-2222-4222-8222-222222222222';
export const eventA = '33333333-3333-4333-8333-333333333333';
export const eventB = '44444444-4444-4444-8444-444444444444';
export const queryHash = 'a'.repeat(64);
export const planA = 'b'.repeat(64);
export const planB = 'c'.repeat(64);
export const toUtc = new Date(Date.now() - 60_000).toISOString();
export const fromUtc = new Date(Date.parse(toUtc) - 3_600_000).toISOString();
export const routeFor = (page, target = targetA) => `/#/${page}?${new URLSearchParams({ target, range: 'custom', from: fromUtc, to: toUtc })}`;

export function targets(count = 2) {
  return Array.from({ length: count }, (_, index) => ({
    instanceId: index === 0 ? targetA : index === 1 ? targetB : `${String(index + 1).padStart(8, '0')}-1111-4111-8111-111111111111`,
    instanceKey: `fixture-${index}`, displayName: `Fixture SQL ${index + 1}`, host: `fixture${index + 1}.invalid`,
    tcpPort: 1433, authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
    lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1,
    discoveryRequestedAtUtc: fromUtc, lastDiscoveryAtUtc: toUtc,
  }));
}

export function overview(url, inventory = targets()) {
  const selected = url.searchParams.get('targetId');
  return {
    targetId: selected, fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
    refreshedAtUtc: toUtc, excludedTargets: 0,
    targets: inventory.map(t => ({ targetId: t.instanceId, displayName: t.displayName, lifecycle: t.lifecycle })),
    evidence: inventory.filter(t => !selected || selected === t.instanceId).map(t => ({
      targetId: t.instanceId, displayName: t.displayName, collectionState: 'current', lastObservedUtc: toUtc,
      activeAlerts: { value: 0, state: 'current', observedAtUtc: toUtc },
      blockedSessions: { value: 0, state: 'current', observedAtUtc: toUtc },
      deadlocks: { value: 1, state: 'current', observedAtUtc: toUtc },
      issues: [], resources: [], gaps: [],
      series: [{ targetId: t.instanceId, label: t.displayName, metric: 'engine.user_connections', unit: 'connections', state: 'current', dimension: null,
        points: Array.from({ length: 120 }, (_, i) => ({ timeUtc: new Date(Date.parse(fromUtc) + i * 30_000).toISOString(), value: i % 12, samples: 1 })) }],
    })),
  };
}

const collector = {
  collectorId: 'engine.core', state: 'current', reason: 'none', manifestVersion: 2, outputSchemaVersion: 2,
  lastExecutionOutcome: 'succeeded', circuitState: 'closed', durationMilliseconds: 5, retryCount: 0,
  sourceRows: 1, outputRows: 1, insertedRows: 1, duplicateRows: 0, rejectedRows: 0, responseBytes: 100, persistedBytes: 100,
  sampleLossKind: null, minimumLostItems: null, lossCountIsExact: null, minimumLostBytes: null, hasVisibilityGap: false,
  scheduledAtUtc: fromUtc, lastAttemptAtUtc: toUtc, lastSuccessAtUtc: toUtc, nextDueAtUtc: toUtc,
};
const deadlock = id => ({ eventId: id, occurredAtUtc: toUtc, collectedAtUtc: toUtc, fingerprint: 'd'.repeat(64), participantCount: 2, relationCount: 2, parseTruncated: false });
const queryRow = (plan, value, key, source = 'query_store') => ({
  databaseId: 5, queryFingerprint: queryHash, planFingerprint: plan, metric: 'cpu', value,
  source, sourceState: source === 'query_store' ? 'read_write' : 'disabled', semantics: source === 'query_store' ? 'query_store_interval' : 'plan_cache_cumulative',
  intervalStartUtc: fromUtc, intervalEndUtc: toUtc, coverage: 'complete', fresh: true, truncated: false, contentAvailable: false,
  collectionRunId: targetA, observationKey: key.repeat(32),
});

export async function mockEvidence(page, { count = 2 } = {}) {
  const inventory = targets(count);
  const state = { requests: [], failFiles: false, failOverview: false, failQueries: false, overviewGate: null, targetGate: null, alertGate: null, alertState: null };
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url());
    state.requests.push(url);
    const path = url.pathname;
    const id = path.split('/')[4];
    const json = body => route.fulfill({ json: body });
    const failure = () => route.fulfill({ status: 503, headers: { 'X-Correlation-ID': eventA }, json: { code: 'request_failed' } });
    if (path === '/api/v1/observation-targets') return json({ items: inventory, nextCursor: null });
    if (path === '/api/v1/overview') {
      if (state.overviewGate) await state.overviewGate;
      return state.failOverview ? failure() : json(overview(url, inventory));
    }
    if (/\/observation-targets\/[^/]+$/.test(path)) return json(inventory.find(t => t.instanceId === id));
    if (id === targetA && state.targetGate) await state.targetGate;
    if (path.endsWith('/health')) return json({ instanceId: id, state: 'current', repositoryTimeUtc: toUtc, collectors: [collector],
      coreMetrics: [{ sampleId: eventA, metricId: 'engine.user_connections', observedAtUtc: toUtc, value: id === targetA ? 42 : 77, dimensions: [] }] });
    if (path.endsWith('/health/databases')) return json({ instanceId: id, repositoryTimeUtc: toUtc, collector, items: [], nextCursor: null });
    if (path.endsWith('/health/files')) return state.failFiles ? failure() : json({ instanceId: id, repositoryTimeUtc: toUtc, collector, items: [], nextCursor: null });
    if (path.endsWith('/alerts/active')) {
      const items = state.alertState ? [{ alertId: eventA, ruleId: eventB, ruleName: 'Blocked requests', state: state.alertState, firstObservedUtc: fromUtc, firedUtc: toUtc, deliverySuppressed: false, value: 5 }] : [];
      if (state.alertGate) await state.alertGate;
      return json({ targetId: id, snapshotUtc: toUtc, items, nextCursor: null });
    }
    if (path.endsWith('/acknowledge')) { state.alertState = 'acknowledged'; return json({}); }
    if (path.endsWith('/deadlocks')) {
      if (url.searchParams.get('fromUtc') !== fromUtc || url.searchParams.get('toUtc') !== toUtc) return route.fulfill({ status: 400 });
      const second = url.searchParams.has('cursor');
      return json({ targetId: id, repositoryTimeUtc: toUtc, items: [deadlock(second ? eventB : eventA)], nextCursor: second ? null : 'second-page' });
    }
    if (/\/deadlocks\/[^/]+$/.test(path)) return json({ summary: deadlock(path.split('/').at(-1)),
      participants: [{ sessionId: 51, isVictim: true }, { sessionId: 52, isVictim: false }],
      relations: [{ blockerSessionId: 51, waiterSessionId: 52, resourceCategory: 'key', lockMode: 'X' }, { blockerSessionId: 52, waiterSessionId: 51, resourceCategory: 'key', lockMode: 'X' }] });
    if (path.endsWith('/query-performance/status')) return json({ targetId: id, snapshotUtc: toUtc, source: 'mixed', sourceState: 'mixed', coverage: 'complete', fresh: true, truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: [{ databaseId: 5, databaseName: 'Sales' }, { databaseId: 6, databaseName: 'Warehouse' }] });
    if (path.endsWith('/query-performance/top')) {
      if (state.failQueries) return failure();
      let rows = [queryRow(planA, 111, 'a'), queryRow(planB, 222, 'b'), queryRow('e'.repeat(64), 333, 'c', 'plan_cache')];
      if (url.searchParams.has('databaseId')) rows = rows.filter(r => r.databaseId === Number(url.searchParams.get('databaseId')));
      if (url.searchParams.has('source')) rows = rows.filter(r => r.source === url.searchParams.get('source'));
      const from = url.searchParams.get('fromUtc') ?? fromUtc, to = url.searchParams.get('toUtc') ?? toUtc;
      return json({ targetId: id, snapshotUtc: toUtc, metric: 'cpu', fromUtc: from, toUtc: to, items: rows.map(row => ({ ...row, intervalStartUtc: from, intervalEndUtc: to })), nextCursor: null });
    }
    if (path.includes('/history/')) return json({ targetId: id, databaseId: 5, queryFingerprint: queryHash, snapshotUtc: toUtc, fromUtc, toUtc, items: [], nextCursor: null });
    if (path.includes('/plans/')) return json({ targetId: id, databaseId: 5, queryFingerprint: queryHash, planFingerprint: path.split('/').at(-1), source: 'query_store', observedAtUtc: toUtc, coverage: 'complete', contentAvailable: false });
    return failure();
  });
  return state;
}
