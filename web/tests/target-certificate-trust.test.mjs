import assert from 'node:assert/strict';
import test from 'node:test';
import { parseObservationTarget } from '../src/features/targets/targetResponseParser.ts';
import { registrationRequest, registeredTarget } from '../component-tests/onboardingFixture.mjs';

test('target receipts preserve administrator-approved trust without relabeling validation', () => {
  const target = registeredTarget(registrationRequest());
  for (const encryptionMode of ['mandatory_validated', 'mandatory_trust_server_certificate']) {
    assert.equal(parseObservationTarget({ ...target, encryptionMode }).encryptionMode, encryptionMode);
  }
  for (const encryptionMode of ['optional', 'disabled', 'trust_all', null]) {
    assert.throws(() => parseObservationTarget({ ...target, encryptionMode }));
  }
});
