import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { transformWithOxc } from 'vite';
import { backupFixture } from '../tools/backup-assessment-fixture.mjs';
import { parseBackupPage, assertBackupSnapshot, assertBackupPagePosition, backupCutoff } from '../src/features/operations/backupAssessment.ts';
import { getBackupAssessment, BackupAssessmentError } from '../src/features/operations/backupAssessmentApi.ts';

const target = '11111111-1111-4111-8111-111111111111', at = '2026-09-30T12:00:00.000001Z';
const fixture = (scenario = 'mixed', after = 0) => backupFixture(target, at, scenario, after);
const parse = value => parseBackupPage(value, target, at);
test('rewind preserves the exact URL cutoff while live ranges use the shared clock', () => {
  const now = Date.parse('2026-09-30T13:00:00Z');
  const scope = { target, range: 'custom', from: '2026-09-30T11:00:00Z', to: at, compare: false };
  assert.equal(backupCutoff(scope, now), at);
  assert.equal(backupCutoff({ ...scope, range: '1h' }, now), '2026-09-30T13:00:00.000Z');
  assert.throws(() => backupCutoff({ ...scope, to: '2026-09-30T13:00:00.000001Z' }, now), /future/);
  assert.throws(() => backupCutoff({ ...scope, to: '2026-09-30T12:00:00+00:00' }, now));
});
test('mixed backup assessments preserve policy absence, local preference, disabled state and revoked provenance', () => {
  const page = parse(fixture());
  assert.equal(page.items[0].full.status, 'within_threshold'); assert.equal(page.items[0].log.status, 'policy_violated');
  assert.equal(page.items[1].policy, null); assert.equal(page.items[1].full.reason, 'policy_not_configured');
  assert.equal(page.items[2].localReplicaPreferred, true); assert.equal(page.items[2].full.reason, 'other_replica_evidence_required');
  assert.equal(page.items[3].full.observedAgeSeconds, null); assert.equal(page.items[3].samples[0].qualificationTrusted, false);
  assert.ok(page.items[3].samples[0].recordedFinishUtc); assert.equal(page.items[4].full.status, 'disabled');
  assert.equal(page.items[5].full.reason, 'finish_time_unknown'); assert.equal(page.items[0].samples[0].sizeBytes, '9223372036854775807');
  const last = parse(fixture('mixed', 50)); assert.equal(last.items[0].databaseFingerprint, null);
  assert.equal(last.items[0].full.reason, 'database_identity_unknown'); assert.equal(last.hasMore, false);
});
for (const scenario of ['not_collected', 'failed', 'expired', 'stale_source']) test(`${scenario} is distinct from within-limit backup health`, () => {
  const page = parse(fixture(scenario));
  if (scenario === 'stale_source') assert.equal(page.items[0].full.reason, 'stale_observation');
  else { assert.equal(page.items.length, 0); assert.ok(page.action); }
});
test('false-green states, untrusted selection, rounded sizes and unexpected contracts fail closed', () => {
  const changed = mutate => { const value = structuredClone(fixture()); mutate(value); return value; };
  for (const mutate of [
    p => p.items[0].samples[0].sizeBytes = 9223372036854775807,
    p => p.items[0].samples[0].sizeBytes = '9223372036854775808',
    p => p.items[0].samples[0].qualificationTrusted = false,
    p => p.items[0].full.selectedBackup.isDamaged = true,
    p => p.items[0].full.maximumAgeSeconds = 30,
    p => p.items[0].scope = 'availability_group_local_only',
    p => p.items[0].policy.enabled = false,
    p => p.items[0].full.status = 'healthy',
    p => p.items[1].full.status = 'within_threshold',
    p => p.items[1].databaseOrdinal = 1,
    p => p.items[1].databaseFingerprint = p.items[0].databaseFingerprint,
    p => p.items[0].samples[0].sourceLocalFinish += 'Z',
    p => p.atUtc = '2026-09-30T12:00:00.000002Z',
    p => p.completedAtUtc = '2026-09-30T12:00:00.000002Z',
    p => p.items[0].unexpected = 'source text',
  ]) assert.throws(() => parse(changed(mutate)), /could not be verified/);
});
test('continuation binds the target, receipt, revision and exact microsecond cutoff', () => {
  const first = parse(fixture()), second = parse(fixture('mixed', 50));
  assert.doesNotThrow(() => assertBackupSnapshot(first, second));
  assert.doesNotThrow(() => assertBackupPagePosition(first, 0)); assert.doesNotThrow(() => assertBackupPagePosition(second, 1));
  assert.throws(() => assertBackupPagePosition(second, 0)); assert.throws(() => assertBackupPagePosition(first, 1));
  for (const change of [{ runId: target }, { targetRevision: 2 }, { atUtc: at.replace('000001', '000002') }, { databaseCount: 52 }])
    assert.throws(() => assertBackupSnapshot(first, { ...second, ...change }), /snapshot changed/);
});
test('API preserves explicit cutoff, cursor and same-origin credentials without caching', async () => {
  const original = globalThis.fetch, requests = [];
  try {
    globalThis.fetch = async (url, options) => {
      const request = new URL(url, 'https://local.test'); requests.push({ request, options });
      return new Response(JSON.stringify(fixture('mixed', request.searchParams.has('cursor') ? 50 : 0)));
    };
    const signal = new AbortController().signal, first = await getBackupAssessment(target, at, signal);
    await getBackupAssessment(target, at, signal, first.nextCursor);
    assert.equal(requests[0].request.pathname, `/api/v2/observation-targets/${target}/health/backups`);
    assert.equal(requests[1].request.searchParams.get('atUtc'), at); assert.equal(requests[1].request.searchParams.get('cursor'), 'backup-page-2');
    assert.equal(requests[1].options.credentials, 'same-origin'); assert.equal(requests[1].options.cache, 'no-store');
    assert.equal(requests[1].options.signal, signal);
  } finally { globalThis.fetch = original; }
});
test('API treats stale cursors, authorization, bounded streaming and cancellation independently', async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [400, 403, 409, 503, 504]) {
      globalThis.fetch = async () => new Response('secret body', { status });
      await assert.rejects(() => getBackupAssessment(target, at, new AbortController().signal, 'cursor'), e =>
        e instanceof BackupAssessmentError && !e.message.includes('secret') && e.restart === (status === 400 || status === 409) && e.retryable === (status !== 403));
    }
    globalThis.fetch = async () => new Response(new ReadableStream({ start(c) { c.enqueue(new Uint8Array(1048577)); c.close(); } }));
    await assert.rejects(() => getBackupAssessment(target, at, new AbortController().signal), /could not be verified/);
    const controller = new AbortController(); controller.abort();
    globalThis.fetch = async () => { throw new DOMException('cancelled', 'AbortError'); };
    await assert.rejects(() => getBackupAssessment(target, at, controller.signal), e => e.name === 'AbortError');
  } finally { globalThis.fetch = original; }
});
const source = await readFile(new URL('../src/features/operations/BackupAssessmentView.tsx', import.meta.url), 'utf8');
const require = createRequire(import.meta.url);
const javascript = (await transformWithOxc(source, 'BackupAssessmentView.tsx', { jsx: { runtime: 'automatic' } })).code
  .replaceAll('"react/jsx-runtime"', JSON.stringify(pathToFileURL(require.resolve('react/jsx-runtime')).href))
  .replaceAll('"./backupAssessment.ts"', JSON.stringify(new URL('../src/features/operations/backupAssessment.ts', import.meta.url).href));
const { BackupAssessmentView } = await import(`data:text/javascript;base64,${Buffer.from(javascript).toString('base64')}`);
const render = page => renderToStaticMarkup(createElement(BackupAssessmentView, { page, formatTime: value => value ? `UTC ${value}` : 'Unknown' }));
test('rendered checks show separate verdicts, configured limits, unknown ages and retained faults', () => {
  const html = render(parse(fixture()));
  for (const expected of ['Within limit', 'Policy violation', 'Unknown', 'Not configured', 'Disabled', 'backup damaged', 'qualification', 'local history only', '9223372036854775807'])
    assert.ok(html.includes(expected), expected);
  assert.match(html, /Age: Unknown/); assert.match(html, /Evidence for inventory row 4/);
  assert.match(html, /Raw start \/ finish \(SQL Server local\)/); assert.match(html, /Verify restore chains/);
});
test('rendered missing and retired snapshots explain the action without fabricated database rows', () => {
  for (const scenario of ['not_collected', 'failed', 'expired']) {
    const html = render(parse(fixture(scenario))); assert.match(html, /no current database verdicts/);
    assert.doesNotMatch(html, /class="backup-check evidence-current"/); assert.doesNotMatch(html, /<tbody>/);
  }
});
