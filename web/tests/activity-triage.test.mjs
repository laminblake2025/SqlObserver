import test from 'node:test';
import assert from 'node:assert/strict';
import { nativeLive } from '../component-tests/waitsAccessFixture.mjs';
import { loadActivityCapture } from '../src/features/activity/activityCaptureModel.ts';
import { buildSessionTriage, defaultTriageThresholds as defaults, intervalRate, rowRates, validTriageThresholds, readTriageThresholds, saveTriageThresholds } from '../src/features/activity/sessionTriageModel.ts';
import { buildBlockingTree } from '../src/features/activity/blockingModel.ts';
import { ActivityRequestError } from '../src/features/activity/activityParser.mjs';
const base = nativeLive(new URL('http://localhost/activity/live'));
const identity = n => n.toString(16).padStart(64, '0');
const row = (id, fields = {}) => ({ ...base.rows[0], identity: identity(id), sessionId: id, blocker: null, waitType: null, elapsedMs: '1', ...fields });
const page = (rows, fields = {}) => ({ ...base, rows, ...fields });
const delta = (fields = {}) => ({ seconds: 10, cpuMs: '0', reads: '0', writes: '0', logicalReads: '0', ...fields });
const rank = rows => buildSessionTriage(rows, defaults);
const signal = group => group.signals.map(value => value.label).join('; ');

test('all metadata pages pin one observation and rank an idle root beyond page one', async () => {
  const calls = [];
  const result = await loadActivityCapture(async parameters => {
    calls.push(Object.fromEntries(parameters));
    return parameters.has('cursor') ? page([row(72, { requestId: null, elapsedMs: '99999999', cpuMs: '99999999' })]) : page([row(81, { blocker: 72 }), row(84, { blocker: 81 })], { nextCursor: 'cGFnZTI=' });
  }, new AbortController().signal);
  assert.equal(result.pagesRead, 2); assert.equal(result.partial, false);
  assert.equal(calls[0].includeIdle, 'true'); assert.equal(calls[0].includeSystem, 'true'); assert.equal(calls[0].blockedOnly, 'false');
  assert.equal(calls[1].snapshot, base.snapshotId); assert.equal(calls[1].cursor, 'cGFnZTI=');
  const groups = rank(result.rows);
  assert.deepEqual(groups.map(group => group.sessionId), [72, 81, 84]);
  assert.equal(groups[0].impact, 2); assert.match(signal(groups[0]), /Blocking 2 sessions/);
  assert.equal(groups.filter(group => group.blocked).length, 2); assert.equal(groups.filter(group => group.tier <= 2).length, 3);
  assert.doesNotMatch(signal(groups[0]), /Running|CPU/);
});
test('request summaries select the highest signal, retain every exact request and separate reused lifetimes', () => {
  const groups = rank([row(90), row(90, { identity: identity(900), requestId: 2, elapsedMs: '48000' }), row(90, { identity: identity(901), sessionLogin: '2026-09-30T13:01:00Z', requestId: null })]);
  assert.equal(groups.length, 2); assert.equal(groups[0].rows.length, 2); assert.equal(groups[0].representative.requestId, 2);
  assert.equal(new Set(groups.flatMap(group => group.rows.map(row => row.identity))).size, 3);
  assert.ok(groups.every(group => signal(group).includes('Session lifetime unresolved')));
  const linked = [row(90), row(90, { identity: identity(901), sessionLogin: '2026-09-30T13:01:00Z' }), row(91, { blocker: 90 })];
  assert.ok(buildBlockingTree(linked).every(group => group.rootLoaded === false));
  assert.ok(rank(linked).filter(group => group.sessionId === 90).every(group => group.impact === 0));
});
test('missing roots, cycles, special SQL blockers and conflicting requests remain unresolved', () => {
  assert.match(signal(rank([row(81, { blocker: 72 })])[0]), /Root unavailable/);
  assert.ok(rank([row(81, { blocker: 84 }), row(84, { blocker: 81 })]).every(group => signal(group).includes('Blocking cycle')));
  assert.match(signal(rank([row(81, { blocker: -2 })])[0]), /Special SQL blocker/);
  const conflicting = [row(81, { blocker: 72 }), row(81, { identity: identity(810), requestId: 1, blocker: 73 })];
  assert.match(signal(rank(conflicting)[0]), /Conflicting blockers/);
  assert.equal(buildBlockingTree(conflicting)[0].rootLoaded, false);
});
test('threshold boundaries use valid interval deltas, elapsed only active users, and never cumulative CPU', () => {
  for (const [fields, expected] of [
    [{ elapsedMs: '29999' }, false], [{ elapsedMs: '30000' }, true],
    [{ delta: delta({ cpuMs: '4999' }) }, false], [{ delta: delta({ cpuMs: '5000' }) }, true],
    [{ delta: delta({ reads: '4999', writes: '5000' }) }, false], [{ delta: delta({ reads: '5000', writes: '5000' }) }, true],
    [{ delta: delta({ logicalReads: '99999' }) }, false], [{ delta: delta({ logicalReads: '100000' }) }, true],
    [{ cpuMs: '9999999999999999999999999', elapsedMs: '9999999999', requestId: null }, false],
    [{ isUser: false, elapsedMs: '9999999999' }, false], [{ isUser: false, delta: delta({ cpuMs: '6000' }) }, true],
  ]) assert.equal(rank([row(80, fields)])[0].tier === 2, expected, JSON.stringify(fields));
  assert.equal(rowRates(row(80)).cpu, undefined);
  assert.equal(intervalRate('100', 0), undefined); assert.equal(intervalRate('100', NaN), undefined);
  assert.equal(intervalRate('900719925474099312345', 0.25), 3602879701896397249380000n);
  assert.equal(intervalRate('5', 1e-7), 50000000000n);
});
test('default ordering is stable across arrival order and uses impact and threshold exceedance', () => {
  const rows = [row(7, { isUser: false }), row(8, { requestId: null }), row(9), row(10, { elapsedMs: '48000' }), row(11, { delta: delta({ cpuMs: '10000' }) }), row(12, { blocker: 13 }), row(13, { requestId: null })];
  assert.deepEqual(rank(rows).map(group => group.sessionId), [13, 12, 11, 10, 9, 8, 7]);
  assert.deepEqual(rank(rows).map(group => group.key), rank([...rows].reverse()).map(group => group.key));
});
test('threshold preferences validate bounds, persist per server and tolerate unavailable browser storage', () => {
  const previous = globalThis.localStorage, values = new Map();
  globalThis.localStorage = { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value) };
  try {
    const custom = { ...defaults, elapsedSeconds: 45 }; saveTriageThresholds('SERVER-A', custom);
    assert.deepEqual(readTriageThresholds('server-a'), custom); assert.deepEqual(readTriageThresholds('server-b'), defaults);
    for (const invalid of [0, -1, 1.5, Infinity, 1e12 + 1, '30']) assert.equal(validTriageThresholds({ ...defaults, elapsedSeconds: invalid }), false);
    assert.throws(() => saveTriageThresholds('a', { ...defaults, elapsedSeconds: 0 }));
    values.set('sqlobserver.activityTriage.v1.server-a', '{bad'); assert.deepEqual(readTriageThresholds('server-a'), defaults);
    globalThis.localStorage = { getItem: () => { throw Error('denied'); }, setItem: () => { throw Error('denied'); } };
    assert.deepEqual(readTriageThresholds('a'), defaults); saveTriageThresholds('a', defaults);
  } finally { globalThis.localStorage = previous; }
});
test('continuation failures disclose validated partial evidence, while denials and invalid pages fail closed', async () => {
  for (const failure of [new ActivityRequestError('forbidden', 401), new ActivityRequestError('forbidden', 403), Error('network')]) {
    let calls = 0;
    const read = () => loadActivityCapture(async () => ++calls === 1 ? page([row(80)], { nextCursor: 'cGFnZTI=' }) : Promise.reject(failure), new AbortController().signal);
    if (failure.status) await assert.rejects(read(), /forbidden/);
    else { const capture = await read(); assert.equal(capture.partial, true); assert.equal(capture.rows.length, 1); assert.match(capture.coverageMessage, /only the validated rows/); }
  }
  for (const bad of [page([row(80)]), page([row(81)], { observedUtc: '2026-09-30T13:30:00.000001Z' }), page([row(81)], { snapshotId: '66666666-6666-4666-8666-666666666666' }), page([row(81)], { nextCursor: 'cGFnZTI=' }), page([row(81)], { truncated: true })]) {
    let calls = 0;
    await assert.rejects(loadActivityCapture(async () => ++calls === 1 ? page([row(80)], { nextCursor: 'cGFnZTI=' }) : bad, new AbortController().signal));
  }
  await assert.rejects(loadActivityCapture(async () => page(Array.from({ length: 51 }, (_, index) => row(index + 1))), new AbortController().signal), /metadata/);
});
test('512-row and 11-page bounds remain qualified, and truncated captures are never complete fleet evidence', async () => {
  let calls = 0;
  const capture = await loadActivityCapture(async () => { const start = calls++ * 50; return page(Array.from({ length: Math.min(50, 512 - start) }, (_, index) => row(start + index + 1)), { truncated: true, nextCursor: Buffer.from(String(calls)).toString('base64') }); }, new AbortController().signal);
  assert.equal(calls, 11); assert.equal(capture.rows.length, 512); assert.equal(capture.partial, true); assert.equal(capture.truncated, true);
  calls = 0;
  const bounded = await loadActivityCapture(async () => page([row(++calls)], { nextCursor: Buffer.from(String(calls)).toString('base64') }), new AbortController().signal);
  assert.equal(calls, 11); assert.equal(bounded.partial, true);
});
test('retention, stale state and cancellation preserve capture identity', async () => {
  const stale = await loadActivityCapture(async () => page([row(80)], { state: 'stale' }), new AbortController().signal);
  assert.equal(stale.state, 'stale');
  const unavailable = page([], { snapshotId: null, observedUtc: null, state: 'unavailable' });
  assert.equal((await loadActivityCapture(async () => unavailable, new AbortController().signal)).state, 'unavailable');
  await assert.rejects(loadActivityCapture(async () => ({ ...unavailable, state: 'available' }), new AbortController().signal), /inconsistent/);
  await assert.rejects(loadActivityCapture(async () => page([row(80)], { repositoryTimeUtc: '2026-10-01T13:30:00Z' }), new AbortController().signal), /retention/);
  const controller = new AbortController(); controller.abort(); let calls = 0;
  await assert.rejects(loadActivityCapture(async () => { calls++; return base; }, controller.signal), { name: 'AbortError' }); assert.equal(calls, 0);
});
