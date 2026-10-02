// Opt-in native DTOs for UI validation only. No connected SQL or protected payloads.
import { syntheticCollector, syntheticFile } from './health-contract-fixture.mjs';
import { handleQueryRowTrendFixture } from './query-row-trend-fixture.mjs';
const cookie = (request, name) => request.headers.cookie?.split(';').map(value => value.trim()).find(value => value.startsWith(name + '='))?.slice(name.length + 1);
const digest = value => value.toString(16).padStart(12, '0') + '0'.repeat(52);
export const nativeFileObservedUtc = '2026-10-02T11:30:00.123456Z';
export const nativeFileCursor = 'bmF0aXZlLWZpbGVzOjI=';
export function handleDashboardNativeInsightsFixture(url, response, request, targets, send) {
  if (cookie(request, 'nativeInsightsFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if (!route) return false;
  const target = targets.find(item => item.instanceId === route[1]);
  if (!target) return false;
  const surface = route[2], mode = cookie(request, 'nativeInsightsMode') ?? 'normal', offset = targets.indexOf(target);
  if (surface === 'health/files') {
    if (mode === 'file-error') { send(response, {}, 503); return true; }
    if (mode === 'file-forbidden') { send(response, {}, 403); return true; }
    const collector = syntheticCollector(nativeFileObservedUtc);
    if (mode === 'file-stale') Object.assign(collector, { state: 'stale', reason: 'evidence_stale' });
    const make = (databaseId, fileId, logicalName, sizeBytes, maximumSizeBytes, growthBytes, growthPercent) => syntheticFile({ databaseId, fileId,
      logicalName: logicalName + (offset ? '_02' : ''), fileType: logicalName.endsWith('_log') ? 'log' : 'rows', sizeBytes, maximumSizeBytes, growthBytes, growthPercent,
      readCount: '100', writeCount: '50', readStallMilliseconds: '200', writeStallMilliseconds: '100' }, nativeFileObservedUtc, collector);
    const files = [make(5, 1, 'SYNTHETIC_exact_data', '9223372036854775807', '9223372036854775807', '67108864', 0),
      make(5, 2, 'SYNTHETIC_percent_log', '1073741824', null, '0', 10),
      make(6, 1, 'SYNTHETIC_other_database', '2147483648', '4294967296', '134217728', 0),
      make(5, 3, 'SYNTHETIC_zero_growth', '536870912', '1073741824', '0', 0)];
    const cursor = url.searchParams.get('cursor');
    if (cursor !== null && cursor !== nativeFileCursor) { send(response, {}, 400); return true; }
    send(response, { instanceId: target.instanceId, repositoryTimeUtc: '2026-10-02T12:00:00.654321Z', collector,
      items: mode === 'file-empty' ? [] : cursor ? files.slice(3) : files.slice(0, 3), nextCursor: mode === 'file-empty' || cursor ? null : nativeFileCursor });
    return true;
  }
  if (!surface.startsWith('query-performance/')) return false;
  // Plan metadata is an identity read, with no window parameters. The trend
  // fixture's window guard must not reject this separate native endpoint.
  const plan = /^query-performance\/databases\/(\d+)\/plans\/([0-9a-f]{64})$/.exec(surface);
  if (plan) {
    const queryFingerprint = url.searchParams.get('queryFingerprint');
    if (!queryFingerprint || !/^[0-9a-f]{64}$/.test(queryFingerprint)) { send(response, {}, 400); return true; }
    send(response, { targetId: target.instanceId, databaseId: Number(plan[1]), queryFingerprint, planFingerprint: plan[2],
      source: 'query_store', observedAtUtc: nativeFileObservedUtc, coverage: 'complete', contentAvailable: false });
    return true;
  }
  return handleQueryRowTrendFixture(url, response, request, targets, (res, value, status = 200) => {
    const queryHistory = /^query-performance\/databases\/(\d+)\/history\/([0-9a-f]{64})$/.exec(surface);
    if (queryHistory && value.items) {
      const index = Number.parseInt(queryHistory[2].slice(0, 12), 16) - 4096;
      value.items = value.items.map(item => ({ ...item, databaseId: Number(queryHistory[1]), queryFingerprint: queryHistory[2],
        planFingerprint: digest(8192 + index), collectionRunId: `99999999-9999-4999-8999-${String(index + 1).padStart(12, '0')}`, observationKey: index.toString(16).padStart(32, '0') }));
    }
    const trend = /^query-performance\/databases\/\d+\/history\/([0-9a-f]{64})\/plans\/[0-9a-f]{64}\/trend$/.exec(surface);
    if (trend && value.points) {
      const index = Number.parseInt(trend[1].slice(0, 12), 16) - 4096;
      value.points = value.points.map(point => ({ ...point, waitCategories: Object.keys(point.waitCategories).length ? {
        3: mode === 'unsafe-waits' ? '9007199254740993' : String(BigInt(point.waitCategories[3]) + BigInt(index * 1000)), 6: point.waitCategories[6],
      } : {} }));
    }
    send(res, value, status);
  });
}
