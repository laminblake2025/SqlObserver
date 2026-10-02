import assert from 'node:assert/strict';
import test from 'node:test';
import { updateObservationTarget, requestObservationTargetRediscovery, retireObservationTarget,
  TargetManagementError } from '../src/features/targets/targetApi.ts';
import { maximumTargetResponseBytes } from '../src/features/targets/targetResponseParser.ts';
import { alpha, beta, registrationRequest, registeredTarget } from '../component-tests/onboardingFixture.mjs';

const update = mode => {
  const { instanceId, instanceKey, ...configuration } = registrationRequest(mode);
  return { expectedRevision: 7, ...configuration };
};
const pending = request => {
  const { expectedRevision, ...configuration } = request;
  return registeredTarget({ instanceId: alpha, instanceKey: 'synthetic-sql', ...configuration }, {
    configurationRevision: 8, capabilityReasons: ['discovery_pending'], lastDiscoveryAtUtc: null,
  });
};
const retired = (revision = 8) => registeredTarget(registrationRequest(), {
  lifecycle: 'retired', capabilityStatus: 'retired', capabilityReasons: [], configurationRevision: revision,
});
const json = (value, status = 200) => new Response(JSON.stringify(value), { status,
  headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' } });
const safeError = (status, outcomeUnknown) => error => {
  assert.ok(error instanceof TargetManagementError);
  assert.equal(error.status, status); assert.equal(error.outcomeUnknown, outcomeUnknown);
  assert.doesNotMatch(error.message, /provider-secret|SyntaxError|password|stack trace/u);
  return true;
};

test('native update sends only revision-checked TCP or named configuration and validates exact projected changes', async () => {
  const original = globalThis.fetch, requests = [];
  let receipt;
  globalThis.fetch = async (path, init) => { requests.push({ path, init }); return json(receipt); };
  try {
    for (const mode of ['tcp', 'named']) {
      const request = { ...update(mode), displayName: '<changed> é', host: 'SQL.EXAMPLE.TEST' };
      receipt = pending(request);
      const result = await updateObservationTarget(alpha.toUpperCase(), request, new AbortController().signal);
      assert.equal(result.configurationRevision, 8); assert.equal(result.displayName, request.displayName);
      const sent = requests.at(-1);
      assert.equal(sent.path, `/api/v1/observation-targets/${alpha}`); assert.equal(sent.init.method, 'PUT');
      assert.equal(sent.init.credentials, 'same-origin'); assert.equal(sent.init.redirect, 'error');
      assert.deepEqual(JSON.parse(sent.init.body), request);
      assert.equal(Object.hasOwn(JSON.parse(sent.init.body), mode === 'named' ? 'tcpPort' : 'namedInstance'), false);
      assert.equal(new Headers(sent.init.headers).get('content-type'), 'application/json');
    }
  } finally { globalThis.fetch = original; }
});

test('native rediscovery and retirement use existing POST routes and revision body without operation identity', async () => {
  const original = globalThis.fetch, requests = [];
  let receipt = pending(update());
  globalThis.fetch = async (path, init) => { requests.push({ path, init }); return json(receipt); };
  try {
    assert.equal((await requestObservationTargetRediscovery(alpha, 7, new AbortController().signal)).lifecycle, 'pending_discovery');
    receipt = retired();
    assert.equal((await retireObservationTarget(alpha, 7, new AbortController().signal)).lifecycle, 'retired');
    assert.deepEqual(requests.map(item => item.path), [
      `/api/v1/observation-targets/${alpha}/rediscovery`, `/api/v1/observation-targets/${alpha}/retire`,
    ]);
    for (const { init } of requests) {
      assert.equal(init.method, 'POST'); assert.deepEqual(JSON.parse(init.body), { expectedRevision: 7 });
    }
  } finally { globalThis.fetch = original; }
});

test('the submitted update configuration remains immutable while its receipt is in flight', async () => {
  const original = globalThis.fetch, request = update(), submitted = { ...request };
  let started, deliver;
  const dispatched = new Promise(resolve => { started = resolve; });
  globalThis.fetch = async () => { started(); return new Promise(resolve => { deliver = resolve; }); };
  try {
    const operation = updateObservationTarget(alpha, request, new AbortController().signal);
    await dispatched; request.displayName = 'Later draft'; request.host = 'changed.example.test';
    deliver(json(pending(submitted)));
    const receipt = await operation;
    assert.equal(receipt.displayName, submitted.displayName); assert.equal(receipt.host, submitted.host);
  } finally { globalThis.fetch = original; }
});

test('all native management routes preserve AlreadyRetired receipts even when expected revision is stale or ahead', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => json(retired(12));
  try {
    for (const revision of [7, 12, 20]) {
      for (const action of [
        () => updateObservationTarget(alpha, { ...update(), expectedRevision: revision, displayName: 'Unapplied draft' }, new AbortController().signal),
        () => requestObservationTargetRediscovery(alpha, revision, new AbortController().signal),
        () => retireObservationTarget(alpha, revision, new AbortController().signal),
      ]) {
        const receipt = await action();
        assert.equal(receipt.lifecycle, 'retired'); assert.equal(receipt.configurationRevision, 12);
        assert.equal(receipt.displayName, registrationRequest().displayName);
      }
    }
  } finally { globalThis.fetch = original; }
});

test('unsafe update and rediscovery receipts cannot claim a native applied mutation', async () => {
  const original = globalThis.fetch, request = update();
  let receipt;
  globalThis.fetch = async () => json(receipt);
  try {
    for (const overrides of [{ instanceId: beta }, { configurationRevision: 7 }, { configurationRevision: 9 },
      { lifecycle: 'active' }, { capabilityStatus: 'supported' }, { capabilityReasons: ['rediscovery_pending'] },
      { lastDiscoveryAtUtc: '2026-09-30T13:00:00.000Z' }, { host: 'wrong.example.test' },
      { tcpPort: 1433 }, { certificateHostName: null }, { displayName: 'Unrequested name' }]) {
      receipt = { ...pending(request), ...overrides };
      await assert.rejects(updateObservationTarget(alpha, request, new AbortController().signal), safeError(200, true));
    }
    for (const overrides of [{ instanceId: beta }, { configurationRevision: 7 }, { configurationRevision: 9 },
      { lifecycle: 'disabled', capabilityStatus: 'disabled' }, { capabilityReasons: [] }]) {
      receipt = { ...pending(request), ...overrides };
      await assert.rejects(requestObservationTargetRediscovery(alpha, 7, new AbortController().signal), safeError(200, true));
    }
  } finally { globalThis.fetch = original; }
});

test('retirement receipts require exact target identity and native terminal projection', async () => {
  const original = globalThis.fetch;
  let receipt;
  globalThis.fetch = async () => json(receipt);
  try {
    for (const overrides of [{ instanceId: beta }, { lifecycle: 'active' }, { capabilityStatus: 'pending' },
      { capabilityReasons: ['provider-secret'] }, { lastDiscoveryAtUtc: '2026-09-30T13:00:00.000Z' },
      { configurationRevision: Number.MAX_SAFE_INTEGER + 1 }]) {
      receipt = { ...retired(), ...overrides };
      await assert.rejects(retireObservationTarget(alpha, 7, new AbortController().signal), safeError(200, true));
    }
  } finally { globalThis.fetch = original; }
});

test('malformed, wrong-content-type, oversized and unexpected success responses are indeterminate and sanitized', async () => {
  const original = globalThis.fetch;
  let response;
  globalThis.fetch = async () => response;
  try {
    for (const value of [
      new Response('{provider-secret', { headers: { 'Content-Type': 'application/json' } }),
      new Response('provider-secret', { headers: { 'Content-Type': 'text/html' } }),
      new Response(new Uint8Array([0xff]), { headers: { 'Content-Type': 'application/json' } }),
      new Response(JSON.stringify(pending(update())) + ' '.repeat(maximumTargetResponseBytes), { headers: { 'Content-Type': 'application/json' } }),
      new Response(null, { status: 204 }), json(pending(update()), 201),
    ]) {
      response = value;
      await assert.rejects(requestObservationTargetRediscovery(alpha, 7, new AbortController().signal), error => {
        assert.ok(error instanceof TargetManagementError); assert.equal(error.outcomeUnknown, true);
        assert.doesNotMatch(error.message, /provider-secret|SyntaxError/u); return true;
      });
    }
  } finally { globalThis.fetch = original; }
});

test('definite administrative refusals preserve status, including conflict readback and safe rate-limit retry', async () => {
  const original = globalThis.fetch;
  let status;
  globalThis.fetch = async () => new Response('provider-secret', { status });
  try {
    for (status of [400, 401, 403, 404, 409, 429]) {
      await assert.rejects(retireObservationTarget(alpha, 7, new AbortController().signal), safeError(status, false));
    }
    status = 409;
    await assert.rejects(retireObservationTarget(alpha, 7, new AbortController().signal), /Reload current state/u);
  } finally { globalThis.fetch = original; }
});

test('network, server failures and cancellation after dispatch are unknown outcomes rather than replay invitations', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async () => { throw new Error('provider-secret'); };
    await assert.rejects(updateObservationTarget(alpha, update(), new AbortController().signal), safeError(undefined, true));
    for (const status of [500, 502, 503, 504, 408]) {
      globalThis.fetch = async () => new Response('provider-secret', { status });
      await assert.rejects(retireObservationTarget(alpha, 7, new AbortController().signal), safeError(status, true));
    }
    let started;
    const dispatched = new Promise(resolve => { started = resolve; });
    globalThis.fetch = async () => { started(); return new Promise(() => {}); };
    const controller = new AbortController();
    const operation = requestObservationTargetRediscovery(alpha, 7, controller.signal);
    await dispatched; controller.abort();
    await assert.rejects(operation, safeError(undefined, true));
  } finally { globalThis.fetch = original; }
});

test('invalid identity/revision/configuration and unknown credential fields never reach native transport', async () => {
  const original = globalThis.fetch;
  let reads = 0;
  globalThis.fetch = async () => { reads++; return json(pending(update())); };
  try {
    for (const request of [null, { ...update(), expectedRevision: 0 }, { ...update(), expectedRevision: 1.5 },
      { ...update(), expectedRevision: Number.MAX_SAFE_INTEGER + 1 }, { ...update(), namedInstance: 'APP' },
      { ...update(), tcpPort: 65536 }, { ...update(), host: 'sql;password=provider-secret' },
      { ...update(), host: '\u212aSQL' }, { ...update(), displayName: 'é'.repeat(257) }, { ...update(), displayName: '\ud800' },
      { ...update(), certificateHostName: '' }, { ...update(), password: 'provider-secret' },
    ]) await assert.rejects(updateObservationTarget(alpha, request, new AbortController().signal), safeError(undefined, false));
    for (const id of ['provider-secret', `${alpha}/retire`, '00000000-0000-0000-0000-000000000000']) {
      await assert.rejects(retireObservationTarget(id, 7, new AbortController().signal), safeError(undefined, false));
    }
    await assert.rejects(requestObservationTargetRediscovery(alpha, -1, new AbortController().signal), safeError(undefined, false));
    assert.equal(reads, 0);
  } finally { globalThis.fetch = original; }
});
