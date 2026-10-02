// Isolated synthetic dashboard summaries; no production imports or connected data.
const cookie = (request, name) => request.headers.cookie?.split(';').map(value => value.trim()).find(value => value.startsWith(name + '='))?.slice(name.length + 1);
const identity = { catalogVersion: 2, catalogChecksum: '702630ac539a313de148f77c4d4ec3bc0952a283441b74dd3c3ad72e16fff338', algorithmVersion: 'sql-baseline-v2', sourceAlgorithmVersion: 'sql-rollup-v2' };

export function handleDashboardSummaryFixture(url, response, request, targets, send) {
  if (cookie(request, 'baselineKpiFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/analytics\/sql-baselines$/.exec(url.pathname);
  if (!route) return false;
  const target = targets.find(item => item.instanceId === route[1]);
  if (!target) { send(response, {}, 404); return true; }
  const mode = cookie(request, 'dashboardBaselineState') ?? 'normal';
  if (mode === 'error') { send(response, {}, 503); return true; }
  const metricKey = url.searchParams.get('metricKey'), fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  const memory = metricKey.endsWith('_bytes'), cpu = metricKey.endsWith('_percent'), scale = memory ? 1073741824 : 1;
  const offset = targets.indexOf(target) * 5;
  const items = mode === 'empty' ? [] : Array.from({ length: 168 }, (_, hourOfWeek) => {
    const missing = mode === 'missing-hour' && hourOfWeek % 24 === 11;
    const partial = mode === 'partial', days = missing ? 0 : partial ? 7 : 28, samples = missing ? 0 : partial ? 1 : 4;
    const median = cpu ? hourOfWeek % 24 * 5 + offset : 10 + offset;
    return { ...identity, sourceCutoffUtc: toUtc, statistics: { metricKey, dimensionsSha256: '44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a', dimensions: {}, hourOfWeek, completeDays: days, sampleCount: samples, mean: null, stddev: null, median: missing ? null : median * scale, mad: missing ? null : scale, p10: missing ? null : Math.max(0, median - 5) * scale, p90: missing ? null : (median + 5) * scale, coverage: days / 28, confidence: days / 28 * samples / 8, lowerBound: null, upperBound: null, windowStartUtc: fromUtc, windowEndUtc: toUtc, generation: 1, visibilityState: missing ? 'unavailable' : partial ? 'partial' : 'complete' } };
  });
  send(response, { ...identity, targetId: mode === 'foreign' ? targets.find(item => item.instanceId !== target.instanceId).instanceId : target.instanceId, targetRevision: Number(url.searchParams.get('targetRevision')), metricKey, unit: memory ? 'bytes' : cpu ? 'percent' : metricKey === 'engine.batch_requests_per_second' ? 'batches_per_second' : 'count', fromUtc, toUtc, snapshotUtc: new Date().toISOString(), generation: 1, items, state: mode === 'empty' ? 'no_data' : ['partial', 'missing-hour'].includes(mode) ? 'partial' : 'complete' });
  return true;
}

export function dashboardSummaryCpuPoints(request, from, to, targetIndex) {
  if (cookie(request, 'baselineKpiFixture') !== '1') return undefined;
  const mode = cookie(request, 'dashboardObservationState');
  const points = [0.125, 0.875].map((fraction, index) => ({ timeUtc: new Date(from + (to - from) * fraction).toISOString(), value: index ? 58 + new Date(to).getUTCHours() + targetIndex * 10 : 20, samples: 10 }));
  if (mode === 'missing') points[1] = { ...points[1], value: null, samples: 0 };
  return points;
}
