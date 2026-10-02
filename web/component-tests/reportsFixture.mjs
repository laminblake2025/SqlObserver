export const target = '11111111-1111-4111-8111-111111111111';
export const otherTarget = '44444444-4444-4444-8444-444444444444';
export const runId = '22222222-2222-4222-8222-222222222222';
export const otherRunId = '55555555-5555-4555-8555-555555555555';
export const postPath = id => `/api/v1/observation-targets/${id}/reports`;
export const reportRun = (overrides = {}) => ({ runId, targetId: target, reportKind: 'instance-health',
  snapshotUtc: '2026-08-24T13:00:00Z', expiresAtUtc: '2026-08-24T14:00:00Z', state: 'ready', ...overrides });
