import test from 'node:test';
import assert from 'node:assert/strict';
import { fileHistoryWindow, fileHistoryUtc, fileHistoryBytes, fileHistoryCursor, parseFileReceiptPage, parseFileSnapshotPage, invalidFileHistory,
  validFileSnapshotSelection, fileSnapshotIdentity, fileHistoryContextIdentity } from '../src/features/resources/databaseFileWindowHistoryModel.ts';
import { getFileReceiptHistory, getFileSnapshotHistory } from '../src/features/resources/databaseFileWindowHistoryApi.ts';
import { resourceSelectionHref, readResourceSelection, retainResourceSelectionHref, preserveInvalidFileSnapshotHref } from '../src/features/resources/resourceSelectionModel.ts';
import { utcMicroseconds } from '../src/utcTimestamp.ts';
import { databaseFileWindowHistoryBackend, fileHistoryBinding, fileHistoryAlpha, fileHistoryBeta } from '../tools/database-file-window-history-fixture.mjs';

const binding = fileHistoryBinding, clone = value => structuredClone(value);
const list = (backend = databaseFileWindowHistoryBackend(), context = binding) => backend.read(context);
const selected = receipt => ({ runId: receipt.runId, sampleTargetRevision: receipt.sampleTargetRevision });
const exact = (backend = databaseFileWindowHistoryBackend(), context = binding) => {
  const receipt = backend.read(context).items[0];
  return { page: backend.snapshot(context, selected(receipt)), selection: selected(receipt) };
};
const parse = (value, selection, context = binding, prior = [], cursor) => parseFileSnapshotPage(value, context, selection, prior, cursor);
const scope = { target: fileHistoryAlpha, range: 'custom', compare: false, from: binding.fromUtc, to: binding.toUtc };

test('shared seven-day windows preserve exact microsecond UTC and reject empty, reversed and invalid civil dates', () => {
  assert.deepEqual(fileHistoryWindow(binding.fromUtc, binding.toUtc), { fromUtc: binding.fromUtc, toUtc: binding.toUtc });
  assert.deepEqual(fileHistoryWindow('2026-10-01T00:00:00.000001Z', '2026-10-08T00:00:00.000001Z'),
    { fromUtc: '2026-10-01T00:00:00.000001Z', toUtc: '2026-10-08T00:00:00.000001Z' });
  for (const pair of [[binding.fromUtc, binding.fromUtc], [binding.toUtc, binding.fromUtc], ['2026-10-01T00:00:00Z', '2026-10-08T00:00:00.000001Z'],
    ['2026-02-30T00:00:00Z', binding.toUtc]]) assert.throws(() => fileHistoryWindow(...pair));
  assert.equal(fileHistoryUtc('2026-10-02T10:00:00.1234560+00:00'), '2026-10-02T10:00:00.123456Z');
  for (const value of ['2026-10-02T10:00:00.1234561Z', '2026-10-02T10:00:00.12345600Z', '2026-10-02T10:00:00+01:00']) assert.throws(() => fileHistoryUtc(value));
});
test('current target revision fences older sample revisions without merging runs or selecting latest elsewhere', () => {
  const context = { ...binding, targetRevision: 5 }, { page, selection } = exact(databaseFileWindowHistoryBackend({ sampleTargetRevision: 2 }), context);
  assert.equal(parse(page, selection, context).receipt.sampleTargetRevision, 2);
  for (const patch of [{ targetId: fileHistoryBeta }, { targetRevision: 6 }, { sampleTargetRevision: 3 }, { runId: fileHistoryBeta },
    { fromUtc: '2026-10-02T10:00:00.000002Z' }, { toUtc: '2026-10-02T11:00:00.000002Z' }]) assert.throws(() => parse({ ...page, ...patch }, selection, context));
  const bad = clone(page); bad.receipt.sampleTargetRevision = 6; assert.throws(() => parse(bad, selection, context));
  assert.equal(validFileSnapshotSelection({ runId: fileHistoryAlpha, sampleTargetRevision: 0 }), false);
  assert.equal(fileSnapshotIdentity(null), 'invalid'); assert.equal(fileSnapshotIdentity(undefined), 'default');
});
test('exact Int64 strings survive allocation and cumulative I/O without floating point or invented maximum', () => {
  const { page, selection } = exact(), parsed = parse(page, selection), file = parsed.items[0];
  assert.equal(file.sizeBytes, '9223372036854775807'); assert.equal(file.readCount, '9007199254740993');
  assert.equal(file.bytesRead, '9223372036854775807'); assert.equal(parsed.items[1].maximumSizeBytes, null);
  for (const value of [1, '-1', '+1', '01', '1.0', '9223372036854775808']) assert.throws(() => fileHistoryBytes(value));
  for (const field of ['sizeBytes', 'growthBytes', 'readCount', 'writeCount', 'bytesRead', 'bytesWritten', 'ioStallMilliseconds', 'readStallMilliseconds']) {
    const bad = clone(page); bad.items[0][field] = '9223372036854775808'; assert.throws(() => parse(bad, selection));
  }
});
test('native file names are inert bounded valid Unicode including control and whitespace content', () => {
  const { page, selection } = exact();
  for (const name of ['<img src=x onerror=alert(1)>', ' \t\n ', '界'.repeat(341) + 'a', '🙂'.repeat(256), 'a'.repeat(1024)]) {
    const changed = clone(page); changed.items[0].logicalName = name; assert.equal(parse(changed, selection).items[0].logicalName, name);
  }
  for (const name of ['', '界'.repeat(342), 'a'.repeat(1025), '\ud800']) {
    const bad = clone(page); bad.items[0].logicalName = name; assert.throws(() => parse(bad, selection));
  }
});
test('growth modes and directional stalls remain exact retained configuration rather than growth events', () => {
  const { page, selection } = exact(); assert.deepEqual(parse(page, selection).items.map(file => file.growthMode), ['fixed_bytes', 'percent', 'disabled']);
  const ambiguous = clone(page); Object.assign(ambiguous.items[0], { growthPercent: 10, growthMode: 'ambiguous' }); assert.equal(parse(ambiguous, selection).items[0].growthMode, 'ambiguous');
  for (const patch of [{ growthMode: 'percent' }, { maximumSizeBytes: '1' }, { growthPercent: 101 }, { fileType: 'full_text' }, { fileState: 'healthy' },
    { readStallMilliseconds: null }, { writeStallMilliseconds: '101' }]) { const bad = clone(page); Object.assign(bad.items[0], patch); assert.throws(() => parse(bad, selection)); }
  const unknown = clone(page); Object.assign(unknown.items[0], { readStallMilliseconds: null, writeStallMilliseconds: null }); assert.equal(parse(unknown, selection).items[0].readStallMilliseconds, null);
});
test('newest failed and missing attempts stay selected before older complete snapshots', () => {
  for (const mode of ['failedLatest', 'missingLatest']) {
    const backend = databaseFileWindowHistoryBackend({ [mode]: true }), receipts = parseFileReceiptPage(backend.read(binding), binding), selection = selected(receipts.items[0]);
    assert.equal(receipts.items[0].state, 'unavailable'); assert.equal(receipts.items[1].state, 'available');
    const page = parse(backend.snapshot(binding, selection), selection); assert.equal(page.receipt.runId, selection.runId); assert.deepEqual(page.items, []);
    assert.equal(page.reason, mode === 'failedLatest' ? 'collection_failed' : 'payload_missing');
  }
});
test('every native attempt outcome remains explicit and never becomes a complete zero-filled snapshot', () => {
  const { page, selection } = exact();
  for (const outcome of ['timed_out', 'transient_failure', 'permanent_failure', 'permission_denied', 'unsupported', 'output_invalid', 'lease_lost', 'circuit_open']) {
    const failed = clone(page); Object.assign(failed, { state: 'unavailable', reason: 'collection_failed', items: [] });
    Object.assign(failed.receipt, { outcome, expectedFileCount: 0, storedFileCount: 0, firstObservedAtUtc: null, lastObservedAtUtc: null,
      firstCollectedAtUtc: null, lastCollectedAtUtc: null, state: failed.state, reason: failed.reason });
    assert.equal(parse(failed, selection).receipt.outcome, outcome);
  }
  const partial = exact(databaseFileWindowHistoryBackend({ partial: true })); assert.equal(parse(partial.page, partial.selection).receipt.outcome, 'partial');
});
test('complete empty succeeds with source receipt while absent selected runs stay unavailable without replacement', () => {
  const empty = exact(databaseFileWindowHistoryBackend({ emptyLatest: true })); assert.equal(parse(empty.page, empty.selection).reason, 'observed_empty');
  assert.equal(parse(empty.page, empty.selection).state, 'available'); assert.equal(empty.page.receipt.expectedFileCount, 0);
  const backend = databaseFileWindowHistoryBackend({ snapshotAbsent: true }), absent = exact(backend); assert.equal(parse(absent.page, absent.selection).receipt, null);
  assert.equal(parse(absent.page, absent.selection).reason, 'snapshot_not_retained');
  assert.throws(() => parse({ ...absent.page, items: empty.page.items.concat(exact().page.items[0]) }, absent.selection));
  const source = databaseFileWindowHistoryBackend(), pin = selected(source.read(binding).items[0]), outsideBinding = { ...binding, fromUtc: '2026-10-03T10:00:00Z', toUtc: '2026-10-03T11:00:00Z' };
  assert.equal(parse(source.snapshot(outsideBinding, pin), pin, outsideBinding).reason, 'snapshot_outside_window');
});
test('isolated exact source can be complete when whole-window cadence has an explicit comparison gap', () => {
  const backend = databaseFileWindowHistoryBackend(), receipts = clone(backend.read(binding)), pin = selected(receipts.items[0]);
  Object.assign(receipts.items[0], { state: 'partial', reason: 'collection_gap' }); Object.assign(receipts, { state: 'partial', reason: 'visibility_gaps' });
  Object.assign(receipts.coverage, { completeRunCount: 1, gapRunCount: 1, hasVisibilityGap: true });
  assert.equal(parseFileReceiptPage(receipts, binding).items[0].reason, 'collection_gap');
  assert.equal(parse(backend.snapshot(binding, pin), pin).reason, 'observed_files');
});
test('source observation clocks remain original and skew is disclosed independently of completion axis', () => {
  const { page, selection } = exact(), skewed = clone(page), actual = '2026-10-02T11:01:00Z';
  Object.assign(skewed, { state: 'partial', reason: 'source_clock_skew' }); Object.assign(skewed.receipt,
    { state: skewed.state, reason: skewed.reason, firstObservedAtUtc: actual, lastObservedAtUtc: actual });
  for (const file of skewed.items) file.observedAtUtc = actual;
  assert.equal(utcMicroseconds(parse(skewed, selection).items[0].observedAtUtc), utcMicroseconds(actual));
  const bad = clone(skewed); Object.assign(bad, { state: 'available', reason: 'observed_files' }); Object.assign(bad.receipt, { state: bad.state, reason: bad.reason }); assert.throws(() => parse(bad, selection));
  const late = clone(page); late.items[0].collectedAtUtc = actual; assert.throws(() => parse(late, selection));
});
test('receipt completion ordering is exact and half-open with UUID ties; observation clocks do not reorder it', () => {
  const page = list(); assert.ok(parseFileReceiptPage(page, binding));
  for (const items of [page.items.toReversed(), [page.items[0], page.items[0]]]) assert.throws(() => parseFileReceiptPage({ ...page, items }, binding));
  for (const completedAtUtc of [binding.toUtc, '2026-10-02T10:00:00Z']) { const bad = clone(page); bad.items[0].completedAtUtc = completedAtUtc; assert.throws(() => parseFileReceiptPage(bad, binding)); }
  const tied = clone(page); tied.items[1].completedAtUtc = tied.items[0].completedAtUtc; tied.items.sort((a, b) => b.runId.localeCompare(a.runId)); assert.ok(parseFileReceiptPage(tied, binding));
  assert.throws(() => parseFileReceiptPage({ ...tied, items: tied.items.toReversed() }, binding));
});
test('receipt continuations bind complete source manifest cutoff, coverage, order, target and epoch', () => {
  const backend = databaseFileWindowHistoryBackend({ receiptCount: 51 }), first = parseFileReceiptPage(backend.read(binding), binding), next = backend.read(binding, first.nextCursor);
  assert.equal(first.items.length, 50); assert.equal(parseFileReceiptPage(next, binding, [first], first.nextCursor).items.length, 1);
  for (const patch of [{ readEpoch: 'b'.repeat(64) }, { cutoffUtc: '2026-10-02T11:00:00Z' }, { targetId: fileHistoryBeta }, { nextCursor: first.nextCursor },
    { items: [first.items[0]] }, { coverage: { ...next.coverage, retentionDays: 30 } }]) assert.throws(() => parseFileReceiptPage({ ...next, ...patch }, binding, [first], first.nextCursor));
  assert.throws(() => parseFileReceiptPage({ ...next, items: [] }, binding, [first], first.nextCursor));
  backend.settings.epoch++; assert.throws(() => backend.read(binding, first.nextCursor), error => error.status === 409);
});
test('exact file continuations bind isolated receipt and strict database/file ordering without complete-window coverage', () => {
  const backend = databaseFileWindowHistoryBackend({ fileCount: 101 }), pin = selected(backend.read(binding).items[0]);
  const first = parse(backend.snapshot(binding, pin), pin), next = backend.snapshot(binding, pin, first.nextCursor);
  assert.equal(first.items.length, 100); assert.equal(parse(next, pin, binding, [first], first.nextCursor).items.length, 1); assert.equal(Object.hasOwn(first, 'coverage'), false);
  for (const patch of [{ readEpoch: 'b'.repeat(64) }, { cutoffUtc: '2026-10-02T11:00:00Z' }, { nextCursor: first.nextCursor }, { items: [first.items[0]] },
    { receipt: { ...next.receipt, expectedFileCount: 102 } }, { sampleTargetRevision: 2 }]) assert.throws(() => parse({ ...next, ...patch }, pin, binding, [first], first.nextCursor));
  assert.throws(() => parse({ ...next, items: [] }, pin, binding, [first], first.nextCursor));
});
test('terminal pages cannot silently omit receipts/files and unread pages must be full native limits', () => {
  const receipts = list(); assert.throws(() => parseFileReceiptPage({ ...receipts, items: receipts.items.slice(1) }, binding));
  assert.throws(() => parseFileReceiptPage({ ...receipts, nextCursor: btoa('synthetic-protected-more') }, binding));
  const { page, selection } = exact(); assert.throws(() => parse({ ...page, items: page.items.slice(1) }, selection));
  assert.throws(() => parse({ ...page, nextCursor: btoa('synthetic-protected-more') }, selection));
  const duplicate = clone(page); duplicate.items[1] = clone(duplicate.items[0]); assert.throws(() => parse(duplicate, selection));
  assert.throws(() => parse({ ...page, items: page.items.toReversed() }, selection));
});
test('closed producer vocabulary, null source clocks and accounting cannot invent valid complete evidence', () => {
  const receipts = list(); for (const patch of [{ state: 'healthy' }, { reason: 'invented' }, { readEpoch: 'g'.repeat(64) }, { secret: 'path' },
    { coverage: { ...receipts.coverage, retainedFileCount: 250001 } }, { coverage: { ...receipts.coverage, gapRunCount: 1 } }]) assert.throws(() => parseFileReceiptPage({ ...receipts, ...patch }, binding));
  const { page, selection } = exact(); for (const patch of [{ outcome: 'ok' }, { lossKind: 'invented' }, { reason: 'fiction' }, { firstObservedAtUtc: null },
    { minimumLostItems: 2147483648 }, { expectedFileCount: 10001 }, { state: 'available', reason: 'collection_loss' }]) { const bad = clone(page); Object.assign(bad.receipt, patch); assert.throws(() => parse(bad, selection), { message: invalidFileHistory }); }
});
test('terminal whole-window accounting checks actual retained rows and overlapping failure, loss and missing evidence', () => {
  const page = list(databaseFileWindowHistoryBackend({ failedLatest: true }));
  Object.assign(page.items[0], { expectedFileCount: 3, lossKind: 'output_validation_failure', minimumLostItems: 3 });
  Object.assign(page.coverage, { lostRunCount: 1, missingPayloadRunCount: 1 });
  assert.equal(parseFileReceiptPage(page, binding).coverage.runCount, 2);
  for (const patch of [{ failedRunCount: 0 }, { lostRunCount: 0 }, { missingPayloadRunCount: 0 }, { emptyRunCount: 1 }, { retainedFileCount: 2 }, { retainedFileCount: 4 }])
    assert.throws(() => parseFileReceiptPage({ ...page, coverage: { ...page.coverage, ...patch } }, binding));
  const boundary = list(); Object.assign(boundary, { state: 'partial', reason: 'revision_boundary' }); boundary.coverage.hasVisibilityGap = true;
  assert.equal(parseFileReceiptPage(boundary, binding).coverage.gapRunCount, 0);
});
test('history-specific cursor accepts canonical 4096 bytes and rejects malformed, oversized and repeated native forms', () => {
  assert.equal(fileHistoryCursor(Buffer.alloc(3072, 7).toString('base64')), true);
  assert.equal(fileHistoryCursor(Buffer.alloc(3073, 7).toString('base64')), false);
  for (const value of ['', 'YQ=', 'YR==', 'a\nAAA', 4]) assert.equal(fileHistoryCursor(value), false);
  const same = { ...binding, fromUtc: '2026-10-02T10:00:00.0000010+00:00' };
  assert.equal(fileHistoryContextIdentity(binding), fileHistoryContextIdentity(same));
});
test('exact snapshot pins survive compatible time edits while a new target or explicit clear drops them', () => {
  const pin = selected(list().items[0]), href = resourceSelectionHref(scope, { fileSnapshot: pin, file: { databaseId: 5, fileId: 1 } });
  assert.deepEqual(readResourceSelection(href, fileHistoryAlpha).fileSnapshot, pin);
  const changed = resourceSelectionHref({ ...scope, from: '2026-10-01T10:00:00Z', to: '2026-10-01T11:00:00Z' }, {});
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(changed, href), fileHistoryAlpha).fileSnapshot, pin);
  const other = resourceSelectionHref({ ...scope, target: fileHistoryBeta }, {});
  assert.equal(readResourceSelection(retainResourceSelectionHref(other, href), fileHistoryBeta).fileSnapshot, undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(scope, {}), fileHistoryAlpha).fileSnapshot, undefined);
  assert.deepEqual(readResourceSelection(href, fileHistoryBeta), {});
});
test('malformed or duplicate explicit pin remains invalid through same-target context edits until cleared', () => {
  const base = resourceSelectionHref(scope, {}), invalid = base + '&fileSnapshotRun=bad&fileSnapshotRevision=01';
  assert.equal(readResourceSelection(invalid, fileHistoryAlpha).invalidFileSnapshot, true);
  const changed = resourceSelectionHref({ ...scope, from: '2026-10-01T10:00:00Z', to: '2026-10-01T11:00:00Z' }, {});
  for (const href of [preserveInvalidFileSnapshotHref(changed, invalid), retainResourceSelectionHref(changed, invalid)]) assert.equal(readResourceSelection(href, fileHistoryAlpha).invalidFileSnapshot, true);
  const pin = selected(list().items[0]), duplicate = resourceSelectionHref(scope, { fileSnapshot: pin }) + '&fileSnapshotRevision=1';
  assert.equal(readResourceSelection(duplicate, fileHistoryAlpha).invalidFileSnapshot, true);
  assert.equal(readResourceSelection(resourceSelectionHref(scope, { fileSnapshot: pin }), fileHistoryAlpha).invalidFileSnapshot, undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(scope, {}), fileHistoryAlpha).invalidFileSnapshot, undefined);
});
test('typed no-store transport sends current fence and independent exact source identity without native epoch injection', async () => {
  const backend = databaseFileWindowHistoryBackend(), pin = selected(backend.read(binding).items[0]), previous = globalThis.fetch, calls = [];
  globalThis.fetch = async (url, init) => { calls.push({ url: String(url), init }); const parsed = new URL(String(url), 'http://fixture');
    return new Response(JSON.stringify(parsed.pathname.endsWith(pin.runId) ? backend.snapshot(binding, pin) : backend.read(binding)), { headers: { 'content-type': 'application/json' } }); };
  try {
    await getFileReceiptHistory(binding, new AbortController().signal); await getFileSnapshotHistory(binding, pin, new AbortController().signal);
    assert.equal(calls.length, 2); for (const call of calls) { const url = new URL(call.url, 'http://fixture'); assert.equal(call.init.cache, 'no-store'); assert.equal(url.searchParams.get('targetRevision'), '1'); assert.equal(url.searchParams.has('cutoffUtc'), false); assert.equal(url.searchParams.has('readEpoch'), false); }
    assert.equal(new URL(calls[0].url, 'http://fixture').searchParams.get('limit'), '50'); const exactUrl = new URL(calls[1].url, 'http://fixture');
    assert.ok(exactUrl.pathname.endsWith('/' + pin.runId)); assert.equal(exactUrl.searchParams.get('sampleTargetRevision'), '1'); assert.equal(exactUrl.searchParams.get('limit'), '100');
  } finally { globalThis.fetch = previous; }
});
test('transport fails closed on wrong content type, mismatched receipt and declared oversize bytes', async () => {
  const previous = globalThis.fetch, { page, selection } = exact();
  try {
    globalThis.fetch = async () => new Response(JSON.stringify(page), { headers: { 'content-type': 'text/plain' } });
    await assert.rejects(() => getFileSnapshotHistory(binding, selection, new AbortController().signal));
    globalThis.fetch = async () => new Response(JSON.stringify({ ...page, runId: fileHistoryBeta }), { headers: { 'content-type': 'application/json' } });
    await assert.rejects(() => getFileSnapshotHistory(binding, selection, new AbortController().signal));
    globalThis.fetch = async () => new Response('{}', { headers: { 'content-type': 'application/json', 'content-length': String(512 * 1024 + 1) } });
    await assert.rejects(() => getFileSnapshotHistory(binding, selection, new AbortController().signal));
  } finally { globalThis.fetch = previous; }
});
test('caller cancellation remains cancellation and cannot manufacture an empty source response', async () => {
  const previous = globalThis.fetch, cancellation = new AbortController(); let calls = 0;
  globalThis.fetch = async () => { calls++; return new Response(JSON.stringify(list()), { headers: { 'content-type': 'application/json' } }); };
  cancellation.abort();
  try { await assert.rejects(() => getFileReceiptHistory(binding, cancellation.signal), error => error.name === 'AbortError'); assert.equal(calls, 0); }
  finally { globalThis.fetch = previous; }
});
for (const status of [401, 403, 404, 409, 422, 503]) test('typed HTTP ' + status + ' preserves ownership refusal without leaking provider details', async () => {
  const previous = globalThis.fetch; globalThis.fetch = async () => new Response('{"detail":"PRIVATE_PROVIDER_ERROR"}', { status });
  try { await assert.rejects(() => getFileReceiptHistory(binding, new AbortController().signal), error => error.status === status && !error.message.includes('PRIVATE')); }
  finally { globalThis.fetch = previous; }
});
