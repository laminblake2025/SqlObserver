import assert from 'node:assert/strict';
import test from 'node:test';
import { targetManagementIdentity, targetManagementUpdate } from '../src/features/targets/targetManagementModel.ts';

const id = 'ABCD1234-ABCD-4ABC-8DEF-ABCDEF123456';
test('management configuration preserves native text bounds and canonicalizes only identity', () => {
  const configuration = { expectedRevision: Number.MAX_SAFE_INTEGER, displayName: '<'.repeat(512),
    host: 'SQL.EXAMPLE.TEST', namedInstance: '9APP_$-', certificateHostName: 'CERT.EXAMPLE.TEST' };
  const parsed = targetManagementUpdate(id, configuration);
  assert.deepEqual(parsed, configuration); assert.notEqual(parsed, configuration);
  assert.equal(targetManagementIdentity(id, 1), id.toLowerCase());
  assert.throws(() => targetManagementIdentity(` ${id}`, 1));
  assert.throws(() => targetManagementIdentity(id, Number.MAX_SAFE_INTEGER + 1));
  assert.throws(() => targetManagementUpdate(id, { ...configuration, displayName: '<'.repeat(513) }));
});

test('native nullable inactive address fields normalize without inventing a port or certificate host', () => {
  assert.deepEqual(targetManagementUpdate(id, { expectedRevision: 1, displayName: 'SQL', host: 'sql',
    tcpPort: 65535, namedInstance: null, certificateHostName: null }),
    { expectedRevision: 1, displayName: 'SQL', host: 'sql', tcpPort: 65535 });
  assert.throws(() => targetManagementUpdate(id, { expectedRevision: 1, displayName: 'SQL', host: 'sql',
    tcpPort: null, namedInstance: null }));
  assert.throws(() => targetManagementUpdate(id, { expectedRevision: 1, displayName: 'SQL', host: 'sql',
    tcpPort: 1433, username: 'operator' }));
});
