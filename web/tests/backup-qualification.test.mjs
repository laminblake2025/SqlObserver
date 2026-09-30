import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { editQualificationDraft, emptyQualificationDraft, qualificationCommand, parseBackupQualification } from '../src/features/operations/backupQualification.ts';
import { getBackupQualification, setBackupQualification, BackupQualificationError } from '../src/features/operations/backupQualificationApi.ts';
const target = '11111111-1111-4111-8111-111111111111', operation = '22222222-2222-4222-8222-222222222222';
const current = { targetId: target, targetRevision: 2, qualificationRevision: 3, enabled: true, sourceZoneId: 'UTC',
  fromUtc: '2026-01-01T00:00:00.000001Z', toUtc: '2027-01-01T00:00:00.000001Z', evidenceSha256: 'a'.repeat(64), rulesSha256: 'b'.repeat(64) };
const draft = () => ({ ...emptyQualificationDraft(), action: 'qualify', sourceZoneId: 'UTC', fromUtc: current.fromUtc, toUtc: current.toUtc,
  serializedZoneRules: 'UTC;0;UTC;UTC;UTC;;', evidenceSha256: current.evidenceSha256, confirmed: true });
const command = () => qualificationCommand(draft(), current, operation);
const empty = { ...current, qualificationRevision: 0, enabled: false, sourceZoneId: null, fromUtc: null, toUtc: null, evidenceSha256: null, rulesSha256: null };
test('new qualification drafts require an explicit action, rules, window and fresh attestation', () => {
  assert.deepEqual(emptyQualificationDraft(), { action: '', sourceZoneId: '', fromUtc: '', toUtc: '', serializedZoneRules: '', evidenceSha256: '', confirmed: false, revokeConfirmed: false });
  const body = command(); assert.equal(body.expectedQualificationRevision, 3); assert.equal(body.expectedTargetRevision, 2);
  assert.equal(body.fromUtc, current.fromUtc); assert.equal(body.toUtc, current.toUtc); assert.equal(body.historicalConfigurationConfirmed, true);
});
test('microsecond UTC bounds, evidence digest, source ID and attestation are validated without inferring rules', () => {
  for (const change of [{ action: '' }, { fromUtc: '2026-01-01T00:00:00+00:00' }, { toUtc: current.fromUtc },
    { fromUtc: '2026-01-01T00:00:00.0000001Z' }, { confirmed: false }, { sourceZoneId: 'UTC\u0000' },
    { sourceZoneId: ' ' }, { evidenceSha256: 'A'.repeat(64) }, { serializedZoneRules: '' }, { serializedZoneRules: 'UTC\0' }])
    assert.throws(() => qualificationCommand({ ...draft(), ...change }, current, operation));
  assert.throws(() => qualificationCommand(draft(), current, '0'.repeat(36)));
  // Only the .NET backend can parse/validate serialized TimeZoneInfo rules.
  assert.equal(qualificationCommand({ ...draft(), serializedZoneRules: 'backend validates this artifact' }, current, operation).serializedZoneRules, 'backend validates this artifact');
});
test('UTF-8 rule and complete JSON envelope byte limits are both enforced', () => {
  assert.throws(() => qualificationCommand({ ...draft(), serializedZoneRules: 'é'.repeat(32769) }, current, operation), /UTF-8/);
  assert.throws(() => qualificationCommand({ ...draft(), serializedZoneRules: 'x'.repeat(65536) }, current, operation), /command exceed/);
  assert.throws(() => qualificationCommand({ ...draft(), serializedZoneRules: '\\'.repeat(32768) }, current, operation), /command exceed/);
});
test('editing attested fields clears confirmation and switching action clears revocation confirmation', () => {
  for (const field of ['sourceZoneId', 'fromUtc', 'toUtc', 'serializedZoneRules', 'evidenceSha256'])
    assert.equal(editQualificationDraft(draft(), field, draft()[field]).confirmed, false);
  assert.equal(editQualificationDraft({ ...draft(), revokeConfirmed: true }, 'action', 'revoke').revokeConfirmed, false);
  assert.equal(editQualificationDraft(emptyQualificationDraft(), 'confirmed', true).confirmed, true);
});
test('revocation requires an active qualification and separate confirmation, omitting all rule evidence', () => {
  const body = qualificationCommand({ ...draft(), action: 'revoke', revokeConfirmed: true }, current, operation);
  assert.equal(body.enabled, false); assert.equal(body.historicalConfigurationConfirmed, false);
  for (const key of ['sourceZoneId', 'fromUtc', 'toUtc', 'serializedZoneRules', 'evidenceSha256']) assert.equal(body[key], null);
  assert.throws(() => qualificationCommand({ ...draft(), action: 'revoke' }, current, operation), /Confirm revocation/);
  assert.throws(() => qualificationCommand({ ...draft(), action: 'revoke', revokeConfirmed: true }, empty, operation), /no active/);
});
test('current snapshots distinguish absent and revoked state while binding target/revision and closed evidence', () => {
  assert.deepEqual(parseBackupQualification(current, target, 2), current); assert.deepEqual(parseBackupQualification(empty, target, 2), empty);
  assert.equal(parseBackupQualification({ ...empty, qualificationRevision: 4 }, target, 2).qualificationRevision, 4);
  for (const change of [{ targetId: operation }, { targetRevision: 1 }, { qualificationRevision: 0 }, { rulesSha256: null },
    { fromUtc: current.toUtc }, { toUtc: '2027-01-01T00:00:00+00:00' }, { unexpected: 'rules' }])
    assert.throws(() => parseBackupQualification({ ...current, ...change }, target, 2));
  assert.throws(() => parseBackupQualification({ ...empty, sourceZoneId: 'UTC' }, target, 2));
});
test('GET uses exact revision, bounded uncached same-origin reads and cancellation', async () => {
  const original = globalThis.fetch;
  try {
    let sent; globalThis.fetch = async (url, options) => { sent = { url, options }; return new Response(JSON.stringify(current)); };
    const signal = new AbortController().signal; assert.deepEqual(await getBackupQualification(target, 2, signal), current);
    assert.ok(sent.url.endsWith('/backup-zone-qualification/?targetRevision=2')); assert.equal(sent.options.signal, signal);
    assert.equal(sent.options.credentials, 'same-origin'); assert.equal(sent.options.cache, 'no-store');
    globalThis.fetch = async () => new Response(' '.repeat(16385)); await assert.rejects(() => getBackupQualification(target, 2, signal), /could not be verified/);
    const aborted = new AbortController(); aborted.abort(); globalThis.fetch = async () => { throw new DOMException('aborted', 'AbortError'); };
    await assert.rejects(() => getBackupQualification(target, 2, aborted.signal), e => e.name === 'AbortError');
  } finally { globalThis.fetch = original; }
});
test('uncertain PUT retries the exact command and validates acknowledged target, revision and action', async () => {
  const original = globalThis.fetch, sent = [], body = command(), signal = new AbortController().signal;
  const ack = { targetId: target, targetRevision: 2, qualificationRevision: 4, enabled: true };
  try {
    globalThis.fetch = async (url, options) => { sent.push({ url, options }); if (sent.length === 1) throw new TypeError('lost result'); return new Response(JSON.stringify(ack)); };
    await assert.rejects(() => setBackupQualification(target, body, signal), e => e.retryable && /uncertain/.test(e.message));
    assert.equal(await setBackupQualification(target, body, signal), 4); assert.equal(sent[0].options.body, sent[1].options.body);
    assert.equal(sent[1].options.method, 'PUT'); assert.equal(sent[1].options.headers['Content-Type'], 'application/json');
    for (const change of [{ qualificationRevision: 3 }, { targetRevision: 3 }, { enabled: false }, { targetId: operation }, { unknown: true }]) {
      globalThis.fetch = async () => new Response(JSON.stringify({ ...ack, ...change }));
      await assert.rejects(() => setBackupQualification(target, body, signal), e => e.retryable && /could not be verified/.test(e.message));
    }
  } finally { globalThis.fetch = original; }
});
test('permission, revision, bad artifact and oversize failures have safe recovery instructions', async () => {
  const original = globalThis.fetch;
  try {
    for (const [status, expected, retryable] of [[403, /TargetAdministrator/, false], [409, /Reload/, false], [400, /serialized rules/, false],
      [413, /byte limit/, false], [404, /no longer available/, false], [429, /uncertain/, true], [503, /uncertain/, true]]) {
      globalThis.fetch = async () => new Response('secret server text', { status });
      await assert.rejects(() => setBackupQualification(target, command(), new AbortController().signal), e =>
        e instanceof BackupQualificationError && expected.test(e.message) && e.retryable === retryable && !e.message.includes('secret'));
    }
  } finally { globalThis.fetch = original; }
});
const source = await readFile(new URL('../src/features/operations/BackupQualificationDetails.tsx', import.meta.url), 'utf8');
const require = createRequire(import.meta.url);
const javascript = (await transformWithOxc(source, 'BackupQualificationDetails.tsx', { jsx: { runtime: 'automatic' } })).code
  .replaceAll('"react/jsx-runtime"', JSON.stringify(pathToFileURL(require.resolve('react/jsx-runtime')).href));
const { BackupQualificationDetails } = await import(`data:text/javascript;base64,${Buffer.from(javascript).toString('base64')}`);
test('rendered metadata preserves precise UTC window and provenance without treating qualification as freshness', () => {
  const render = value => renderToStaticMarkup(createElement(BackupQualificationDetails, { current: value }));
  const active = render(current); assert.match(active, /Active historical time qualification/);
  assert.ok(active.includes(current.fromUtc)); assert.ok(active.includes(current.rulesSha256)); assert.match(active, /does not prove backup restoreability/);
  assert.match(render(empty), /No historical time qualification/); assert.match(render({ ...empty, qualificationRevision: 4 }), /is revoked/);
  assert.doesNotMatch(render(empty), /<dl>/);
});
