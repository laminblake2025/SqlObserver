import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { decodeDeadlockCursor, deadlockUtc, validateDeadlockReceipt, validateDeadlockRequest,
  maximumDeadlockPages, maximumDeadlockRows } from '../src/features/deadlocks/deadlockPaging.ts';
import { getDeadlocks, getDeadlock, deadlockAccessDenied } from '../src/features/deadlocks/deadlockApi.ts';
import { parseDeadlockPage, parseDeadlockDetail } from '../src/features/deadlocks/deadlockParser.mjs';
const target = '11111111-1111-4111-8111-111111111111';
const window = { fromUtc: '2026-09-30T00:00:00.123456Z', toUtc: '2026-10-01T00:00:00.654321Z' };
const cutoff = '2026-09-30T23:00:00.0000000+00:00';
function item(index) {
  const fingerprint = index.toString(16).padStart(64, '0');
  const hash = createHash('sha256').update(Buffer.from(target.replaceAll('-', '') + fingerprint, 'hex')).digest('hex').slice(0, 32);
  const eventId = `${hash.slice(0, 8)}-${hash.slice(8, 12)}-${hash.slice(12, 16)}-${hash.slice(16, 20)}-${hash.slice(20)}`;
  return { eventId, occurredAtUtc: new Date(Date.parse('2026-09-30T12:00:00Z') - index * 60_000).toISOString(), fingerprint,
    participantCount: 0, relationCount: 0, parseTruncated: false, collectedAtUtc: '2026-09-30T22:00:00Z' };
}
function cursor(page, overrides = {}) {
  const tail = { ...page.items.at(-1), ...overrides };
  return Buffer.from(JSON.stringify([overrides.targetId ?? target, tail.occurredAtUtc, tail.eventId,
    overrides.cutoff ?? cutoff, overrides.fromUtc ?? window.fromUtc, overrides.toUtc ?? window.toUtc])).toString('base64');
}
function page(start = 1, count = 25, more = true) {
  const value = { targetId: target, repositoryTimeUtc: cutoff, items: Array.from({ length: count }, (_, i) => item(start + i)) };
  if (more) value.nextCursor = cursor(value);
  return value;
}
const receipt = value => ({ page: value, window });
const response = value => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' } });
test('standard native cursor binds exact DateTimeOffset precision and the retained bound', () => {
  const first = page(); validateDeadlockReceipt(first, target, window);
  assert.equal(decodeDeadlockCursor(first.nextCursor).eventId, first.items.at(-1).eventId);
  assert.equal(deadlockUtc(cutoff), deadlockUtc('2026-09-30T23:00:00Z'));
  validateDeadlockRequest(target, { ...window, fromUtc: window.fromUtc.replace('Z', '0+00:00') }, first.nextCursor);
  assert.equal(maximumDeadlockPages, 20); assert.equal(maximumDeadlockRows, 500);
});
test('continuation refuses changed cutoff/order/tail and repeated received identity', () => {
  const first = page(), second = page(26);
  validateDeadlockReceipt(second, target, window, [receipt(first)], first.nextCursor);
  for (const malformed of [
    { ...second, repositoryTimeUtc: '2026-09-30T23:00:00.000001Z' }, { ...second, items: [...second.items].reverse() },
    { ...second, items: [first.items.at(-1), ...second.items.slice(1)] },
    { ...second, nextCursor: cursor(second, { occurredAtUtc: second.items[0].occurredAtUtc }) },
    { ...second, nextCursor: cursor(second, { cutoff: '2026-09-30T23:01:00Z' }) },
  ]) assert.throws(() => validateDeadlockReceipt(malformed, target, window, [receipt(first)], first.nextCursor));
});
test('foreign or malformed cursor is refused before API dispatch', async () => {
  const first = page();
  for (const value of ['opaque-token', cursor(first, { targetId: '22222222-2222-4222-8222-222222222222' }), cursor(first, { fromUtc: '2026-09-30T01:00:00Z' })])
    assert.throws(() => validateDeadlockRequest(target, window, value));
  const original = globalThis.fetch; let reads = 0;
  try { globalThis.fetch = async () => { reads++; return response(first); };
    await assert.rejects(getDeadlocks(target, new AbortController().signal, 'opaque-token', window)); assert.equal(reads, 0);
  } finally { globalThis.fetch = original; }
});
test('window, collected cutoff, full page and finite retained cache reject incompatible evidence', () => {
  const valid = page();
  for (const value of [page(1, 26), page(1, 1),
    { ...valid, items: [{ ...valid.items[0], collectedAtUtc: '2026-10-01T00:01:00Z' }, ...valid.items.slice(1)] },
    { ...valid, items: [{ ...valid.items[0], occurredAtUtc: window.toUtc }, ...valid.items.slice(1)] }])
    assert.throws(() => validateDeadlockReceipt(value, target, window));
  assert.throws(() => validateDeadlockReceipt(page(501, 1, false), target, window, Array.from({ length: 20 }, (_, i) => receipt(page(1 + i * 25)))));
});
test('parser accepts native UTC offset seven-digit DTOs and refuses impossible calendar dates', () => {
  const source = page(1, 1, false);
  source.items[0].occurredAtUtc = '2026-09-30T11:59:00.1234560+00:00';
  assert.equal(parseDeadlockPage(source, target).items[0].occurredAtUtc, source.items[0].occurredAtUtc);
  assert.equal(parseDeadlockDetail({ summary: source.items[0], participants: [], relations: [] }, target, source.items[0].eventId).summary.eventId, source.items[0].eventId);
  assert.throws(() => parseDeadlockPage({ ...source, repositoryTimeUtc: '2026-02-30T12:00:00Z' }, target));
});
test('list and detail retain definitive401403 status without reflecting provider bodies', async () => {
  const original = globalThis.fetch;
  try { for (const status of [401, 403]) {
    globalThis.fetch = async () => new Response('PROVIDER_SECRET', { status });
    for (const operation of [() => getDeadlocks(target, new AbortController().signal, undefined, window), () => getDeadlock(target, item(1).eventId, new AbortController().signal)])
      await assert.rejects(operation(), error => deadlockAccessDenied(error) && error.status === status && !error.message.includes('PROVIDER_SECRET'));
  } } finally { globalThis.fetch = original; }
});
test('reader binds exact request limit/window and rejects malformed success safely', async () => {
  const original = globalThis.fetch;
  try {
    let observed; globalThis.fetch = async url => { observed = new URL(url, 'https://example.invalid'); return response(page()); };
    assert.equal((await getDeadlocks(target, new AbortController().signal, undefined, window)).items.length, 25);
    assert.equal(observed.searchParams.get('limit'), '25'); assert.equal(observed.searchParams.get('fromUtc'), window.fromUtc);
    globalThis.fetch = async () => response(page(1, 26)); await assert.rejects(getDeadlocks(target, new AbortController().signal, undefined, window));
    globalThis.fetch = async () => response({ ...page(), repositoryTimeUtc: 'PROVIDER_SECRET' });
    await assert.rejects(getDeadlocks(target, new AbortController().signal, undefined, window), error => !error.message.includes('PROVIDER_SECRET'));
  } finally { globalThis.fetch = original; }
});
