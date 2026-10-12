import test from "node:test";
import assert from "node:assert/strict";
import {
  DestinationAttempts, prepareDestination, createDestination, changeDestination,
} from "../src/features/alerts/destinationAdminActions.ts";

const target = "11111111-1111-4111-8111-111111111111";
const destinationId = "33333333-3333-4333-8333-333333333333";
const values = { kind: "https-webhook", configurationReference: "ops-webhook", enabled: true };
const item = { destinationId, kind: values.kind, enabled: true, approved: false, revision: 4 };

test("destination form accepts references and rejects URLs, controls, and wrong Event Log source", () => {
  assert.deepEqual(prepareDestination({ ...values, configurationReference: " ops-webhook " }), values);
  assert.throws(() => prepareDestination({ ...values, configurationReference: "https://example.com" }), /reference/);
  assert.throws(() => prepareDestination({ ...values, configurationReference: "bad\nkey" }), /reference/);
  assert.throws(() => prepareDestination({ ...values, configurationReference: "x".repeat(257) }), /256/);
  assert.deepEqual(prepareDestination({ kind: "windows-event-log", configurationReference: "SqlObserver Alerts", enabled: false }),
    { kind: "windows-event-log", configurationReference: "SqlObserver Alerts", enabled: false });
  assert.throws(() => prepareDestination({ kind: "windows-event-log", configurationReference: "wrong", enabled: true }), /Windows Event Log/);
});

test("destination attempts keep create and each revision-fenced action stable", () => {
  const attempts = new DestinationAttempts();
  const first = attempts.createFor(target, values);
  assert.deepEqual(attempts.createFor(target, values), first);
  assert.notDeepEqual(attempts.createFor(target, { ...values, enabled: false }), first);
  attempts.completeCreate(target, values);
  assert.notDeepEqual(attempts.createFor(target, values), first);
  const update = attempts.changeFor(target, destinationId, 4, "update", values);
  assert.equal(attempts.changeFor(target, destinationId, 4, "update", values), update);
  assert.notEqual(attempts.changeFor(target, destinationId, 4, "approve", values), update);
  assert.notEqual(attempts.changeFor(target, destinationId, 5, "update", values), update);
  attempts.completeChange(target, destinationId, 4, "update", values);
  assert.notEqual(attempts.changeFor(target, destinationId, 4, "update", values), update);
});

test("destination requests use explicit routes, references, and expected revisions", async () => {
  const previous = globalThis.fetch;
  try {
    const requests = [];
    globalThis.fetch = async (url, options) => {
      requests.push({ url, options, body: JSON.parse(options.body) });
      return new Response(null, { status: 200 });
    };
    const signal = new AbortController().signal;
    const operationId = crypto.randomUUID();
    await createDestination(target, values, { destinationId, operationId }, signal);
    await changeDestination(target, item, "update", values, operationId, signal);
    await changeDestination(target, item, "approve", values, operationId, signal);
    await changeDestination(target, item, "disable", values, operationId, signal);
    assert.match(requests[0].url, /\/alerts\/destinations$/);
    assert.equal(requests[0].options.method, "POST");
    assert.deepEqual(requests[0].body, { ...values, destinationId, operationId });
    assert.equal(requests[0].options.credentials, "same-origin");
    assert.match(requests[1].url, /\/alerts\/destinations\/33333333-3333-4333-8333-333333333333$/);
    assert.equal(requests[1].options.method, "PUT");
    assert.deepEqual(requests[1].body, { ...values, destinationId, operationId, expectedRevision: 4 });
    assert.match(requests[2].url, /\/approve$/);
    assert.equal(requests[2].options.method, "POST");
    assert.match(requests[3].url, /\/disable$/);
    assert.equal(requests[3].body.enabled, false);
    globalThis.fetch = async () => new Response(null, { status: 409 });
    await assert.rejects(() => changeDestination(target, item, "update", values, operationId, signal), /changed/);
  } finally { globalThis.fetch = previous; }
});
