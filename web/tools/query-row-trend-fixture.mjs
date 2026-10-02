// Isolated synthetic query history for ranking trends. No protected SQL payloads.
const cookie = (request, name) => request.headers.cookie?.split(';').map(x => x.trim()).find(x => x.startsWith(name + '='))?.slice(name.length + 1);
const digest = value => value.toString(16).padStart(12, '0') + '0'.repeat(52);
const run = value => `99999999-9999-4999-8999-${String(value + 1).padStart(12, '0')}`;
const identity = index => ({ databaseId: index === 1 ? 6 : 5, queryFingerprint: digest(4096 + index), planFingerprint: digest(8192 + index) });
export function handleQueryRowTrendFixture(url, response, request, targets, send) {
  if (cookie(request, 'queryRowTrendFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/query-performance\/(.+)$/.exec(url.pathname);
  if (!route) return false;
  const target = targets.find(x => x.instanceId === route[1]);
  if (!target) { send(response, {}, 404); return true; }
  const targetId = target.instanceId, surface = route[2], fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  const start = Date.parse(fromUtc), end = Date.parse(toUtc), duration = end - start;
  const mode = cookie(request, 'queryRowTrendState') ?? 'normal';
  const count = cookie(request, 'queryRowTrendCount') === '35' ? 35 : 3;
  const offset = targets.indexOf(target) * 1000;
  if (surface === 'status') {
    send(response, { targetId, snapshotUtc: toUtc, source: 'query_store', sourceState: 'read_write', coverage: 'complete', fresh: true, truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: [{ databaseId: 5, databaseName: 'SYNTHETIC Orders' }, { databaseId: 6, databaseName: 'SYNTHETIC Warehouse' }] }); return true;
  }
  if (!Number.isFinite(start) || !Number.isFinite(end) || duration <= 0 || duration > 7 * 86400000) { send(response, {}, 400); return true; }
  const at = fraction => new Date(start + duration * fraction).toISOString();
  const observation = (index, metric = 'cpu') => ({ ...identity(index), source: 'query_store', sourceState: 'read_write', semantics: 'query_store_interval', metric, value: 5000 - index * 20, intervalStartUtc: fromUtc, intervalEndUtc: toUtc, coverage: mode === 'partial' ? 'truncated' : 'complete', fresh: true, truncated: mode === 'partial', contentAvailable: false, collectionRunId: run(index), observationKey: index.toString(16).padStart(32, '0') });
  if (surface === 'top') { send(response, { targetId, metric: url.searchParams.get('metric'), snapshotUtc: toUtc, fromUtc, toUtc, nextCursor: null, items: Array.from({ length: count }, (_, i) => observation(i, url.searchParams.get('metric'))) }); return true; }
  if (surface === 'waits/top') { send(response, { targetId, fromUtc, toUtc, category: url.searchParams.has('category') ? Number(url.searchParams.get('category')) : null, semantics: 'query_store_group_wait_delta', coverage: 'observed_groups_only', items: [0, 1].map(index => ({ ...identity(index), waitMilliseconds: String(1000 - index * 100), comparableRows: 2, sourceRows: 3, lastObservedAtUtc: at(.875) })) }); return true; }
  if (surface === 'cache/top') {
    send(response, { targetId, fromUtc, toUtc, metric: url.searchParams.get('metric'), semantics: 'plan_cache_delta', coverage: 'observed_entries_only', sourceFrames: 4, incompleteFrames: 1, comparableRows: 4, unavailableRows: 2, boundaryRows: 0, hasMore: false, items: [0, 1].map(index => ({ ...identity(index), databaseIncarnation: digest(12288 + index), cpuMilliseconds: String(200 - index * 20), durationMilliseconds: String(400 - index * 20), executions: '20', logicalReads: '4000', writes: '10', rows: '100', comparableRows: 2, firstObservedAtUtc: at(.125), lastObservedAtUtc: at(.875) })) }); return true;
  }
  const trend = /^databases\/(\d+)\/history\/([0-9a-f]{64})\/plans\/([0-9a-f]{64})\/trend$/.exec(surface);
  if (trend) {
    if (mode === 'unavailable') { send(response, {}, 503); return true; }
    const buckets = []; for (let tick = Math.floor(start / 3600000) * 3600000; tick < end; tick += 3600000) buckets.push(new Date(tick).toISOString());
    const points = buckets.flatMap((bucketStartUtc, index) => {
      if (index === 1) return [];
      const reset = index === 2, partial = mode === 'partial', sourceRows = partial ? 2 : 1;
      return [{ bucketStartUtc, sourceRows, runtimeComparableRows: reset ? 0 : 1, runtimeBaselineRows: partial ? 1 : 0, runtimeResetRows: reset ? 1 : 0, runtimeAmbiguousRows: 0, cpuMilliseconds: reset ? null : String(120 + index * 40 + offset), durationMilliseconds: reset ? null : String(240 + index * 80 + offset), executions: reset ? null : '10', logicalReads: reset ? null : String(4000 + index * 1000), writes: reset ? null : '10', rows: reset ? null : '100', waitComparableRows: reset ? 0 : 1, waitBaselineRows: 0, waitResetRows: reset ? 1 : 0, waitUnavailableRows: partial ? 1 : 0, waitStaleRows: 0, waitCategories: reset ? {} : { 3: String(80 + index * 40), 6: '20' } }];
    });
    send(response, { targetId, databaseId: Number(trend[1]), queryFingerprint: trend[2], planFingerprint: trend[3], fromUtc, toUtc, semantics: 'query_store_group_delta', revisionScope: 'current', coverage: 'observed_groups_only', bucketSeconds: 3600, points }); return true;
  }
  const cache = /^cache\/databases\/(\d+)\/history\/([0-9a-f]{64})\/plans\/([0-9a-f]{64})$/.exec(surface);
  if (cache) {
    if (mode === 'unavailable') { send(response, {}, 503); return true; }
    const items = [3, 2, 1, 0].map(index => {
      const comparable = index === 0 || index === 3, observedAtUtc = at((index + 1) / 5);
      return { collectionRunId: run(index), cacheEntryFingerprint: digest(16384), sourceEpoch: digest(20480), observedAtUtc, committedAtUtc: observedAtUtc, captureComplete: index !== 1, transition: comparable ? 'comparable' : index === 2 ? 'reset' : 'stale', intervalFromUtc: comparable ? at((index + .5) / 5) : null, intervalToUtc: comparable ? observedAtUtc : null, cpuMilliseconds: comparable ? String(100 + index * 50 + offset) : null, durationMilliseconds: comparable ? String(200 + index * 50 + offset) : null, executions: comparable ? '10' : null, logicalReads: comparable ? '1000' : null, writes: comparable ? '10' : null, rows: comparable ? '100' : null };
    });
    send(response, { targetId, databaseId: Number(cache[1]), databaseIncarnation: url.searchParams.get('databaseIncarnation'), queryFingerprint: cache[2], planFingerprint: cache[3], fromUtc, toUtc, snapshotUtc: toUtc, revision: 1, totalRows: items.length, nextCursor: null, semantics: 'plan_cache_delta', coverage: 'observed_entries_only', items }); return true;
  }
  const history = /^databases\/(\d+)\/history\/([0-9a-f]{64})$/.exec(surface);
  if (history) { send(response, { targetId, databaseId: Number(history[1]), queryFingerprint: history[2], fromUtc, toUtc, snapshotUtc: toUtc, nextCursor: null, items: [{ ...observation(0), databaseId: Number(history[1]), queryFingerprint: history[2], cpuMilliseconds: 120, durationMilliseconds: 240, executions: 10, logicalReads: 4000 }] }); return true; }
  return false;
}
