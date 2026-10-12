import assert from 'node:assert/strict';
import test from 'node:test';
import { ConfigurationCollectionError, setServerConfigurationCollection } from '../src/features/health/healthApi.ts';

const target = '11111111-1111-4111-8111-000000000001';
const valid = () => ({ targetId: target, targetRevision: 4, collectorEnabled: false, scheduleRevision: 7 });
const call = signal => setServerConfigurationCollection(target, 4, true, false, signal ?? new AbortController().signal);
const unknown = error => error instanceof ConfigurationCollectionError && error.outcomeUnknown && !error.message.includes('PROVIDER_SECRET');
const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

test('configuration collection consumes only the exact native applied receipt', async () => {
  const original = globalThis.fetch; const requests = []; let body = valid();
  globalThis.fetch = async (url, options) => { requests.push({ url, options }); return json(body); };
  try {
    await call(); assert.equal(requests.length, 1);
    assert.deepEqual(JSON.parse(requests[0].options.body), { expectedTargetRevision: 4, expectedEnabled: true, enabled: false });
    assert.equal(requests[0].options.credentials, 'same-origin'); assert.equal(requests[0].options.method, 'PUT');
    for (const patch of [{ targetId: '11111111-1111-4111-8111-000000000002' }, { targetRevision: 5 },
      { collectorEnabled: true }, { scheduleRevision: null }, { scheduleRevision: 0 }, { scheduleRevision: Number.MAX_SAFE_INTEGER + 1 }]) {
      body = { ...valid(), ...patch }; await assert.rejects(call(), unknown);
    }
  } finally { globalThis.fetch = original; }
});

test('configuration invalid requests and already-cancelled commands never dispatch', async () => {
  const original = globalThis.fetch; let requests = 0;
  globalThis.fetch = async () => { requests++; throw new Error(); };
  try {
    for (const args of [['bad', 4, true, false], [target, 0, true, false], [target, 4, undefined, false], [target, 4, true, undefined]])
      await assert.rejects(setServerConfigurationCollection(...args, new AbortController().signal), error => error.status === 400 && !error.outcomeUnknown);
    const controller = new AbortController(), reason = new Error('Cancelled'); controller.abort(reason);
    await assert.rejects(call(controller.signal), error => error === reason); assert.equal(requests, 0);
  } finally { globalThis.fetch = original; }
});

test('configuration rejections preserve status; transport and invalid successes have an unknown outcome', async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [400, 401, 403, 404, 409, 429, 500]) {
      globalThis.fetch = async () => json({ detail: 'PROVIDER_SECRET' }, status);
      await assert.rejects(call(), error => error.status === status && error.outcomeUnknown === (status === 500) && !error.message.includes('PROVIDER_SECRET'));
    }
    for (const response of [json(valid(), 201), new Response(null, { status: 204 }),
      new Response('PROVIDER_SECRET', { headers: { 'content-type': 'text/plain' } }),
      new Response('{', { headers: { 'content-type': 'application/json' } }),
      new Response(JSON.stringify(valid()), { headers: { 'content-type': 'application/json', 'content-length': '16385' } })]) {
      globalThis.fetch = async () => response; await assert.rejects(call(), unknown);
    }
    globalThis.fetch = async () => { throw new Error('PROVIDER_SECRET'); }; await assert.rejects(call(), unknown);
    const controller = new AbortController(); globalThis.fetch = async () => { controller.abort(); return json(valid()); };
    await assert.rejects(call(controller.signal), unknown);
  } finally { globalThis.fetch = original; }
});
