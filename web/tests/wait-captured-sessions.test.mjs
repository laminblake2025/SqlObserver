import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { capturedSessionWaitCategory, capturedWaitSnapshotExpired, readSessionWaitCategory, sessionWaitCategoryHref, retainSessionWaitCategoryHref,
  waitSessionHistoryWindow, boundedWaitSessionCaptures, loadCapturedWaitSessions,
  readSessionWaitCapture, sessionWaitCaptureHref, retainSessionWaitCaptureHref, validateWaitCaptureWindow } from '../src/features/activity/waitCapturedSessionsModel.ts';

const target = '11111111-1111-4111-8111-111111111111', snapshot = '22222222-2222-4222-8222-222222222222';
const observed = '2026-10-02T11:30:00.123456Z';
const scope = { target, range: 'custom', from: '2026-10-02T11:00:00.000001Z', to: '2026-10-02T12:00:00.000001Z', compare: true, databaseId: 5 };
const row = id => ({ identity: id.toString(16).padStart(64, '0'), sessionId: id + 1, requestId: 0, databaseId: 5, waitType: 'LCK_M_X' });
const page = (rows, nextCursor = null) => ({ snapshotId: snapshot, observedUtc: observed, repositoryTimeUtc: '2026-10-02T12:00:00Z', state: 'available', truncated: false, rows, databases: [], nextCursor });

test('chosen capture URL is target-bound, paired, exact and preserves independent categories', () => {
  const base = `#/waits?${new URLSearchParams({ target, range: 'custom', from: scope.from, to: scope.to, databaseId: '5', compare: '1', waitCategory: '3', sessionWaitCategory: 'Lock' })}`;
  const href = sessionWaitCaptureHref(scope, { id: snapshot, observedUtc: observed }, base);
  assert.deepEqual(readSessionWaitCapture(href, target), { id: snapshot, observedUtc: observed });
  const letterGuid = 'abcdefab-abcd-4abc-8abc-abcdefabcdef';
  const uppercaseHref = sessionWaitCaptureHref(scope, { id: letterGuid.toUpperCase(), observedUtc: observed.replace('123456Z', '1234560Z') });
  assert.equal(readSessionWaitCapture(uppercaseHref).id, letterGuid);
  assert.equal(new URLSearchParams(uppercaseHref.split('?')[1]).get('sessionSnapshot'), letterGuid);
  assert.equal(new URLSearchParams(uppercaseHref.split('?')[1]).get('sessionObservedUtc'), observed);
  assert.equal(new URLSearchParams(href.split('?')[1]).get('waitCategory'), '3');
  assert.equal(new URLSearchParams(href.split('?')[1]).get('sessionWaitCategory'), 'Lock');
  assert.equal(readSessionWaitCapture(base, target), undefined);
  assert.equal(readSessionWaitCapture(`${href}&sessionSnapshot=${snapshot}`, target), null);
  assert.equal(readSessionWaitCapture(href.replace('sessionObservedUtc=', 'brokenObserved='), target), null);
  assert.equal(readSessionWaitCapture(`${href}&databaseId=5`, target), null);
  assert.equal(readSessionWaitCapture(href, snapshot), null);
  assert.equal(readSessionWaitCapture(href.replace('#/waits', '#/activity'), target), null);
  assert.equal(readSessionWaitCapture(sessionWaitCaptureHref(scope, undefined, href)), undefined);
  assert.throws(() => sessionWaitCaptureHref(scope, { id: snapshot, observedUtc: scope.to }), /inside/u);
  const zeroEquivalent = sessionWaitCaptureHref(scope, { id: snapshot, observedUtc: observed.replace('123456Z', '1234560Z') });
  assert.equal(readSessionWaitCapture(zeroEquivalent).observedUtc, observed);
});

test('capture retention keeps compatible context but drops contradictory target/database/window', () => {
  const href = sessionWaitCaptureHref(scope, { id: snapshot, observedUtc: observed });
  const changed = `#/waits?${new URLSearchParams({ target, range: 'custom', from: scope.from, to: scope.to, databaseId: '5', waitCategory: '6' })}`;
  assert.deepEqual(readSessionWaitCapture(retainSessionWaitCaptureHref(changed, href)), { id: snapshot, observedUtc: observed });
  for (const next of [changed.replace(target, snapshot), changed.replace('databaseId=5', 'databaseId=6'), changed.replace(encodeURIComponent(scope.to), encodeURIComponent(observed))])
    assert.equal(retainSessionWaitCaptureHref(next, href), next);
  const live = { target, range: '1h', compare: false };
  const liveHref = sessionWaitCaptureHref(live, { id: snapshot, observedUtc: observed });
  assert.equal(readSessionWaitCapture(retainSessionWaitCaptureHref(`#/waits?target=${target}&range=1h`, liveHref)).id, snapshot);
  assert.equal(retainSessionWaitCaptureHref(`#/waits?target=${target}&range=6h`, liveHref), `#/waits?target=${target}&range=6h`);
});

test('direct omitted-history capture must retain exact time, loaded window and native expiry', async () => {
  const now = Date.parse('2026-10-02T12:00:00Z'), capture = { id: snapshot, observedUtc: observed, truncated: false };
  validateWaitCaptureWindow(capture, scopeWindow(), now);
  assert.throws(() => validateWaitCaptureWindow({ ...capture, observedUtc: scope.to }, scopeWindow(), now), /outside/u);
  assert.throws(() => validateWaitCaptureWindow({ ...capture, observedUtc: '2026-10-01T12:00:00.000000Z' }, { fromUtc: '2026-10-01T11:00:00Z', toUtc: '2026-10-02T12:00:00Z' }, now), /expired/u);
  const result = await loadCapturedWaitSessions(async parameters => {
    assert.equal(parameters.get('snapshot'), snapshot); return page([row(1)]);
  }, new URLSearchParams(), new AbortController().signal, capture);
  assert.equal(result.page.snapshotId, snapshot);
  await assert.rejects(loadCapturedWaitSessions(async () => ({ ...page([row(1)]), observedUtc: observed.replace('123456', '123457') }),
    new URLSearchParams(), new AbortController().signal, capture), /observation time/u);
});

test('native session categories match the authoritative server-wait CASE including Idle precedence', () => {
  const examples = {
    Lock: ['LCK_M_X', 'LCK_M_S'], Log: ['WRITELOG', 'LOGBUFFER', 'LOG_RATE_GOVERNOR', 'LOGMGR_FLUSH'],
    'I/O': ['PAGEIOLATCH_SH', 'IO_COMPLETION', 'ASYNC_IO_COMPLETION', 'BACKUPIO', 'WRITE_COMPLETION'],
    'CPU/signal': ['SOS_SCHEDULER_YIELD'], Memory: ['RESOURCE_SEMAPHORE', 'RESOURCE_SEMAPHORE_QUERY_COMPILE', 'MEMORY_ALLOCATION_EXT'],
    Parallelism: ['CXPACKET', 'CXCONSUMER', 'EXCHANGE', 'CXSYNC_PORT'], Other: ['PAGELATCH_EX', 'ASYNC_NETWORK_IO'],
    Idle: ['SLEEP_TASK', 'LAZYWRITER_SLEEP', 'LOGMGR_QUEUE', 'XE_TIMER_EVENT', 'CHECKPOINT_QUEUE', 'SQLTRACE_BUFFER_FLUSH', 'ONDEMAND_TASK_QUEUE'],
  };
  for (const [category, types] of Object.entries(examples)) for (const type of types) assert.equal(capturedSessionWaitCategory(type), category);
  assert.equal(capturedSessionWaitCategory(null), undefined); assert.equal(capturedSessionWaitCategory(''), undefined);
  const canonical = readFileSync(new URL('../../database/migrations/0137_server_wait_category_run_summary.sql', import.meta.url), 'utf8').replaceAll('\\_', '_');
  for (const token of Object.values(examples).flat().filter(value => !['LCK_M_X', 'LCK_M_S', 'LOGMGR_FLUSH', 'PAGEIOLATCH_SH', 'RESOURCE_SEMAPHORE_QUERY_COMPILE', 'CXSYNC_PORT', 'PAGELATCH_EX', 'ASYNC_NETWORK_IO', 'SLEEP_TASK'].includes(value))) assert.ok(canonical.includes(token), token);
});

test('independent session category URL preserves exact shared context and Query Store category', () => {
  const base = `#/waits?target=${target}&range=custom&from=${encodeURIComponent(scope.from)}&to=${encodeURIComponent(scope.to)}&databaseId=5&compare=1&waitCategory=3`;
  const href = sessionWaitCategoryHref(scope, 'I/O', base);
  assert.equal(readSessionWaitCategory(href, target), 'I/O');
  const parameters = new URLSearchParams(href.split('?')[1]);
  for (const [key, value] of new URLSearchParams(base.split('?')[1])) assert.equal(parameters.get(key), value);
  assert.equal(readSessionWaitCategory(`${href}&sessionWaitCategory=Lock`, target), null);
  assert.equal(readSessionWaitCategory(href.replace('I%2FO', 'bogus'), target), null);
  assert.equal(readSessionWaitCategory(href.replace('#/waits', '#/queries'), target), null);
  assert.equal(readSessionWaitCategory(href, snapshot), null);
  const changed = `#/waits?target=${target}&range=1h&databaseId=6`;
  assert.equal(readSessionWaitCategory(retainSessionWaitCategoryHref(changed, href)), 'I/O');
  assert.equal(retainSessionWaitCategoryHref(changed.replace(target, snapshot), href), changed.replace(target, snapshot));
});

test('retention intersection and native capture selection preserve microseconds and half-open bounds', () => {
  const now = Date.parse('2026-10-02T12:00:00Z');
  assert.deepEqual(waitSessionHistoryWindow(scopeWindow(), now), { fromUtc: scope.from, toUtc: '2026-10-02T12:00:00.000001Z' });
  assert.equal(waitSessionHistoryWindow({ fromUtc: '2026-09-30T00:00:00Z', toUtc: '2026-10-01T12:00:00Z' }, now), undefined);
  assert.deepEqual(waitSessionHistoryWindow({ fromUtc: '2026-10-01T00:00:00Z', toUtc: '2026-10-02T12:00:00Z' }, now),
    { fromUtc: '2026-10-01T12:00:00.000001Z', toUtc: '2026-10-02T12:00:00.000000Z' });
  const captures = [{ id: snapshot, observedUtc: scope.from, truncated: false }, { id: target, observedUtc: observed, truncated: true }];
  assert.equal(boundedWaitSessionCaptures(captures, scopeWindow()).captures[0].id, target);
  assert.throws(() => boundedWaitSessionCaptures([{ ...captures[0], observedUtc: scope.to }], scopeWindow()));
  assert.throws(() => boundedWaitSessionCaptures([...captures, captures[0]], scopeWindow()));
  const many = Array.from({ length: 105 }, (_, i) => ({ id: `33333333-3333-4333-8333-${String(i + 1).padStart(12, '0')}`, observedUtc: `2026-10-02T11:30:00.${String(i).padStart(6, '0')}Z`, truncated: false }));
  const bounded = boundedWaitSessionCaptures(many, scopeWindow());
  assert.equal(bounded.captures.length, 100); assert.equal(bounded.omitted, 5); assert.equal(bounded.captures[0].id, many.at(-1).id);
});
function scopeWindow() { return { fromUtc: scope.from, toUtc: scope.to }; }

test('inspection expiry follows exact repository retention microseconds without rounding', () => {
  const now = Date.parse('2026-10-02T12:00:00Z');
  const current = { ...page([]), repositoryTimeUtc: '2026-10-02T12:00:00.123456Z', observedUtc: '2026-10-01T12:00:00.123457Z' };
  assert.equal(capturedWaitSnapshotExpired(current, now), false);
  assert.equal(capturedWaitSnapshotExpired({ ...current, observedUtc: '2026-10-01T12:00:00.1234560Z' }, now), true);
  assert.equal(capturedWaitSnapshotExpired({ ...current, observedUtc: '2026-10-01T12:00:00.123455Z' }, now), true);
});

test('metadata continuation pins capture/time, rejects duplicate/foreign/cyclic evidence and stays SQL-free', async () => {
  const calls = [];
  const result = await loadCapturedWaitSessions(async parameters => {
    calls.push(new URLSearchParams(parameters)); return parameters.has('cursor') ? page([row(2)]) : page([row(1)], 'cGFnZTI=');
  }, new URLSearchParams({ databaseId: '5' }), new AbortController().signal);
  assert.equal(result.rows.length, 2); assert.equal(result.pagesRead, 2); assert.equal(result.limited, false);
  assert.equal(calls[1].get('snapshot'), snapshot); assert.equal(calls[1].get('databaseId'), '5'); assert.equal(calls[1].get('cursor'), 'cGFnZTI=');
  await assert.rejects(loadCapturedWaitSessions(async p => p.has('cursor') ? page([row(1)]) : page([row(1)], 'cGFnZTI='), new URLSearchParams(), new AbortController().signal), /duplicate/u);
  await assert.rejects(loadCapturedWaitSessions(async p => p.has('cursor') ? { ...page([row(2)]), observedUtc: '2026-10-02T11:30:00.123457Z' } : page([row(1)], 'cGFnZTI='), new URLSearchParams(), new AbortController().signal), /observation time/u);
  await assert.rejects(loadCapturedWaitSessions(async () => page([{ ...row(1), databaseId: 6 }]), new URLSearchParams({ databaseId: '5' }), new AbortController().signal), /out-of-scope/u);
  let index = 0;
  await assert.rejects(loadCapturedWaitSessions(async () => page([row(++index)], 'cGFnZTI='), new URLSearchParams(), new AbortController().signal), /cursor/u);
  await assert.rejects(loadCapturedWaitSessions(async () => page([row(1)]), new URLSearchParams(), new AbortController().signal,
    { id: target, observedUtc: observed, truncated: false }), /requested capture/u);
});

test('all 512 captured rows are bounded at eleven pages with honest partial coverage and cancellation', async () => {
  let reads = 0;
  const result = await loadCapturedWaitSessions(async () => {
    const offset = reads++ * 50, count = reads === 11 ? 12 : 50;
    return page(Array.from({ length: count }, (_, i) => row(offset + i + 1)), Buffer.from(`page${reads}`).toString('base64'));
  }, new URLSearchParams(), new AbortController().signal);
  assert.equal(reads, 11); assert.equal(result.rows.length, 512); assert.equal(result.limited, true);
  const controller = new AbortController();
  await assert.rejects(loadCapturedWaitSessions(async () => { controller.abort(); return page([row(1)]); }, new URLSearchParams(), controller.signal), /abort/iu);
  const missing = await loadCapturedWaitSessions(async () => ({ ...page([]), snapshotId: null, observedUtc: null, state: 'unavailable' }), new URLSearchParams(), new AbortController().signal,
    { id: snapshot, observedUtc: observed, truncated: false });
  assert.equal(missing.rows.length, 0); assert.equal(missing.page.state, 'unavailable');
});
