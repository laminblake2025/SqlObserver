import { activityFixture, alpha, beta, evidence, wait, waitHistoryItem, unavailableLivePage } from './activityFixture.mjs';
export { alpha, beta };
export const origin = 'http://127.0.0.1:4328';
export const now = '2026-09-30T14:00:00.000Z';
const run = '33333333-3333-4333-8333-333333333333', baseline = '44444444-4444-4444-8444-444444444444';
const encode = fields => Buffer.from(fields.join('\n')).toString('base64url');
const roundTrip = value => {
  const match = /^(.*T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/u.exec(value);
  if (!match) throw new Error('Invalid native UTC timestamp');
  return `${match[1]}.${(match[2] ?? '').padEnd(7, '0')}+00:00`;
};
const historyRun = index => `33333333-3333-4333-8333-${(index + 1).toString(16).padStart(12, '0')}`;
export function sourceOf(url) {
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(activity\/(?:sessions|requests|waits(?:\/history|\/trend)?|blocking\/(?:current|history)|live(?:\/history)?)|query-performance\/waits\/top)$/u.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) throw new Error('Unregistered native source');
  return { target: match[1], source: match[2] };
}
const types = ['ASYNC_IO_COMPLETION', 'ASYNC_NETWORK_IO', 'BACKUPIO', 'CXCONSUMER', 'CXPACKET', 'HADR_SYNC_COMMIT', 'IO_COMPLETION', 'LATCH_EX', 'LATCH_SH', 'LCK_M_IS', 'LCK_M_IX', 'LCK_M_S', 'LCK_M_U', 'LCK_M_X', 'LOGMGR_QUEUE', 'MEMORY_ALLOCATION_EXT', 'PAGEIOLATCH_EX', 'PAGEIOLATCH_SH', 'PAGELATCH_EX', 'PAGELATCH_SH', 'RESOURCE_SEMAPHORE', 'SOS_SCHEDULER_YIELD', 'THREADPOOL', 'WRITE_COMPLETION', 'WRITELOG'];
export function nativeCurrent(target = alpha, next = false, marker = 'LCK_M_X') {
  const page = activityFixture(target).waits;
  const items = next ? [wait({ waitType: 'XE_TIMER_EVENT', observedAtUtc: '2026-09-30T13:30:00Z' })]
    : types.map(type => wait({ waitType: type === 'LCK_M_X' ? marker : type, observedAtUtc: '2026-09-30T13:30:00Z' }));
  return { ...page, repositoryTimeUtc: now, evidence: evidence({ collectorId: 'engine.waits', completedAtUtc: '2026-09-30T13:30:00Z' }), items,
    nextCursor: next ? null : encode(['w', target.replaceAll('-', ''), run.replaceAll('-', ''), '1', baseline.replaceAll('-', ''), items.at(-1).waitType]) };
}
export function nativeHistory(url, next = false) {
  const { target } = sourceOf(url), fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  const center = Math.floor((Date.parse(fromUtc) + Date.parse(toUtc)) / 2);
  const items = Array.from({ length: next ? 1 : 25 }, (_, index) => {
    const position = next ? 30 : index, observedAtUtc = new Date(center - position * 1000).toISOString();
    return waitHistoryItem({ waitType: next ? 'PAGEIOLATCH_SH' : 'LCK_M_X', observedAtUtc }, { runId: historyRun(position), completedAtUtc: observedAtUtc });
  });
  return { ...activityFixture(target)['waits/history'], repositoryTimeUtc: now, fromUtc, toUtc, items,
    nextCursor: next ? null : encode(['vh', target.replaceAll('-', ''), roundTrip(fromUtc), roundTrip(toUtc), roundTrip(items.at(-1).wait.observedAtUtc), items.at(-1).evidence.runId.replaceAll('-', ''), items.at(-1).wait.waitType]) };
}
export function nativeTrend(url) {
  const { target } = sourceOf(url), fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  const points = [];
  const end = Date.parse(toUtc) + (/\.\d{4,}/u.test(toUtc) ? 1 : 0);
  for (let time = Math.floor(Date.parse(fromUtc) / 300000) * 300000; time < end; time += 300000)
    for (const category of ['Lock', 'I/O', 'CPU/signal', 'Memory', 'Parallelism', 'Log', 'Other']) points.push({ bucketStartUtc: new Date(time).toISOString(), category, waitMilliseconds: category === 'Lock' ? '55' : '0', runCount: 1, missingSummaryRuns: 0, partialRuns: 0, incomparableTypes: 0, comparableTypes: 1 });
  return { instanceId: target, fromUtc, toUtc, repositoryTimeUtc: now, points };
}
export function nativeRanking(url) {
  const { target } = sourceOf(url), fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  return { targetId: target, fromUtc, toUtc, category: Number(url.searchParams.get('category')), semantics: 'query_store_group_wait_delta', coverage: 'observed_groups_only',
    items: [{ databaseId: 7, queryFingerprint: 'a'.repeat(64), planFingerprint: 'b'.repeat(64), waitMilliseconds: '55', comparableRows: 1, sourceRows: 1, lastObservedAtUtc: new Date((Date.parse(fromUtc) + Date.parse(toUtc)) / 2).toISOString() }] };
}
export function nativeLive(url) {
  const snapshotId = url.searchParams.get('snapshot') ?? '55555555-5555-4555-8555-555555555555';
  return { ...unavailableLivePage(), snapshotId, observedUtc: '2026-09-30T13:30:00Z', repositoryTimeUtc: now, state: 'available',
    rows: [{ identity: 'c'.repeat(64), sessionId: 71, requestId: 0, databaseId: 7, databaseName: 'Synthetic inventory', login: null, clientHost: null, application: null, status: 'suspended', command: 'SELECT', isUser: true, waitType: 'LCK_M_X', blocker: 63, cpuMs: '1', memoryBytes: '1024', reads: '2', writes: '0', logicalReads: '3', elapsedMs: '55', queryId: null, queryState: 'unavailable', engineStartup: '2026-09-30T10:00:00Z', sessionLogin: '2026-09-30T13:00:00Z', requestStart: '2026-09-30T13:25:00Z', delta: null }], databases: [{ id: 7, name: 'Synthetic inventory' }] };
}
export function nativeResponse(url) {
  const { target, source } = sourceOf(url), keys = [...url.searchParams.keys()];
  const validate = allowed => { if (keys.some(key => !allowed.includes(key))) throw new Error('Unsupported native parameter'); };
  if (source === 'activity/waits') { validate(['limit', 'cursor']); if (url.searchParams.get('limit') !== '25') throw new Error('Invalid page limit'); if (url.searchParams.has('cursor') && url.searchParams.get('cursor') !== nativeCurrent(target).nextCursor) throw new Error('Invalid native current cursor'); return nativeCurrent(target, url.searchParams.has('cursor')); }
  if (source === 'activity/waits/history') { validate(['limit', 'fromUtc', 'toUtc', 'cursor']); if (url.searchParams.get('limit') !== '25' || !url.searchParams.get('fromUtc') || !url.searchParams.get('toUtc')) throw new Error('Invalid native history bounds'); if (url.searchParams.has('cursor') && url.searchParams.get('cursor') !== nativeHistory(url).nextCursor) throw new Error('Invalid native history cursor'); return nativeHistory(url, url.searchParams.has('cursor')); }
  if (source === 'activity/waits/trend') { validate(['fromUtc', 'toUtc']); return nativeTrend(url); }
  if (source === 'query-performance/waits/top') { validate(['fromUtc', 'toUtc', 'limit', 'category']); if (url.searchParams.get('limit') !== '25') throw new Error('Invalid ranking limit'); return nativeRanking(url); }
  if (source === 'activity/live/history') { validate(['from', 'to']); return [{ id: '55555555-5555-4555-8555-555555555555', observedUtc: '2026-09-30T13:30:00Z', truncated: false }]; }
  if (source === 'activity/live') { validate(['includeIdle', 'includeSystem', 'blockedOnly', 'sort', 'descending', 'databaseId', 'snapshot', 'cursor', 'login', 'application', 'status']); return nativeLive(url); }
  validate(source.endsWith('/history') ? ['limit', 'fromUtc', 'toUtc', 'cursor'] : ['limit']);
  const fixture = activityFixture(target)[source.replace('activity/', '')];
  const observedAtUtc = source.endsWith('/history') ? new Date((Date.parse(url.searchParams.get('fromUtc')) + Date.parse(url.searchParams.get('toUtc'))) / 2).toISOString() : '2026-09-30T13:30:00.000Z';
  const items = fixture.items.map(item => item.edge ? { ...item, edge: { ...item.edge, observedAtUtc }, evidence: { ...item.evidence, completedAtUtc: observedAtUtc } } : { ...item, observedAtUtc });
  return { ...fixture, items, evidence: { ...fixture.evidence, completedAtUtc: observedAtUtc }, repositoryTimeUtc: now, ...(source.endsWith('/history') ? { fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') } : {}) };
}
export const json = (route, value, status = 200) => route.fulfill({ status, contentType: 'application/json', headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(value) });
