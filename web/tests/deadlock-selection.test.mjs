import test from 'node:test';
import assert from 'node:assert/strict';
import { deadlockSelectionHref, readDeadlockSelection, retainDeadlockSelectionHref,
  deadlockOccurrenceInScope, matchesDeadlockSelection } from '../src/features/deadlocks/deadlockSelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const eventId = '33333333-3333-4333-8333-000000000001';
const scope = { target, range: 'custom', from: '2026-09-26T08:00:00.123456Z',
  to: '2026-09-26T12:00:00.654321Z', databaseId: 5, compare: true };
const selection = { eventId, occurredAtUtc: '2026-09-26T10:00:00.123456Z' };

test('deadlock URL restores exact event occurrence and all workspace context', () => {
  const href = deadlockSelectionHref(scope, selection);
  assert.deepEqual(readDeadlockSelection(href, target.toUpperCase()), selection);
  const parameters = new URLSearchParams(href.split('?')[1]);
  assert.equal(parameters.get('from'), scope.from);
  assert.equal(parameters.get('to'), scope.to);
  assert.equal(parameters.get('databaseId'), '5');
  assert.equal(parameters.get('compare'), '1');
  assert.deepEqual(readDeadlockSelection(href.replace(eventId, eventId.toUpperCase())), selection);
  assert.equal(readDeadlockSelection(deadlockSelectionHref(scope)), undefined);
});

test('deadlock URL rejects partial, duplicate, malformed and foreign identities', () => {
  const href = deadlockSelectionHref(scope, selection);
  for (const suffix of ['&deadlockEvent=' + eventId, '&deadlockAt=' + selection.occurredAtUtc,
    '&target=' + target, '&range=1h', '&databaseId=6', '&compare=0', '&from=' + scope.from,
    '&at=' + selection.occurredAtUtc, '&event=' + eventId, '&timelineEvent=deadlock:' + eventId])
    assert.equal(readDeadlockSelection(href + suffix), null, suffix);
  assert.equal(readDeadlockSelection(href.replace(/&deadlockAt=[^&]+/u, '')), null);
  assert.equal(readDeadlockSelection(href.replace('deadlocks?', 'activity?')), null);
  assert.equal(readDeadlockSelection(href, '11111111-1111-4111-8111-000000000002'), null);
  assert.equal(readDeadlockSelection(href.replace(eventId, '00000000-0000-0000-0000-000000000000')), null);
  assert.equal(readDeadlockSelection(href.replace(encodeURIComponent(selection.occurredAtUtc), '2026-02-30T10%3A00%3A00Z')), null);
});

test('deadlock custom windows use exact half-open microsecond boundaries', () => {
  assert.equal(deadlockOccurrenceInScope(scope.from, scope), true);
  assert.equal(deadlockOccurrenceInScope('2026-09-26T08:00:00.123455Z', scope), false);
  assert.equal(deadlockOccurrenceInScope('2026-09-26T12:00:00.654320Z', scope), true);
  assert.equal(deadlockOccurrenceInScope(scope.to, scope), false);
  assert.throws(() => deadlockSelectionHref(scope, { ...selection, occurredAtUtc: scope.to }));
  assert.equal(readDeadlockSelection(deadlockSelectionHref(scope, selection).replace(encodeURIComponent(selection.occurredAtUtc), encodeURIComponent(scope.to))), null);
  assert.equal(deadlockOccurrenceInScope(selection.occurredAtUtc, { ...scope, to: scope.from }), false);
  assert.equal(deadlockOccurrenceInScope(selection.occurredAtUtc, { ...scope, from: '2026-08-01T00:00:00Z' }), false);
});

test('retained deadlock identity never matches only a timestamp or rounded occurrence', () => {
  assert.equal(matchesDeadlockSelection(selection, { ...selection, eventId: eventId.toUpperCase(), occurredAtUtc: '2026-09-26T10:00:00.1234560+00:00' }), true);
  assert.equal(matchesDeadlockSelection(selection, { ...selection, occurredAtUtc: '2026-09-26T10:00:00.123457Z' }), false);
  assert.equal(matchesDeadlockSelection(selection, { ...selection, eventId: target }), false);
  assert.equal(matchesDeadlockSelection(selection, { ...selection, occurredAtUtc: '2026-09-26T10:00:00.1234561Z' }), false);
});

test('deadlock retention keeps server-wide context, drops incompatible target/window, preserves Live', () => {
  const current = deadlockSelectionHref(scope, selection);
  const live = deadlockSelectionHref({ ...scope, range: '1h' });
  const retained = retainDeadlockSelectionHref(live, current);
  assert.deepEqual(readDeadlockSelection(retained), selection);
  assert.equal(new URLSearchParams(retained.split('?')[1]).get('range'), '1h');
  assert.equal(new URLSearchParams(retained.split('?')[1]).has('from'), false);
  assert.deepEqual(readDeadlockSelection(retainDeadlockSelectionHref(deadlockSelectionHref({ ...scope, databaseId: 6 }), current)), selection);
  for (const changed of [{ ...scope, target: '11111111-1111-4111-8111-000000000002' }, { ...scope, from: '2026-09-26T11:00:00Z' }]) {
    const next = deadlockSelectionHref(changed);
    assert.equal(retainDeadlockSelectionHref(next, current), next);
  }
  assert.equal(retainDeadlockSelectionHref('#/queries?target=' + target, current), '#/queries?target=' + target);
});
