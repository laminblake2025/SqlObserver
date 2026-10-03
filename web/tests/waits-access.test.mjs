import test from 'node:test';
import assert from 'node:assert/strict';
import { ActivityReadBoundary, activityAccessDenied } from '../src/features/activity/activityAccess.ts';
import { ApiRequestError } from '../src/apiClient.ts';
import { ActivityRequestError } from '../src/features/activity/activityParser.mjs';
import { getCurrentServerWaitPage, getServerWaitHistoryPage, getActivitySnapshot } from '../src/features/activity/activityApi.ts';
import { getServerWaitTrend } from '../src/features/activity/waitTrendApi.ts';
import { readLive } from '../src/features/activity/liveActivityApi.ts';
const target = '11111111-1111-4111-8111-111111111111';
const window = { fromUtc: '2026-09-30T13:00:00.123456Z', toUtc: '2026-09-30T14:00:00.654321Z' };
test('definitive native HTTP denial is distinct from collector200permission-denied coverage and ordinary failure', () => {
  for (const status of [401, 403]) {
    assert.equal(activityAccessDenied(new ActivityRequestError('safe', status)), true);
    assert.equal(activityAccessDenied(new ApiRequestError('safe', status)), true);
  }
  assert.equal(activityAccessDenied(new ActivityRequestError('safe', 503)), false);
  assert.equal(activityAccessDenied({ status: 403 }), false);
  assert.equal(activityAccessDenied({ evidence: { outcome: 'permission_denied' } }), false);
});
test('denial synchronously cancels every parallel owner and invalidates protected idle caches', () => {
  const boundary = new ActivityReadBoundary(); let firstPage = 'protected', history = 'protected';
  boundary.subscribeInvalidation(() => { firstPage = undefined; history = undefined; });
  const current = boundary.begin('current', 'current'), trend = boundary.begin('trend', 'trend');
  boundary.deny();
  assert.equal(firstPage, undefined); assert.equal(history, undefined);
  assert.equal(current.controller.signal.aborted, true); assert.equal(trend.controller.signal.aborted, true);
  assert.equal(boundary.current(trend), false); assert.equal(boundary.recover(trend), false);
  assert.equal(boundary.begin('current', 'auto'), undefined);
});
test('explicit validated recovery opens only the source read by its accepted owner', () => {
  const boundary = new ActivityReadBoundary(); boundary.deny();
  const failed = boundary.begin('history', 'recover', true); boundary.finish(failed);
  assert.equal(boundary.allowed('history'), false);
  const successful = boundary.begin('history', 'recover', true);
  assert.equal(boundary.recover(successful), true);
  assert.equal(boundary.allowed('history'), true); assert.equal(boundary.allowed('current'), false);
  assert.equal(boundary.allowed('trend'), false); assert.equal(boundary.allowed('capture-metadata'), false);
  boundary.finish(successful); assert.notEqual(boundary.begin('history', 'auto'), undefined);
});
test('a second denial cancels recovery and re-closes all previously restored sources', () => {
  const boundary = new ActivityReadBoundary(); boundary.deny();
  const history = boundary.begin('history', 'history', true); boundary.recover(history); boundary.finish(history);
  const current = boundary.begin('current', 'current', true);
  boundary.deny(); assert.equal(boundary.allowed('history'), false); assert.equal(boundary.recover(current), false);
});
test('partial supporting recovery grants only actual successful native source fields', () => {
  const boundary = new ActivityReadBoundary(); boundary.deny();
  const empty = boundary.begin('supporting', 'aggregate', true);
  boundary.recover(empty, []); boundary.finish(empty); assert.equal(boundary.allowed('supporting'), false);
  const partial = boundary.begin('supporting', 'aggregate', true);
  boundary.recover(partial, ['wait-history']); boundary.finish(partial);
  assert.equal(boundary.allowed('wait-history'), true); assert.equal(boundary.allowed('supporting'), false); assert.equal(boundary.allowed('blocking-history'), false);
});
test('superseded, unmounted and ABA-retired boundaries cannot commit a late result', () => {
  const original = new ActivityReadBoundary(); const old = original.begin('trend', 'owner');
  const next = original.begin('trend', 'owner'); assert.equal(original.current(old), false); assert.equal(original.current(next), true);
  original.dispose(); assert.equal(original.current(next), false); assert.equal(original.begin('trend', 'again', true), undefined);
  const newContext = new ActivityReadBoundary(); assert.equal(newContext.current(next), false);
});
test('current/history/trend/live readers preserve401403without reflecting rejected provider bodies', async () => {
  const original = globalThis.fetch;
  try { for (const status of [401, 403]) {
    globalThis.fetch = async () => new Response('PROVIDER_SECRET', { status });
    for (const operation of [() => getCurrentServerWaitPage(target, new AbortController().signal),
      () => getServerWaitHistoryPage(target, window, new AbortController().signal),
      () => getServerWaitTrend(target, window, new AbortController().signal),
      () => readLive(target, '', new URLSearchParams(), new AbortController().signal)])
      await assert.rejects(operation(), error => activityAccessDenied(error) && !error.message.includes('PROVIDER_SECRET'));
  } } finally { globalThis.fetch = original; }
});
test('aggregate denial callback runs before acceptance while ordinary503remains a safe source gap', async () => {
  const original = globalThis.fetch;
  try {
    let denied = 0; globalThis.fetch = async () => new Response('PROVIDER_SECRET', { status: 403 });
    const controller = new AbortController();
    await assert.rejects(getActivitySnapshot(target, controller.signal, window, () => { denied++; controller.abort(); }), error => activityAccessDenied(error));
    assert.equal(denied, 1);
    globalThis.fetch = async () => new Response('PROVIDER_SECRET', { status: 503 });
    const result = await getActivitySnapshot(target, new AbortController().signal, window, () => denied++);
    assert.equal(denied, 1); assert.equal(result.errors.length, 6); assert.equal(result.errors.join('').includes('PROVIDER_SECRET'), false);
  } finally { globalThis.fetch = original; }
});
test('supporting automatic reads omit histories that have not been explicitly restored', async () => {
  const original = globalThis.fetch; const paths = [];
  try {
    globalThis.fetch = async url => { paths.push(String(url)); return new Response('{}', { status: 503 }); };
    await getActivitySnapshot(target, new AbortController().signal, window, undefined, ['sessions', 'requests', 'waits', 'blocking']);
    assert.equal(paths.length, 4); assert.equal(paths.some(path => path.includes('/history')), false);
  } finally { globalThis.fetch = original; }
});
