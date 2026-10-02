import assert from 'node:assert/strict';
import test from 'node:test';
import { updateObservationTargetGrouping, TargetManagementError } from '../src/features/targets/targetApi.ts';

const alpha = '11111111-1111-4111-8111-000000000001';
const unknown = error => error instanceof TargetManagementError && error.outcomeUnknown && !error.message.includes('PROVIDER_SECRET');
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });

test('grouping accepts only an exact applied revision and normalized submitted labels', async () => {
  const original = globalThis.fetch;
  const valid = { instanceId: alpha, environment: 'production', tags: ['api', 'east'], groupingRevision: 3 };
  let response = valid;
  globalThis.fetch = async () => json(response);
  try {
    assert.deepEqual(await updateObservationTargetGrouping(alpha, 2, ' Production ', ['East', 'api'], new AbortController().signal), valid);
    for (const patch of [{ groupingRevision: 2 }, { groupingRevision: 4 }, { environment: 'other' },
      { tags: ['east', 'api'] }, { tags: ['api'] }, { instanceId: '11111111-1111-4111-8111-000000000002' }]) {
      response = { ...valid, ...patch };
      await assert.rejects(updateObservationTargetGrouping(alpha, 2, ' Production ', ['East', 'api'], new AbortController().signal), unknown);
    }
  } finally { globalThis.fetch = original; }
});

test('invalid grouping labels and revisions are refused before dispatch', async () => {
  const original = globalThis.fetch; let dispatched = 0;
  globalThis.fetch = async () => { dispatched++; throw new Error('PROVIDER_SECRET'); };
  try {
    for (const args of [[alpha, -1, null, []], [alpha, Number.MAX_SAFE_INTEGER, null, []], ['bad', 0, null, []],
      [alpha, 0, 'bad label', []], [alpha, 0, null, ['East', 'east']], [alpha, 0, null, ['']],
      [alpha, 0, null, Array.from({ length: 17 }, (_, index) => `tag${index}`)]]) {
      await assert.rejects(updateObservationTargetGrouping(...args, new AbortController().signal), error => error.status === 400 && !error.outcomeUnknown);
    }
    assert.equal(dispatched, 0);
    const controller = new AbortController(), reason = new Error('Caller cancelled'); controller.abort(reason);
    await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], controller.signal), error => error === reason);
    assert.equal(dispatched, 0);
  } finally { globalThis.fetch = original; }
});

test('grouping distinguishes rejected mutations from unknown outcomes without exposing provider bodies', async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [400, 401, 403, 404, 409, 429, 500]) {
      globalThis.fetch = async () => json({ detail: 'PROVIDER_SECRET' }, status);
      await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], new AbortController().signal),
        error => error.status === status && error.outcomeUnknown === (status === 500) && !error.message.includes('PROVIDER_SECRET'));
    }
    for (const response of [json({ detail: 'PROVIDER_SECRET' }), new Response(null, { status: 204 }),
      new Response('PROVIDER_SECRET', { headers: { 'content-type': 'text/plain' } })]) {
      globalThis.fetch = async () => response;
      await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], new AbortController().signal), unknown);
    }
    globalThis.fetch = async () => { throw new Error('PROVIDER_SECRET'); };
    await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], new AbortController().signal), unknown);
    const controller = new AbortController();
    globalThis.fetch = async () => { controller.abort(); return json({ instanceId: alpha, environment: null, tags: [], groupingRevision: 1 }); };
    await assert.rejects(updateObservationTargetGrouping(alpha, 0, null, [], controller.signal), unknown);
  } finally { globalThis.fetch = original; }
});
