// Synthetic native shapes and cursor codecs. No external backend or connected evidence.
export const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
export const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const run = '99999999-9999-4999-8999-000000000001';
const databaseRun = (id, generation) => `${id === beta ? '88888888-8888-4888-8888-' : '99999999-9999-4999-8999-'}${String(generation).padStart(12, '0')}`.replaceAll('-', '');
const alertId = index => `aaaaaaaa-aaaa-4aaa-8aaa-${String(index).padStart(12, '0')}`;
export const cursors = {
  servers: Buffer.from('alpha-0100\n55555555-5555-4555-8555-000000000100').toString('base64url'),
  alerts: Buffer.from(`2026-10-02T11:00:00.1234560+00:00|2026-10-02T10:00:00.1234560+00:00|${alpha}|${alertId(1)}`).toString('base64'),
  databases: (id, generation = 1) => Buffer.from(`d\n${id.replaceAll('-', '')}\n${databaseRun(id, generation)}\n1\n29`).toString('base64url'),
};
function collector(count) {
  return { collectorId: 'database.health', manifestVersion: 1, outputSchemaVersion: 1, state: 'current', reason: 'none',
    lastExecutionOutcome: 'succeeded', circuitState: 'closed', durationMilliseconds: 1, retryCount: 0, sourceRows: count, outputRows: count,
    insertedRows: count, duplicateRows: 0, rejectedRows: 0, responseBytes: 5000, persistedBytes: 5000,
    sampleLossKind: null, minimumLostItems: null, lossCountIsExact: null, minimumLostBytes: null, hasVisibilityGap: false,
    scheduledAtUtc: from, lastAttemptAtUtc: from, lastSuccessAtUtc: from, nextDueAtUtc: to };
}
function server(index) {
  return { instanceId: index === 1 ? alpha : `55555555-5555-4555-8555-${String(index).padStart(12, '0')}`,
    instanceKey: `alpha-${String(index).padStart(4, '0')}`, displayName: `SYNTHETIC Alpha server ${index}`, host: `alpha${index}.invalid`, tcpPort: 1433,
    authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated', lifecycle: 'active', capabilityStatus: 'supported',
    capabilityReasons: [], configurationRevision: 1, discoveryRequestedAtUtc: from, lastDiscoveryAtUtc: from };
}
export function nativeValue(item, session) {
  const name = item.id === beta ? 'Beta' : 'Alpha';
  if (item.kind === 'servers') return { items: Array.from({ length: item.cursor ? 1 : 100 }, (_, i) => server(item.cursor ? 101 : i + 1)),
    nextCursor: item.cursor ? null : cursors.servers };
  if (item.kind === 'alerts') return { snapshotUtc: to, items: Array.from({ length: item.cursor ? 1 : 100 }, (_, i) => ({
    targetId: alpha, targetName: 'SYNTHETIC Alpha', alertId: alertId(item.cursor ? 0 : 100 - i),
    ruleId: 'bbbbbbbb-bbbb-4bbb-8bbb-000000000001', ruleName: `SYNTHETIC blocking rule ${item.cursor ? 0 : 100 - i}`,
    state: 'firing', firstObservedUtc: from, firedUtc: from, deliverySuppressed: false, value: 2, reason: null })),
    nextCursor: item.cursor ? null : cursors.alerts };
  if (item.kind === 'databases') {
    const count = item.cursor ? 1 : 25, summary = collector(count);
    const ids = Array.from({ length: count }, (_, i) => item.cursor ? 30 : i + 5);
    return { instanceId: item.id, repositoryTimeUtc: to, collector: summary, items: ids.map(databaseId => ({
      databaseId, name: `SYNTHETIC ${name} generation ${session.generation} database ${databaseId}`, state: 'online', recoveryModel: 'full', userAccess: 'multi_user',
      isReadOnly: false, compatibilityLevel: 160, logReuseWaitDescription: null, observedAtUtc: from, collector: summary })),
      nextCursor: item.cursor ? null : cursors.databases(item.id, session.generation) };
  }
  if (item.kind === 'catalog') return { targetId: item.id, snapshotUtc: to, source: 'query_store', sourceState: 'read_write', coverage: 'complete',
    fresh: true, truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: [{ databaseId: 5, databaseName: `SYNTHETIC ${name} Orders` }] };
  if (item.kind === 'history') return { targetId: item.id, databaseId: 5, queryFingerprint: item.hash, snapshotUtc: item.toUtc,
    fromUtc: item.fromUtc, toUtc: item.toUtc, nextCursor: null, items: [{ databaseId: 5, queryFingerprint: item.hash, planFingerprint: null,
      source: 'query_store', sourceState: 'read_write', semantics: 'query_store_interval', cpuMilliseconds: 25, durationMilliseconds: 50, executions: 1,
      intervalStartUtc: item.fromUtc, intervalEndUtc: item.toUtc, coverage: 'complete', fresh: true, truncated: false, contentAvailable: false,
      collectionRunId: run, observationKey: 'b'.repeat(32) }] };
  throw new Error(`Unregistered fixture kind ${item.kind}`);
}
export function registered(url, session) {
  const keys = [...url.searchParams.keys()].sort();
  if (new Set(keys).size !== keys.length) return;
  const cursor = url.searchParams.get('cursor');
  for (const [path, kind] of [['/api/v1/observation-targets', 'servers'], ['/api/v1/alerts/active', 'alerts']]) {
    if (url.pathname === path && keys.join('|') === (cursor ? 'cursor|limit' : 'limit') && url.searchParams.get('limit') === '100'
      && (!cursor || cursor === cursors[kind])) return { kind, cursor };
  }
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) return;
  const id = match[1], suffix = match[2];
  if (suffix === 'health/databases' && keys.join('|') === (cursor ? 'cursor|limit' : 'limit') && url.searchParams.get('limit') === '25'
    && (!cursor || cursor === cursors.databases(id, session.generation))) return { kind: 'databases', id, cursor };
  if (suffix === 'query-performance/status' && keys.length === 0) return { kind: 'catalog', id };
  const history = /^query-performance\/databases\/5\/history\/([ab]{64})$/.exec(suffix);
  if (history && keys.join('|') === 'fromUtc|limit|toUtc' && url.searchParams.get('limit') === '200'
    && url.searchParams.get('fromUtc') === from && url.searchParams.get('toUtc') === to)
    return { kind: 'history', id, hash: history[1], fromUtc: from, toUtc: to };
}
