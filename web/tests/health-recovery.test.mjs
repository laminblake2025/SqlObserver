import assert from 'node:assert/strict';
import { test } from 'node:test';
import { healthRecoveryAdvice } from '../src/features/health/healthRecoveryModel.ts';

const source = { reason: 'none', state: 'current', circuitState: 'closed' };
test('healthy current evidence does not add recovery instructions', () => {
  assert.equal(healthRecoveryAdvice(source), undefined);
});
test('permission gaps identify collector identity and the permission-script destination', () => {
  const advice = healthRecoveryAdvice({ ...source, state: 'unavailable', reason: 'permission_denied' });
  assert.equal(advice.permissions, true);
  assert.match(advice.message, /collector Windows identity.*required read permissions/);
});
test('every native non-permission reason gives specific recovery without a permission change', () => {
  const expected = {
    capability_profile_missing: /capability discovery.*current discovery profile/,
    capability_profile_stale: /capability profile can be refreshed/,
    capability_missing: /required capability.*discovered profile/,
    version_unsupported: /SQL Server version.*supported versions/,
    platform_unsupported: /platform.*supported platforms/,
    edition_unsupported: /edition supports the capability/,
    never_collected: /first successful observation/,
    timed_out: /connectivity, response time and collector load/,
    collection_failed: /collector service diagnostics/,
    output_invalid: /output validation diagnostics/,
    sample_loss: /incomplete until a loss-free collection succeeds/,
    circuit_open: /probe for recovery.*cannot reset/,
    evidence_stale: /fresh successful evidence/,
  };
  for (const [reason, match] of Object.entries(expected)) {
    const advice = healthRecoveryAdvice({ ...source, state: 'unavailable', reason });
    assert.match(advice.message, match); assert.equal(advice.permissions, false);
  }
});
test('disabled, unexplained unavailable and open-circuit states retain honest recovery paths', () => {
  assert.match(healthRecoveryAdvice({ ...source, state: 'disabled' }).message, /administrator.*whether this collector should be enabled/);
  assert.match(healthRecoveryAdvice({ ...source, state: 'unavailable' }).message, /last collection outcome and the next due time/);
  assert.match(healthRecoveryAdvice({ ...source, circuitState: 'open' }).message, /cannot reset the circuit/);
});
