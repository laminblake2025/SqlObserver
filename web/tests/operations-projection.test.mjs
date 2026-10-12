import test from 'node:test';
import assert from 'node:assert/strict';
import { parseOperationalProjectionCursor, validateOperationalProjectionPage } from '../src/features/operations/operationsProjectionModel.ts';

const target = '11111111-1111-4111-8111-111111111111';
const other = '44444444-4444-4444-8444-444444444444';
const run = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const observedAtUtc = '2026-10-03T12:00:00.123456Z';
const snapshot = '2026-10-03T12:00:00.1234560+00:00';
const file = fileId => ({ fileId, sizeBytes: 1024, usedBytes: 256, freeBytes: 768, state: 'Online' });
const tie = changes => ({ Kind: 'tempdb', RunId: run, Revision: 4, From: null, To: null, A: '50', B: '', C: '', D: '', ...changes });
const encode = (changes = {}, id = target, at = snapshot) => Buffer.from(`${id}|${at}|${Buffer.from(JSON.stringify(tie(changes))).toString('base64')}`).toString('base64');
const page = changes => ({ targetId: target, targetRevision: 4, runId: run, observedAtUtc,
  evidence: { observedAtUtc }, state: 'Complete', items: Array.from({ length: 50 }, (_, index) => file(index + 1)),
  hasMore: true, nextCursor: encode(), truncated: false, ...changes });

test('native encoded TempDB cursor binds equivalent seventh-zero and microsecond source times', () => {
  const received = page();
  validateOperationalProjectionPage(received, 'tempdb-files', target);
  assert.equal(parseOperationalProjectionCursor(received.nextCursor, 'tempdb-files', target, received).A, '50');
  assert.throws(() => parseOperationalProjectionCursor(encode({}, target, '2026-10-03T12:00:00.1234570+00:00'), 'tempdb-files', target, received));
});

test('continuation rejects a changed native run, revision or one-microsecond header without joining rows', () => {
  const first = page(), next = page({ items: [file(51)], hasMore: false, nextCursor: null });
  validateOperationalProjectionPage(next, 'tempdb-files', target, first, first.items.at(-1));
  for (const changes of [{ runId: other }, { targetRevision: 5 },
    { observedAtUtc: '2026-10-03T12:00:00.123457Z', evidence: { observedAtUtc: '2026-10-03T12:00:00.123457Z' } },
    { state: 'Partial' }, { truncated: true }])
    assert.throws(() => validateOperationalProjectionPage({ ...next, ...changes }, 'tempdb-files', target, first, first.items.at(-1)));
});

test('native keyset continuation refuses duplicates and out-of-order file identities', () => {
  const first = page();
  for (const items of [[file(50)], [file(52), file(51)], [file(51), file(51)]])
    assert.throws(() => validateOperationalProjectionPage(page({ items, hasMore: false, nextCursor: null }), 'tempdb-files', target, first, first.items.at(-1)));
});

test('native cursor guards foreign target, run, revision, kind, tail and noncanonical encoding', () => {
  for (const nextCursor of [encode({}, other), encode({ RunId: other }), encode({ Revision: 5 }),
    encode({ Kind: 'backups' }), encode({ A: '49' }), encode({ A: '050' }), 'page+/=two'])
    assert.throws(() => validateOperationalProjectionPage(page({ nextCursor }), 'tempdb-files', target));
});

test('repeated native continuation cursor refuses a loop without claiming the loaded page is fresh', () => {
  const received = page();
  assert.throws(() => validateOperationalProjectionPage(received, 'tempdb-files', target, undefined, undefined, [undefined, received.nextCursor]));
});

test('actual native first-page row bound refuses partial hasMore receipts and oversized loaded pages', () => {
  assert.throws(() => validateOperationalProjectionPage(page({ items: [file(50)] }), 'tempdb-files', target));
  assert.throws(() => validateOperationalProjectionPage(page({ items: Array.from({ length: 51 }, (_, i) => file(i + 1)), hasMore: false, nextCursor: null }), 'tempdb-files', target));
});

test('NoData permits the native revision-one repository-check fallback and forbids invented snapshot rows', () => {
  const empty = page({ targetRevision: 1, runId: null, evidence: null, state: 'NoData', items: [], hasMore: false, nextCursor: null });
  validateOperationalProjectionPage(empty, 'tempdb-files', target);
  for (const changes of [{ items: [file(1)] }, { runId: run }, { evidence: { observedAtUtc } }])
    assert.throws(() => validateOperationalProjectionPage({ ...empty, ...changes }, 'tempdb-files', target));
});

test('completed projection evidence cannot substitute a missing or different native header instant', () => {
  for (const evidence of [null, {}, { observedAtUtc: '2026-10-03T12:00:00.123457Z' }])
    assert.throws(() => validateOperationalProjectionPage(page({ evidence }), 'tempdb-files', target));
});

test('native TempDB summary accepts omitted-file continuation without manufacturing file rows', () => {
  validateOperationalProjectionPage(page({ items: [], totalBytes: 1024, usedBytes: 256 }), 'tempdb-summary', target);
  assert.throws(() => validateOperationalProjectionPage(page({ items: [file(1)] }), 'tempdb-summary', target));
});

test('backup ordering preserves genuine null finish/set identifiers and deterministic database/kind ties', () => {
  const backup = (fingerprint, kind = 'Full') => ({ databaseFingerprint: fingerprint.repeat(64), kind, lastFinishUtc: null, backupSetId: null });
  const received = page({ items: [backup('a'), backup('a', 'Differential'), backup('b')], hasMore: false, nextCursor: null });
  validateOperationalProjectionPage(received, 'backups', target);
  assert.throws(() => validateOperationalProjectionPage({ ...received, items: [backup('b'), backup('a')] }, 'backups', target));
  // A full native page may finish with genuinely unknown UTC/set identity.
  const paged = page({ items: Array.from({ length: 50 }, (_, index) => ({ ...backup('a'), databaseFingerprint: (index + 1).toString(16).padStart(64, '0') })) });
  paged.nextCursor = encode({ Kind: 'backups', A: '<null-utc>', B: '', C: paged.items.at(-1).databaseFingerprint, D: '1' });
  validateOperationalProjectionPage(paged, 'backups', target);
});

test('AG native cursor binds both fingerprint keys and continuation preserves source visibility', () => {
  const replica = value => ({ groupFingerprint: 'a'.repeat(64), replicaFingerprint: value.toString(16).padStart(64, '0'),
    role: 'PRIMARY', operationalState: 'ONLINE', connectedState: 'CONNECTED', visibilityScope: 'PrimaryAllKnown', stateAvailable: true });
  const received = page({ items: Array.from({ length: 50 }, (_, i) => replica(i + 1)), visibilityScope: 'PrimaryAllKnown', truncated: true,
    nextCursor: encode({ Kind: 'ag.replicas', A: 'a'.repeat(64), B: replica(50).replicaFingerprint }) });
  validateOperationalProjectionPage(received, 'ag-replicas', target);
  const next = { ...received, items: [{ ...replica(51), visibilityScope: 'SecondaryLocalOnly' }], visibilityScope: 'SecondaryLocalOnly', truncated: false, hasMore: false, nextCursor: null };
  validateOperationalProjectionPage(next, 'ag-replicas', target, received, received.items.at(-1));
  // Both native fields are page-relative; the stable run/revision/time anchor still applies.
  assert.throws(() => validateOperationalProjectionPage({ ...next, runId: other }, 'ag-replicas', target, received, received.items.at(-1)));
  assert.throws(() => validateOperationalProjectionPage({ ...received, nextCursor: encode({ Kind: 'ag.replicas', A: 'b'.repeat(64), B: replica(50).replicaFingerprint }) }, 'ag-replicas', target));
});
