import assert from "node:assert/strict";
import test from "node:test";
import { AcknowledgementAttempts } from "../src/features/alerts/acknowledgementAttempts.ts";
import { acknowledgeAlert } from "../src/features/alerts/alertApi.ts";

const target = "11111111-1111-4111-8111-111111111111";
const alert = "22222222-2222-4222-8222-222222222222";
const observed = "2026-09-24T12:00:00Z";

test("failed acknowledgement retries keep the operation ID for that alert episode", async () => {
  const attempts = new AcknowledgementAttempts();
  const first = attempts.tokenFor(target, alert, observed);
  assert.equal(attempts.tokenFor(target.toUpperCase(), alert, observed), first);
  assert.notEqual(attempts.tokenFor(target, alert, "2026-09-24T12:01:00Z"), first);
  const previous = globalThis.fetch;
  const bodies = [];
  try {
    globalThis.fetch = async (_url, options) => {
      bodies.push(JSON.parse(options.body));
      return new Response(null, { status: bodies.length === 1 ? 503 : 200 });
    };
    const signal = new AbortController().signal;
    await assert.rejects(acknowledgeAlert(target, alert, first, signal), /could not be acknowledged/u);
    await acknowledgeAlert(target, alert, attempts.tokenFor(target, alert, observed), signal);
    assert.deepEqual(bodies, [{ operationId: first }, { operationId: first }]);
    attempts.complete(target, alert, observed);
    assert.notEqual(attempts.tokenFor(target, alert, observed), first);
  } finally {
    globalThis.fetch = previous;
  }
});
