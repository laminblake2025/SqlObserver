export const alpha = '11111111-1111-4111-8111-111111111111';
export const beta = '22222222-2222-4222-8222-222222222222';
export const nextCursor = 'targets+/=next';
export const at = '2026-09-30T13:00:00.000Z';

export function registrationRequest(mode = 'tcp') {
  return { instanceId: alpha, instanceKey: 'synthetic-sql', displayName: 'Synthetic SQL', host: 'sql.example.test',
    ...(mode === 'named' ? { namedInstance: 'APP' } : { tcpPort: 1444 }),
    certificateHostName: 'certificate.example.test' };
}

// A synthetic response exercises client callback handling, not server auth.
export function registeredTarget(request, overrides = {}) {
  return { ...request, authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
    lifecycle: 'pending_discovery', capabilityStatus: 'pending', capabilityReasons: ['Capability discovery is queued.'],
    configurationRevision: 1, discoveryRequestedAtUtc: at, ...overrides };
}
