import test from 'node:test';
import assert from 'node:assert/strict';
import { policyDraft, policyCommand, parseCurrentBackupPolicy } from '../src/features/operations/backupPolicy.ts';
import { getCurrentBackupPolicy, saveBackupPolicy, BackupPolicyError } from '../src/features/operations/backupPolicyApi.ts';
import { canManageBackupPolicies } from '../src/features/targets/meApi.ts';
const target = '11111111-1111-4111-8111-111111111111', hash = 'a'.repeat(64), operation = '22222222-2222-4222-8222-222222222222';
const policy = { targetId: target, targetRevision: 3, databaseFingerprint: hash, policyRevision: 4, enabled: true,
  maximumFullAgeSeconds: 86400, maximumLogAgeSeconds: 900, maximumObservationAgeSeconds: 300, fullRequirement: 'full_image', requireChecksums: false };
const draft = () => policyDraft(policy);
const command = () => policyCommand(draft(), 3, 4, operation, 'full');
test('new policies have no suggested values and existing settings round-trip explicitly', () => {
  assert.ok(Object.values(policyDraft(null)).every(value => value === ''));
  assert.deepEqual(command(), { expectedTargetRevision: 3, expectedPolicyRevision: 4, operationId: operation, enabled: true,
    maximumFullAgeSeconds: 86400, maximumLogAgeSeconds: 900, maximumObservationAgeSeconds: 300, fullRequirement: 'full_image', requireChecksums: false });
});
test('limits, explicit choices, revisions and recovery-model log requirements are validated', () => {
  for (const change of [{ fullSeconds: '' }, { fullSeconds: '2592001' }, { fullSeconds: '1.5' }, { observationSeconds: '3601' },
    { logSeconds: '' }, { enabled: '' }, { fullRequirement: '' }, { checksums: '' }])
    assert.throws(() => policyCommand({ ...draft(), ...change }, 3, 4, operation, 'full'));
  assert.equal(policyCommand({ ...draft(), logSeconds: '' }, 3, 4, operation, 'simple').maximumLogAgeSeconds, null);
  assert.equal(policyCommand({ ...draft(), enabled: 'false', logSeconds: '' }, 3, 4, operation, 'full').enabled, false);
  assert.throws(() => policyCommand(draft(), 0, 4, operation, 'full'));
  assert.throws(() => policyCommand(draft(), 3, 4, '0'.repeat(36), 'full'));
});
test('current policy reads bind exact target, revision and database without accepting unknown contract fields', () => {
  assert.deepEqual(parseCurrentBackupPolicy(policy, target, 3, hash), policy);
  for (const change of [{ targetRevision: 2 }, { targetId: operation }, { databaseFingerprint: 'b'.repeat(64) }, { policyRevision: 0 },
    { maximumFullAgeSeconds: 0 }, { enabled: 'true' }, { fullRequirement: 'default' }, { unknown: true }])
    assert.throws(() => parseCurrentBackupPolicy({ ...policy, ...change }, target, 3, hash));
});
test('policy actions require an active TargetAdministrator grant on the exact target', () => {
  const access = { active: true, targetId: target, grantedRoles: ['Viewer', 'TargetAdministrator'], allTargetRoles: [], targetRoles: ['Viewer'] };
  assert.equal(canManageBackupPolicies(access, target), false);
  assert.equal(canManageBackupPolicies({ ...access, targetRoles: ['TargetAdministrator'] }, target), true);
  assert.equal(canManageBackupPolicies({ ...access, targetRoles: ['TargetAdministrator'], targetId: operation }, target), false);
  assert.equal(canManageBackupPolicies({ ...access, active: false, targetRoles: ['TargetAdministrator'] }, target), false);
});
test('a missing current policy is explicit and reads use bounded same-origin uncached requests', async () => {
  const original = globalThis.fetch; const requests = [];
  try {
    globalThis.fetch = async (url, options) => { requests.push({ url, options }); return new Response(null, { status: 404 }); };
    assert.equal(await getCurrentBackupPolicy(target, 3, hash, new AbortController().signal), null);
    assert.ok(requests[0].url.endsWith('/policy/?targetRevision=3'));
    assert.equal(requests[0].options.credentials, 'same-origin'); assert.equal(requests[0].options.cache, 'no-store');
    globalThis.fetch = async () => new Response(' '.repeat(16385));
    await assert.rejects(() => getCurrentBackupPolicy(target, 3, hash, new AbortController().signal), /could not be verified/);
  } finally { globalThis.fetch = original; }
});
test('uncertain saves can retry the identical operation and verify exact returned settings', async () => {
  const original = globalThis.fetch; const requests = []; const body = command();
  try {
    globalThis.fetch = async (url, options) => { requests.push({ url, options }); if (requests.length === 1) throw new TypeError('connection closed');
      return new Response(JSON.stringify({ ...policy, policyRevision: 5 })); };
    const signal = new AbortController().signal;
    await assert.rejects(() => saveBackupPolicy(target, hash, body, signal), e => e instanceof BackupPolicyError && e.retryable);
    assert.equal((await saveBackupPolicy(target, hash, body, signal)).policyRevision, 5);
    assert.equal(requests[0].options.body, requests[1].options.body);
    assert.equal(requests[1].options.method, 'PUT'); assert.equal(requests[1].options.headers['Content-Type'], 'application/json');
    for (const change of [{ policyRevision: 4 }, { maximumFullAgeSeconds: 7200 }, { targetRevision: 4 }]) {
      globalThis.fetch = async () => new Response(JSON.stringify({ ...policy, policyRevision: 5, ...change }));
      await assert.rejects(() => saveBackupPolicy(target, hash, body, signal), e => e.retryable && /could not be verified/.test(e.message));
    }
  } finally { globalThis.fetch = original; }
});
test('conflicts, missing inventory, authorization and throttling give safe field-specific actions', async () => {
  const original = globalThis.fetch;
  try {
    for (const [status, code, expected, retryable] of [[409, 'backup_policy_conflict', /Reload/, false],
      [409, 'backup_inventory_unavailable', /Collect recent/, false], [400, 'log_threshold_required', /explicit log/, false],
      [403, '', /TargetAdministrator/, false], [404, '', /no longer available/, false], [429, '', /busy/, true], [503, '', /uncertain/, true]]) {
      globalThis.fetch = async () => new Response(JSON.stringify({ code, message: 'secret response' }), { status });
      await assert.rejects(() => saveBackupPolicy(target, hash, command(), new AbortController().signal), e =>
        e instanceof BackupPolicyError && expected.test(e.message) && e.retryable === retryable && !e.message.includes('secret'));
    }
  } finally { globalThis.fetch = original; }
});
