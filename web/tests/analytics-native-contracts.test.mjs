import assert from 'node:assert/strict';
import test from 'node:test';
import { clearApiCache } from '../src/apiClient.ts';
import { getAnalyticsSurface } from '../src/features/analytics/analyticsApi.ts';
import { analyticsUtcTicks, decodeAnalyticsSurfaceCursor, parseAnalyticsSurfacePage, parseIncidentsSurfacePage, sameAnalyticsUtc } from '../src/features/analytics/analyticsParser.ts';

const target = '11111111-1111-4111-8111-111111111111';
const id = '22222222-2222-4222-8222-222222222222';
const laterId = '33333333-3333-4333-8333-333333333333';
const from = '2026-09-26T08:00:00.123456Z';
const to = '2026-09-26T12:00:00.654321Z';
const at = '2026-09-26T09:00:00.0000001Z';
const snapshot = '2026-09-26T13:00:00.0000000+00:00';
const revision = 4;
const row = surface => {
  const base = { targetRevision: revision };
  if (surface === 'host/status') return { ...base, hostId: id, bindingRevision: 1, hostName: 'SYNTHETIC host', bindingState: 'active', capabilityState: 'available', observedAtUtc: at, generation: 1 };
  if (surface === 'host/metrics') return { ...base, observedAtUtc: at, runId: id, metricKey: 'host.cpu.percent', value: 42, dimensions: { numa_node: '0' } };
  if (surface.startsWith('replication/')) return { ...base, observedAtUtc: at, runId: id, role: 'distributor', synchronizationState: 'healthy', sendQueueBytes: null, redoQueueBytes: null, pendingCommands: 20, latencySeconds: 0.5, visibilityScope: 1, coverage: 'complete', visibilityGap: null, stateAvailable: true };
  if (surface === 'diagnostics/search') return { ...base, occurredAtUtc: at, eventId: id, eventKind: 'synthetic', severity: 2, safeMetadata: '{"reason":"SYNTHETIC <script>literal</script>","count":2}', collectedAtUtc: at };
  if (surface === 'jobs' || surface === 'backfill') return { ...base, jobId: id, jobKind: surface === 'backfill' ? 'backfill' : 'incident', status: 'running', requestedAtUtc: at, startedAtUtc: at, completedAtUtc: null, attempt: 1 };
  return { ...base, occurredAtUtc: at, packetId: id, evidenceKind: 'synthetic', sourceRunId: null, sourceDigest: 'a'.repeat(64), confidence: null, visibilityState: 'partial' };
};
const page = (surface, items = [row(surface)]) => ({ targetId: target, surface, fromUtc: from, toUtc: to, items, state: 'complete', nextCursor: null, cutoffUtc: snapshot, snapshotUtc: snapshot, generation: 8, targetRevision: revision });
const cursorFields = response => {
  const item = response.items.at(-1);
  const surface = response.surface;
  return { kind: 'm10.surface.v1', surface, targetId: target, targetRevision: revision, generation: 8, snapshotUtc: snapshot, sourceCutoffUtc: snapshot, fromUtc: from.replace('Z', '+00:00'), toUtc: to.replace('Z', '+00:00'), filters: {},
    tieAt: item.requestedAtUtc ?? item.observedAtUtc ?? item.occurredAtUtc,
    tieId: item.jobId ?? item.hostId ?? item.runId ?? item.eventId ?? item.packetId,
    tieMetric: surface === 'host/metrics' ? item.metricKey : null,
    tieDimensions: surface === 'host/metrics' ? item.dimensions : null,
    tieFingerprint: surface.startsWith('replication/') ? 'b'.repeat(64) : null };
};
const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
const continued = response => ({ ...response, nextCursor: encode(cursorFields(response)) });

test('all eight paged surfaces accept their native cursor and tail identity', () => {
  for (const surface of ['host/status', 'host/metrics', 'replication/status', 'replication/evidence', 'diagnostics/search', 'jobs', 'backfill', 'evidence-packets']) {
    const response = continued(page(surface));
    const parsed = parseAnalyticsSurfacePage(response, surface, target, revision);
    assert.equal(parsed.items.length, 1);
    assert.equal(decodeAnalyticsSurfaceCursor(parsed.nextCursor).tieId, id);
  }
});

test('native diagnostic JSON metadata becomes safe evidence without mutating its wire object', () => {
  const response = page('diagnostics/search');
  const parsed = parseAnalyticsSurfacePage(response);
  assert.equal(parsed.items[0].safeMetadata.reason, 'SYNTHETIC <script>literal</script>');
  assert.equal(typeof response.items[0].safeMetadata, 'string');
  for (const safeMetadata of ['not json', 'null', '[]', '"text"', {}, 5]) assert.throws(() => parseAnalyticsSurfacePage(page('diagnostics/search', [{ ...row('diagnostics/search'), safeMetadata }])));
});

test('unknown evidence confidence is preserved as null and numeric confidence remains bounded', () => {
  assert.equal(parseAnalyticsSurfacePage(page('evidence-packets')).items[0].confidence, null);
  for (const confidence of [0, 0.5, 1]) assert.equal(parseAnalyticsSurfacePage(page('evidence-packets', [{ ...row('evidence-packets'), confidence }])).items[0].confidence, confidence);
  for (const confidence of [-1, 1.01, '0.5', undefined]) assert.throws(() => parseAnalyticsSurfacePage(page('evidence-packets', [{ ...row('evidence-packets'), confidence }])));
});

test('native host fallback capability and incident derivation jobs remain readable', () => {
  assert.equal(parseAnalyticsSurfacePage(page('host/status', [{ ...row('host/status'), capabilityState: 'unavailable' }])).items[0].capabilityState, 'unavailable');
  assert.equal(parseAnalyticsSurfacePage(page('jobs')).items[0].jobKind, 'incident');
});

test('UTC binding preserves sub-millisecond fractions and rejects invalid calendar times', () => {
  assert.equal(analyticsUtcTicks('2026-09-26T09:00:00.0000002Z') - analyticsUtcTicks(at), 1n);
  assert.ok(sameAnalyticsUtc(from, from.replace('Z', '+00:00')));
  assert.equal(sameAnalyticsUtc(at, '2026-09-26T09:00:00.0000002Z'), false);
  for (const value of ['2026-02-30T00:00:00Z', '2026-09-26T24:00:00Z', '2026-09-26T09:00:00+01:00']) assert.throws(() => analyticsUtcTicks(value));
});

test('outgoing continuation binds every native fence and last row', () => {
  const response = page('diagnostics/search');
  for (const changes of [{ kind: 'invented' }, { surface: 'jobs' }, { targetId: id }, { targetRevision: 5 }, { generation: 9 }, { snapshotUtc: '2026-09-26T14:00:00Z' }, { sourceCutoffUtc: '2026-09-26T12:30:00Z' }, { fromUtc: '2026-09-26T08:00:00.1234561Z' }, { toUtc: '2026-09-26T12:00:00.6543211Z' }, { filters: { metricKey: 'host.cpu.percent' } }, { tieAt: '2026-09-26T09:00:00.0000002Z' }, { tieId: laterId }, { tieMetric: 'host.cpu.percent' }]) {
    assert.throws(() => parseAnalyticsSurfacePage({ ...response, nextCursor: encode({ ...cursorFields(response), ...changes }) }));
  }
  for (const nextCursor of ['YWJj', 'not-json', 'a'.repeat(1025)]) assert.throws(() => parseAnalyticsSurfacePage({ ...response, nextCursor }));
  assert.throws(() => parseAnalyticsSurfacePage({ ...continued(response), items: [] }));
});

test('native metric and exposed visibility-gap topology ties bind to the last row', () => {
  const metric = page('host/metrics');
  assert.throws(() => parseAnalyticsSurfacePage({ ...metric, nextCursor: encode({ ...cursorFields(metric), tieDimensions: { numa_node: '1' } }) }));
  const gap = page('replication/evidence', [{ ...row('replication/evidence'), coverage: 'visibility_gap', visibilityScope: 3, visibilityGap: { kind: 'visibility_gap', reason: 'distribution_database_unbound', identity: 'c'.repeat(64) } }]);
  assert.throws(() => parseAnalyticsSurfacePage(continued(gap)));
  assert.equal(parseAnalyticsSurfacePage({ ...gap, nextCursor: encode({ ...cursorFields(gap), tieFingerprint: 'c'.repeat(64) }) }).items.length, 1);
});

test('windowed rows obey exact half-open bounds and snapshot filtering', () => {
  const source = row('host/metrics');
  assert.throws(() => parseAnalyticsSurfacePage(page('host/metrics', [{ ...source, observedAtUtc: '2026-09-26T08:00:00.1234559Z' }])));
  assert.throws(() => parseAnalyticsSurfacePage(page('host/metrics', [{ ...source, observedAtUtc: to }])));
  const diagnostic = row('diagnostics/search');
  assert.throws(() => parseAnalyticsSurfacePage(page('diagnostics/search', [{ ...diagnostic, collectedAtUtc: '2026-09-26T13:00:00.0000001Z' }])));
});

test('jobs and backfill are native inventories while their cutoff still bounds request time', () => {
  for (const surface of ['jobs', 'backfill']) {
    assert.equal(parseAnalyticsSurfacePage(page(surface, [{ ...row(surface), requestedAtUtc: '2026-09-20T00:00:00Z' }])).items.length, 1);
    assert.throws(() => parseAnalyticsSurfacePage(page(surface, [{ ...row(surface), requestedAtUtc: '2026-09-26T13:00:00.0000001Z' }])));
  }
});

test('native row direction and exposed tie ordering reject gaps disguised as valid pages', () => {
  const early = row('evidence-packets'), late = { ...early, occurredAtUtc: '2026-09-26T09:00:00.0000002Z', packetId: laterId };
  assert.equal(parseAnalyticsSurfacePage(page('evidence-packets', [early, late])).items.length, 2);
  assert.throws(() => parseAnalyticsSurfacePage(page('evidence-packets', [late, early])));
  assert.throws(() => parseAnalyticsSurfacePage(page('evidence-packets', [early, early])));
  const host = row('host/status'), older = { ...host, observedAtUtc: '2026-09-26T09:00:00.0000000Z', hostId: laterId };
  assert.equal(parseAnalyticsSurfacePage(page('host/status', [host, older])).items.length, 2);
  assert.throws(() => parseAnalyticsSurfacePage(page('host/status', [older, host])));
});

test('legacy incidents stay bounded without fabricated receipt or continuation fields', () => {
  const response = { targetId: target, fromUtc: from, toUtc: to, state: 'complete', items: [{ threadId: id, openedAtUtc: at, closedAtUtc: null, currentGeneration: 2, packets: [], generations: [] }] };
  const parsed = parseIncidentsSurfacePage(response, target, revision);
  assert.equal(parsed.nextCursor, null);
  assert.equal(parsed.snapshotUtc, undefined);
  assert.equal(parsed.targetRevision, undefined);
  for (const extras of [{ nextCursor: null }, { targetRevision: revision }, { snapshotUtc: snapshot }, { surface: 'incidents' }]) assert.throws(() => parseIncidentsSurfacePage({ ...response, ...extras }));
  assert.throws(() => parseIncidentsSurfacePage({ ...response, items: [{ ...response.items[0], openedAtUtc: to }] }));
});

test('surface reader binds exact requested windows, target revisions and page size', async () => {
  const original = globalThis.fetch;
  let response = page('diagnostics/search');
  const requests = [];
  globalThis.fetch = async input => { requests.push(String(input)); return new Response(JSON.stringify(response), { headers: { 'content-type': 'application/json' } }); };
  try {
    clearApiCache();
    const options = { fromUtc: from, toUtc: to, expectedTargetRevision: revision, limit: 1 };
    assert.equal((await getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, options)).items.length, 1);
    const url = new URL(requests[0], 'https://synthetic.invalid');
    assert.equal(url.searchParams.get('fromUtc'), from);
    response = { ...page('diagnostics/search'), toUtc: '2026-09-26T12:00:00.6543211Z' }; clearApiCache();
    await assert.rejects(getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, options), /window/);
    response = { ...page('diagnostics/search'), targetRevision: 5 }; clearApiCache();
    await assert.rejects(getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, options), /revision/);
    response = continued(page('diagnostics/search')); clearApiCache();
    await assert.rejects(getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, { ...options, limit: 2 }), /size/);
    const continuation = response.nextCursor;
    response = { ...page('diagnostics/search'), generation: 9 }; clearApiCache();
    await assert.rejects(getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, { ...options, cursor: continuation }), /receipt/);
    const before = requests.length;
    await assert.rejects(getAnalyticsSurface(target, 'incidents', new AbortController().signal, { ...options, cursor: continuation }), /continuation/);
    await assert.rejects(getAnalyticsSurface(target, 'diagnostics/search', new AbortController().signal, { ...options, cursor: continuation, fromUtc: '2026-09-26T08:00:00.1234561Z' }), /request/);
    assert.equal(requests.length, before);
  } finally { globalThis.fetch = original; clearApiCache(); }
});
