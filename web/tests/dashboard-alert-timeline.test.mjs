import test from 'node:test';
import assert from 'node:assert/strict';
import { alertFiredTimelineEvents, loadAlertTimelineInbox, timelineEventsInWindow } from '../src/components/dashboardTimelineModel.ts';

const target = '11111111-1111-4111-8111-000000000001', other = '11111111-1111-4111-8111-000000000002';
const from = '2026-10-02T08:00:00.123456Z', to = '2026-10-02T12:00:00.123456Z';
const scope = { target, range: 'custom', from, to, compare: true, databaseId: 6 };
const id = value => `22222222-2222-4222-8222-${String(value).padStart(12, '0')}`;
const item = (value = 1, extra = {}) => ({ alertId: id(value), ruleId: '33333333-3333-4333-8333-333333333333', ruleName: 'Synthetic blocked sessions', state: 'firing',
  firstObservedUtc: '2026-10-02T07:00:00.000000Z', firedUtc: '2026-10-02T09:00:00.123456Z', deliverySuppressed: false, ...extra });
const inbox = (items, extra = {}) => ({ targetId: target, items, snapshotUtc: '2026-10-02T12:30:00.123456Z', pagesRead: 1, hasMore: false, ...extra });
const page = (items, extra = {}) => ({ targetId: target, items, snapshotUtc: '2026-10-02T12:30:00.123456Z', ...extra });
const events = items => alertFiredTimelineEvents(target, scope, from, to, inbox(items));

test('alert markers use native fired UTC and exact retained alert links without replacing missing fired time', () => {
  const loaded = events([item(), item(2, { state: 'acknowledged', firedUtc: '2026-10-02T10:00:00.1234560Z' }), item(3, { firedUtc: undefined }), item(4, { state: 'resolved' }), item(5, { state: 'pending' })]);
  assert.equal(loaded.length, 2);
  assert.equal(loaded[0].timeBasis, 'fired'); assert.equal(loaded[0].kind, 'alert'); assert.equal(loaded[0].timeUtc, item().firedUtc);
  assert.match(loaded[0].detail, /current-inbox evidence, not a reconstructed transition history/);
  assert.match(loaded[1].detail, /acknowledged/);
  const query = new URLSearchParams(loaded[0].href.split('?')[1]);
  assert.equal(loaded[0].href.split('?')[0], '#/alerts');
  for (const [key, value] of Object.entries({ target, range: 'custom', from, to, databaseId: '6', compare: '1', alert: id(1) })) assert.equal(query.get(key), value);
  assert.equal(timelineEventsInWindow(loaded, from, to).length, 2);
});

test('alert fired bounds are half-open at one microsecond and duplicate timeline identities keep the first', () => {
  const loaded = events([item(1, { firedUtc: from }), item(2, { firedUtc: '2026-10-02T12:00:00.123455Z' }), item(3, { firedUtc: to }), item(4, { firedUtc: '2026-10-02T08:00:00.123455Z' }), item(1, { ruleName: 'Duplicate must not replace first' })]);
  // Different fired instants for the same native alert are distinct cycle evidence;
  // an exact duplicate marker has a stable identity and keeps its first title.
  assert.equal(loaded.length, 3);
  const duplicates = events([item(), item(1, { ruleName: 'Duplicate must not replace first' })]);
  assert.equal(duplicates.length, 1); assert.match(duplicates[0].title, /Synthetic blocked sessions/);
  assert.equal(events([item(1, { firedUtc: '2026-10-02T08:00:00.1234560Z' })]).length, 1);
});

test('malformed target scope timestamp and metadata fail closed without guessed marker times', () => {
  for (const row of [item(1, { firedUtc: '2026-02-30T08:00:00Z' }), item(1, { firedUtc: '2026-10-02T09:00:00.1234561Z' }), item(1, { alertId: 'invalid' }), item(1, { ruleName: '\u0000' }), item(1, { firstObservedUtc: 'invalid' }), item(1, { state: 'unknown' })]) assert.throws(() => events([row]));
  for (const metadata of [{ targetId: other }, { items: Array.from({ length: 501 }, (_, i) => item(i + 1)) }, { pagesRead: 6 }, { hasMore: 'yes' }, { snapshotUtc: 'invalid' }]) assert.throws(() => alertFiredTimelineEvents(target, scope, from, to, inbox([item()], metadata)));
  assert.throws(() => alertFiredTimelineEvents(target, { ...scope, target: other }, from, to, inbox([item()])));
  assert.throws(() => alertFiredTimelineEvents(target, { ...scope, to: '2026-10-02T12:00:00.123455Z' }, from, to, inbox([item()])));
});

test('current inbox loading bounds five snapshot-bound pages and exposes remaining rows', async () => {
  const cursors = [], controller = new AbortController(); let reads = 0;
  const result = await loadAlertTimelineInbox(target, async cursor => {
    cursors.push(cursor); const index = reads++;
    return page(Array.from({ length: 100 }, (_, j) => item(index * 100 + j + 1)), { nextCursor: Buffer.from('page-' + (index + 1)).toString('base64') });
  }, controller.signal);
  assert.equal(reads, 5); assert.equal(result.items.length, 500); assert.equal(result.pagesRead, 5); assert.equal(result.hasMore, true); assert.equal(cursors[0], undefined);
  assert.equal(alertFiredTimelineEvents(target, scope, from, to, result).length, 120);
});

test('current inbox rejects foreign target changed snapshot duplicate identities and cursor cycles', async () => {
  const controller = new AbortController();
  const invalidSequences = [
    [page([item()], { targetId: other })],
    [page([item()], { nextCursor: 'cGFnZTI=' }), page([item(2)], { snapshotUtc: '2026-10-02T12:30:00.123457Z' })],
    [page([item()], { nextCursor: 'cGFnZTI=' }), page([item(1, { alertId: id(1).toUpperCase() })])],
    [page([item()], { nextCursor: 'cGFnZTI=' }), page([item(2)], { nextCursor: 'cGFnZTI=' })],
    [page([item()], { nextCursor: 'invalid cursor' })],
    [page([item()], { nextCursor: 'cGFnZTI=', snapshotUtc: undefined })],
  ];
  for (const sequence of invalidSequences) { let index = 0; await assert.rejects(loadAlertTimelineInbox(target, async () => sequence[index++], controller.signal)); }
});

test('current inbox accepts exact equivalent snapshot precision and leaves absent snapshot explicit on a single page', async () => {
  let reads = 0;
  const loaded = await loadAlertTimelineInbox(target, async () => reads++ === 0 ? page([item()], { nextCursor: 'cGFnZTI=' }) : page([item(2)], { snapshotUtc: '2026-10-02T12:30:00.1234560Z' }), new AbortController().signal);
  assert.equal(loaded.items.length, 2); assert.equal(loaded.hasMore, false);
  const unbound = await loadAlertTimelineInbox(target, async () => page([item()], { snapshotUtc: undefined }), new AbortController().signal);
  assert.match(alertFiredTimelineEvents(target, scope, from, to, unbound)[0].detail, /Inbox snapshot: not reported/);
});

test('aborted current inbox rejects late response and never dispatches another page', async () => {
  const controller = new AbortController(); let calls = 0;
  await assert.rejects(loadAlertTimelineInbox(target, async () => { calls++; controller.abort(); return page([item()], { nextCursor: 'cGFnZTI=' }); }, controller.signal), { name: 'AbortError' });
  assert.equal(calls, 1);
});

test('native inbox coalesced observation cannot be newer than its snapshot at microsecond precision', async () => {
  const cutoff = item().firedUtc;
  const matching = await loadAlertTimelineInbox(target, async () => page([item()], { snapshotUtc: cutoff }), new AbortController().signal);
  assert.equal(matching.items.length, 1);
  for (const row of [item(), item(2, { firedUtc: undefined, firstObservedUtc: cutoff })]) {
    const earlierSnapshot = '2026-10-02T09:00:00.123455Z';
    await assert.rejects(loadAlertTimelineInbox(target, async () => page([row], { snapshotUtc: earlierSnapshot }), new AbortController().signal), /newer than its inbox snapshot/);
    assert.throws(() => alertFiredTimelineEvents(target, scope, from, to, inbox([row], { snapshotUtc: earlierSnapshot })), /newer than its inbox snapshot/);
  }
});
