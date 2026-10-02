import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTargetAlertJump } from '../src/features/alerts/targetAlertJump.ts';

const target = 'abcdefab-abcd-4abc-8abc-abcdefabcdef';
const first = '44444444-4444-4444-8444-444444444444', requested = 'fedcbafe-fedc-4fed-8fed-fedcbafedcba';
const snapshotUtc = '2026-09-30T12:00:00.000Z';
const row = (alertId = first) => ({ alertId, ruleName: 'Synthetic rule', state: 'firing' });
const page = (items = [row()], nextCursor, extra = {}) => ({ targetId: target, items, nextCursor, snapshotUtc, ...extra });

test('target alert restore traverses bounded pages and finds the exact case-insensitive requested alert', async () => {
  const cursors = [];
  const pages = [page([row()], 'second'), page([row(first.toUpperCase()), row(requested.toUpperCase())], 'third')];
  const result = await loadTargetAlertJump(async cursor => { cursors.push(cursor); return pages[cursors.length - 1]; }, target.toUpperCase(), requested);
  assert.deepEqual(cursors, [undefined, 'second']);
  assert.equal(result.found, true);
  assert.equal(result.items.length, 2);
  assert.equal(result.nextCursor, 'third');
  assert.equal(result.snapshotUtc, snapshotUtc);
});

test('missing requested alert stops after five pages and never invents selection of another loaded row', async () => {
  let calls = 0;
  const result = await loadTargetAlertJump(async () => page([row()], `page-${++calls}`), target, requested);
  assert.equal(calls, 5);
  assert.equal(result.found, false);
  assert.deepEqual(result.items, [row()]);
  assert.equal(result.nextCursor, 'page-5');
  assert.equal((await loadTargetAlertJump(async () => page(), target, undefined)).found, false);
});

test('target alert restore rejects foreign targets, oversized pages, snapshot drift and repeated cursors', async () => {
  await assert.rejects(loadTargetAlertJump(async () => page(undefined, undefined, { targetId: 'another-target' }), target, requested), /target/);
  await assert.rejects(loadTargetAlertJump(async () => page(Array(101).fill(row())), target, requested), /bounds/);
  let calls = 0;
  await assert.rejects(loadTargetAlertJump(async () => page([], 'next', { snapshotUtc: calls++ ? '2026-09-30T12:00:01Z' : snapshotUtc }), target, requested), /snapshot changed/);
  await assert.rejects(loadTargetAlertJump(async () => page([], 'next'), target, requested), /repeated a cursor/);
  await assert.rejects(loadTargetAlertJump(async () => page(undefined, undefined, { snapshotUtc: 'invalid' }), target, requested));
});

test('target alert page bound is validated before any repository read', async () => {
  for (const bound of [0, 6, 1.5, NaN]) {
    let calls = 0;
    await assert.rejects(loadTargetAlertJump(async () => { calls++; return page(); }, target, requested, bound), /page limit/);
    assert.equal(calls, 0);
  }
});
