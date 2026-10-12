import test from 'node:test';
import assert from 'node:assert/strict';
import { queryStoreRankingTrend, cacheRankingTrend, queryStoreRankingTrendRequest, cacheRankingTrendRequest, rankingTrendRequestKey, rankingTrendIdentityCounts } from '../src/features/queries/rankingTrendModel.ts';
import { createRankingTrendQueue } from '../src/features/queries/rankingTrendQueue.ts';

const targetId = '11111111-1111-4111-8111-000000000001';
const fromUtc = '2026-09-26T08:00:00.123456Z', toUtc = '2026-09-26T12:00:00.123456Z';
const window = { fromUtc, toUtc };
const request = queryStoreRankingTrendRequest(targetId, 5, 'b'.repeat(64), 'c'.repeat(64), window);
const cacheRequest = cacheRankingTrendRequest({ targetId, databaseId: 5, databaseIncarnation: 'a'.repeat(64), queryFingerprint: 'b'.repeat(64), planFingerprint: 'c'.repeat(64) }, window);
const point = (bucketStartUtc = '2026-09-26T08:00:00Z', extra = {}) => ({ bucketStartUtc, sourceRows: 1,
  runtimeComparableRows: 1, runtimeBaselineRows: 0, runtimeResetRows: 0, runtimeAmbiguousRows: 0,
  cpuMilliseconds: '120', durationMilliseconds: '240', executions: '3', logicalReads: '4000', writes: '12', rows: '20',
  waitComparableRows: 1, waitBaselineRows: 0, waitResetRows: 0, waitUnavailableRows: 0, waitStaleRows: 0,
  waitCategories: { '3': '90', '6': '10' }, ...extra });
const trend = points => ({ ...window, points });
const cacheRow = (extra = {}) => ({ collectionRunId: '22222222-2222-4222-8222-222222222222', cacheEntryFingerprint: 'd'.repeat(64), sourceEpoch: 'e'.repeat(64),
  observedAtUtc: '2026-09-26T09:00:00Z', committedAtUtc: '2026-09-26T09:00:00Z', captureComplete: true, transition: 'comparable',
  intervalFromUtc: '2026-09-26T08:30:00Z', intervalToUtc: '2026-09-26T09:00:00Z',
  cpuMilliseconds: '1800', durationMilliseconds: '3600', executions: '18', logicalReads: '180', writes: '36', rows: '72', ...extra });
const history = items => ({ ...cacheRequest, snapshotUtc: toUtc, revision: 1, totalRows: items.length, nextCursor: null, items });

test('all six runtime metrics use real additive hourly group counters rather than the ranking value', () => {
  const expected = { cpu: '120', duration: '240', executions: '3', logical_reads: '4000', writes: '12', rows: '20' };
  for (const [metric, exact] of Object.entries(expected)) {
    const result = queryStoreRankingTrend(trend([point()]), request, metric);
    assert.equal(result.values[0].exact, exact);
    assert.equal(result.segments.length, 1);
    assert.equal(result.segments[0].points.length, 1);
    assert.equal(result.segments[0].points[0].timeUtc, fromUtc);
    assert.equal(result.segments[0].points[0].value, Number(exact));
    assert.match(result.source, /comparable hourly group deltas/);
  }
});

test('wait previews select the requested category and preserve a comparable observed zero', () => {
  for (const [category, value] of [[null, 100], [3, 90], [6, 10], [7, 0]]) {
    const result = queryStoreRankingTrend(trend([point()]), request, 'waits', category);
    assert.equal(result.segments[0].points[0].value, value);
    assert.equal(result.values[0].exact, String(value));
  }
  const noWait = point(undefined, { waitComparableRows: 0, waitBaselineRows: 1, waitCategories: {} });
  assert.equal(queryStoreRankingTrend(trend([noWait]), request, 'waits').segments.length, 0);
});

test('missing/reset/ambiguous buckets and partial sums cannot bridge clean Query Store segments', () => {
  const points = [point(), point('2026-09-26T10:00:00Z', { runtimeComparableRows: 0, runtimeResetRows: 1, cpuMilliseconds: null }), point('2026-09-26T11:00:00Z')];
  const result = queryStoreRankingTrend(trend(points), request, 'cpu');
  assert.deepEqual(result.segments.map(segment => segment.points.length), [1, 1]);
  assert.match(result.coverage, /2 missing hourly buckets/); // 09h and final partial 12h.
  assert.equal(result.values[1].exact, null);
  const partial = point('2026-09-26T09:00:00Z', { sourceRows: 2, runtimeBaselineRows: 1, waitBaselineRows: 1 });
  const mixed = queryStoreRankingTrend(trend([point(), partial, point('2026-09-26T10:00:00Z')]), request, 'cpu');
  assert.deepEqual(mixed.segments.map(segment => segment.points.length), [1, 1, 1]);
  assert.match(mixed.values[1].note, /1 baseline/);
});

test('hourly membership uses exact half-open microseconds including pre-1970 floor and one-microsecond final buckets', () => {
  const shortWindow = { fromUtc: '2026-09-26T11:59:59.999999Z', toUtc: '2026-09-26T12:00:00Z' };
  const shortRequest = { ...request, ...shortWindow };
  const beforeEnd = queryStoreRankingTrend({ ...shortWindow, points: [point('2026-09-26T11:00:00Z')] }, shortRequest, 'cpu');
  assert.equal(beforeEnd.segments[0].points[0].timeUtc, shortWindow.fromUtc);
  assert.throws(() => queryStoreRankingTrend({ ...shortWindow, points: [point('2026-09-26T12:00:00Z')] }, shortRequest, 'cpu'), /bucket/);
  const boundaryWindow = { fromUtc: '2026-09-26T12:00:00Z', toUtc: '2026-09-26T12:00:00.000001Z' };
  assert.equal(queryStoreRankingTrend({ ...boundaryWindow, points: [point('2026-09-26T12:00:00Z')] }, { ...request, ...boundaryWindow }, 'cpu').segments.length, 1);
  const oldWindow = { fromUtc: '1969-12-31T23:30:00.000001Z', toUtc: '1970-01-01T00:00:00Z' };
  assert.equal(queryStoreRankingTrend({ ...oldWindow, points: [point('1969-12-31T23:00:00Z')] }, { ...request, ...oldWindow }, 'cpu').segments[0].points[0].timeUtc, oldWindow.fromUtc);
});

test('unsafe large counters remain exact text and do not substitute zero or an older sample', () => {
  const result = queryStoreRankingTrend(trend([point(undefined, { cpuMilliseconds: '9007199254740993' })]), request, 'cpu');
  assert.equal(result.precisionOmissions, 1);
  assert.equal(result.segments.length, 0);
  assert.equal(result.values[0].exact, '9007199254740993');
  assert.throws(() => queryStoreRankingTrend(trend([point(), point()]), request, 'cpu'), /bucket/);
  assert.throws(() => queryStoreRankingTrend({ ...trend([]), toUtc: '2026-09-26T12:00:00.123457Z' }, request, 'cpu'), /window changed/);
});

test('request identity binds target, plan, database incarnation and precise window; legacy incomplete identity is unavailable', () => {
  assert.equal(queryStoreRankingTrendRequest(targetId, 5, 'b'.repeat(64), undefined, window), undefined);
  assert.equal(cacheRankingTrendRequest({ ...cacheRequest, databaseIncarnation: undefined }, window), undefined);
  assert.equal(rankingTrendRequestKey(request), rankingTrendRequestKey({ ...request, fromUtc: fromUtc.replace('Z', '0Z') }));
  assert.notEqual(rankingTrendRequestKey(cacheRequest), rankingTrendRequestKey({ ...cacheRequest, databaseIncarnation: 'f'.repeat(64) }));
  assert.throws(() => rankingTrendRequestKey({ ...request, toUtc: '2026-10-03T08:00:00.123457Z' }), /window/);
  assert.throws(() => cacheRankingTrend(history([]), { ...cacheRequest, planFingerprint: 'f'.repeat(64) }, 'cpu'), /identity/);
  const counts = rankingTrendIdentityCounts([request, { ...request }, undefined]);
  assert.equal(counts.get(rankingTrendRequestKey(request)), 2);
});

test('cache rate previews keep intervals and epochs separate and qualify all six selected metrics', () => {
  const first = cacheRow();
  const second = cacheRow({ sourceEpoch: 'f'.repeat(64), intervalFromUtc: '2026-09-26T10:00:00Z', intervalToUtc: '2026-09-26T10:30:00Z', observedAtUtc: '2026-09-26T10:30:00Z' });
  const reset = cacheRow({ transition: 'reset', intervalFromUtc: null, intervalToUtc: null, cpuMilliseconds: null, observedAtUtc: '2026-09-26T09:30:00Z' });
  for (const [metric, rate] of Object.entries({ cpu: 1, duration: 2, executions: .01, logical_reads: .1, writes: .02, rows: .04 })) {
    const result = cacheRankingTrend(history([second, reset, first]), cacheRequest, metric);
    assert.equal(result.segments.length, 2);
    assert.deepEqual(result.segments.map(segment => segment.points.length), [2, 2]);
    assert.equal(result.segments[0].points[0].value, rate);
    assert.match(result.coverage, /1 unavailable comparison/);
    assert.match(result.values[0].note, /epoch ffffffff/);
  }
});

test('cache previews retain raw counters, pagination and precision omissions; UTC offsets use the same exact precision', () => {
  const input = history([cacheRow({ cpuMilliseconds: '9223372036854775.807', intervalFromUtc: '2026-09-26T08:30:00.0000000+00:00', intervalToUtc: '2026-09-26T08:30:00.0000010+00:00', observedAtUtc: '2026-09-26T08:30:00.0000010+00:00' })]);
  input.totalRows = 150; input.nextCursor = 'cursor.signature';
  const result = cacheRankingTrend(input, cacheRequest, 'cpu');
  assert.equal(result.precisionOmissions, 1);
  assert.equal(result.segments.length, 0);
  assert.equal(result.values[0].exact, '9223372036854775.807');
  assert.match(result.coverage, /Newest 1 of 150 rows; more history/);
  const atEnd = history([cacheRow({ intervalToUtc: toUtc, observedAtUtc: toUtc })]);
  assert.equal(cacheRankingTrend(atEnd, cacheRequest, 'cpu').segments.length, 0);
});

const flush = () => new Promise(resolve => setImmediate(resolve));
test('visible-row queue bounds concurrency, shares identity, progresses beyond 25 rows and replaces safely on cancellation', async () => {
  const calls = [], pending = []; let notifications = 0;
  const queue = createRankingTrendQueue((job, signal) => new Promise(resolve => { calls.push({ job, signal }); pending.push(resolve); }), () => notifications++);
  const jobs = Array.from({ length: 35 }, (_, index) => ({ ...request, queryFingerprint: index.toString(16).padStart(64, '0') }));
  for (const job of jobs) assert.equal(queue.schedule(job), true);
  queue.schedule(jobs[0]);
  await flush(); assert.equal(calls.length, 2);
  for (let index = 0; index < 35; index++) {
    pending[index]('loaded'); await flush();
    assert.ok(calls.length <= index + 3);
  }
  assert.equal(calls.length, 35);
  assert.equal(queue.read(jobs[34]).status, 'ready');
  const cancelled = createRankingTrendQueue((job, signal) => new Promise(resolve => { calls.push({ job, signal }); pending.push(resolve); }), () => notifications++);
  cancelled.schedule(jobs[0]); cancelled.schedule(jobs[1]); cancelled.schedule(jobs[2]);
  await flush(); const before = notifications; cancelled.cancel();
  assert.equal(calls.at(-1).signal.aborted, true);
  pending.at(-1)('late'); pending.at(-2)('late'); await flush();
  assert.equal(notifications, before);
  assert.equal(cancelled.schedule(jobs[3]), false);
});

test('queue request cap rejects excess unique rows but allows a deliberate retry of a failed existing identity', async () => {
  let tries = 0;
  const queue = createRankingTrendQueue(async () => { if (tries++ === 0) throw new Error('source unavailable'); return 'retry result'; }, () => {}, 1, 1);
  queue.schedule(request); await flush();
  assert.equal(queue.read(request).status, 'error');
  assert.equal(queue.schedule({ ...request, planFingerprint: 'd'.repeat(64) }), false);
  assert.equal(queue.schedule(request, true), true); await flush();
  assert.equal(queue.read(request).value, 'retry result');
  const pageQueue = createRankingTrendQueue(async (_job, signal) => new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(new Error('cancelled')), { once: true })), () => {});
  for (let index = 0; index < 225; index++) assert.equal(pageQueue.schedule({ ...request, queryFingerprint: index.toString(16).padStart(64, '0') }), true);
  assert.equal(pageQueue.schedule({ ...request, queryFingerprint: (225).toString(16).padStart(64, '0') }), false);
  pageQueue.cancel(); await flush();
});
