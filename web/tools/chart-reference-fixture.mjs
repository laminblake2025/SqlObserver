// Loopback synthetic chart QA; never part of production routing or authorization.
const observations = [];
const listingStartedUtc = '2026-09-30T00:00:00Z';
let delayMilliseconds = 0;
const cookie = (request, name) => request.headers.cookie?.match(new RegExp(`${name}=([a-z]+)`))?.[1];
export async function handleChartReferenceFixture(url, res, request, targets, send) {
  if (url.pathname === '/__chart_reference_fixture/observations') {
    if (url.searchParams.has('delay')) delayMilliseconds = Math.max(0, Math.min(2500, Number(url.searchParams.get('delay')) || 0));
    send(res, { observations, delayMilliseconds }); return true;
  }
  if (url.pathname === '/__chart_reference_fixture') {
    const role = url.searchParams.get('role') ?? 'viewer', mode = url.searchParams.get('mode') ?? 'normal';
    if (!['viewer', 'admin'].includes(role) || !['normal', 'empty', 'error', 'future', 'gap', 'partial', 'unavailable', 'foreign'].includes(mode)) { send(res, {}, 400); return true; }
    res.writeHead(302, { 'set-cookie': [`chartReferenceRole=${role}; SameSite=Strict; Path=/`, `chartReferenceMode=${mode}; SameSite=Strict; Path=/`], location: '/' }); res.end(); return true;
  }
  const role = cookie(request, 'chartReferenceRole');
  if (url.pathname === '/api/v1/me' && role) {
    const targetId = url.searchParams.get('targetId'), roles = role === 'admin' ? ['Viewer', 'TargetAdministrator'] : ['Viewer'];
    send(res, { active: true, targetId, grantedRoles: roles, allTargetRoles: roles, targetRoles: targetId ? roles : [] }); return true;
  }
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/(analytics\/(?:sql-baselines|baselines)|alerts\/rules)$/.exec(url.pathname);
  if (!route) return false;
  const targetId = route[1], mode = cookie(request, 'chartReferenceMode') ?? 'normal';
  if (!targets.some(target => target.instanceId === targetId)) { send(res, {}, 404); return true; }
  observations.push({ targetId, surface: route[2], role: role ?? 'viewer', query: Object.fromEntries(url.searchParams) });
  if (delayMilliseconds) await new Promise(resolve => setTimeout(resolve, delayMilliseconds));
  if (mode === 'error') { send(res, {}, 503); return true; }
  if (route[2] === 'analytics/sql-baselines') {
    const fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc'), metricKey = url.searchParams.get('metricKey');
    const memory = metricKey?.endsWith('_bytes'), cpu = metricKey?.endsWith('_percent');
    const identity = { catalogVersion: 2, catalogChecksum: '702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338',
      algorithmVersion: 'sql-baseline-v2', sourceAlgorithmVersion: 'sql-rollup-v2' };
    const scale = memory ? 1073741824 : cpu ? 5 : 1;
    const offset = targetId === targets[0].instanceId ? 0 : 6;
    const items = mode === 'empty' ? [] : Array.from({ length: 168 }, (_, hourOfWeek) => {
      const unavailable = mode === 'unavailable' || mode === 'gap' && hourOfWeek % 2 === 0;
      const partial = mode === 'partial', days = unavailable ? 0 : partial ? 7 : 28, samples = unavailable ? 0 : partial ? 1 : 4;
      return { ...identity, sourceCutoffUtc: toUtc, statistics: {
        metricKey, hourOfWeek, completeDays: days, sampleCount: samples, mean: null, stddev: null,
        median: unavailable ? null : (10 + offset) * scale, mad: unavailable ? null : scale,
        p10: unavailable ? null : (8 + offset) * scale, p90: unavailable ? null : (14 + offset) * scale,
        lowerBound: null, upperBound: null, coverage: days / 28, confidence: days / 28 * samples / 8,
        windowStartUtc: fromUtc, windowEndUtc: toUtc, generation: 1,
        visibilityState: unavailable ? 'unavailable' : partial ? 'partial' : 'complete', dimensions: {},
        dimensionsSha256: '44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a',
      } };
    });
    send(res, { ...identity, targetId: mode === 'foreign' ? targets.find(target => target.instanceId !== targetId).instanceId : targetId,
      targetRevision: Number(url.searchParams.get('targetRevision')), metricKey, unit: memory ? 'bytes' : cpu ? 'percent' : metricKey === 'engine.batch_requests_per_second' ? 'batches_per_second' : 'count',
      fromUtc, toUtc, snapshotUtc: new Date().toISOString(), generation: 1, items,
      state: mode === 'empty' ? 'no_data' : mode === 'unavailable' ? 'unavailable' : ['partial', 'gap'].includes(mode) ? 'partial' : 'complete' });
    return true;
  }
  if (route[2] === 'analytics/baselines') {
    const fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc'), metricKey = url.searchParams.get('metricKey');
    const gib = 1073741824;
    send(res, { targetId, metricKey, fromUtc, toUtc, state: mode === 'empty' ? 'no_data' : 'complete', items: mode === 'empty' ? [] : Array.from({ length: 168 }, (_, hourOfWeek) => ({
      metricKey, hourOfWeek, completeDays: 28, sampleCount: 4, mean: null, stddev: null, median: 10 * gib, mad: gib, p10: 8 * gib, p90: 14 * gib,
      lowerBound: null, upperBound: null, coverage: 1, confidence: .5, windowStartUtc: fromUtc,
      windowEndUtc: mode === 'future' ? new Date(Date.parse(toUtc) + 7 * 86400000).toISOString() : toUtc,
      generation: 3, visibilityState: 'complete', dimensions: {}, dimensionsSha256: 'a'.repeat(64),
    })).filter(row => mode !== 'gap' || row.hourOfWeek % 2 === 0) }); return true;
  }
  if (role !== 'admin') { send(res, {}, 403); return true; }
  const secondPage = url.searchParams.get('cursor') === 'cGFnZTI=';
  const rule = { ruleId: secondPage ? 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' : 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    name: secondPage ? 'SYNTHETIC OS memory floor' : 'SYNTHETIC SQL CPU threshold', kind: 'metric_threshold',
    metricId: secondPage ? 'engine.os_available_memory_bytes' : 'engine.sql_scheduler_cpu_percent', comparison: secondPage ? 'less_than' : 'greater_than',
    threshold: secondPage ? 6 * 1073741824 : 80, hysteresis: 0, confirmationCount: 3, confirmationSeconds: 60, evaluationSeconds: 30,
    clearConfirmationCount: 2, enabled: true, revision: 7, updatedAtUtc: listingStartedUtc };
  send(res, { targetId, listingStartedUtc, items: [rule], hasMore: !secondPage, nextCursor: secondPage ? null : 'cGFnZTI=' }); return true;
}
