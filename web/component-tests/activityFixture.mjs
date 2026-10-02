// Native activity DTOs: these pass through the real API clients and parsers.
export const alpha = '11111111-1111-4111-8111-111111111111';
export const beta = '22222222-2222-4222-8222-222222222222';
export const at = '2026-09-30T12:45:00.000Z';
export const fromUtc = '2026-09-30T12:00:00.000Z';
export const toUtc = '2026-09-30T13:00:00.000Z';
export const cursor = 'history+/=next';
export const baselineRunId = '44444444-4444-4444-8444-444444444444';

export function evidence(overrides = {}) {
  return { runId: '33333333-3333-4333-8333-333333333333', targetRevision: '1',
    collectorId: 'engine.blocking', freshness: 'stale', outcome: 'partial', reason: 'sample_loss',
    isPartial: true, completedAtUtc: at,
    loss: { kind: 'source_row_limit', minimumLostItems: 3, minimumLostBytes: 12, countIsExact: false },
    ...overrides };
}

export function edge(overrides = {}) {
  return { blockedSessionId: 71, blockerKind: 'session', blockerSessionId: 63,
    rootBlockerSessionId: 59, waitType: 'LCK_M_X', waitingTaskCount: '2',
    waitDurationMilliseconds: '9007199254740993', chainDepth: 2, chainState: 'root_missing',
    observedAtUtc: at, ...overrides };
}

export function blockingHistoryItem(edgeOverrides = {}, evidenceOverrides = {}) {
  return { edge: edge(edgeOverrides), evidence: evidence(evidenceOverrides) };
}

export function wait(overrides = {}) {
  return { waitType: 'LCK_M_X', waitingTasksCount: '9007199254740993',
    waitTimeMilliseconds: '9007199254740995', maximumWaitTimeMilliseconds: '300',
    signalWaitTimeMilliseconds: '50', baselineAvailable: true, resetDetected: false,
    waitingTasksDelta: '2', waitTimeMillisecondsDelta: '75', signalWaitTimeMillisecondsDelta: '5',
    observedAtUtc: at, ...overrides };
}

export function waitHistoryItem(waitOverrides = {}, evidenceOverrides = {}) {
  return { wait: wait(waitOverrides), evidence: evidence({ collectorId: 'engine.waits', ...evidenceOverrides }), baselineRunId };
}

export function activityFixture(instanceId = alpha) {
  const page = (items = [], overrides = {}) => ({ instanceId, repositoryTimeUtc: toUtc,
    evidence: evidence(), items, nextCursor: null, ...overrides });
  return {
    sessions: page(), requests: page(),
    waits: page([wait()], { evidence: evidence({ collectorId: 'engine.waits' }), baselineRunId }),
    'blocking/current': page([edge()], { maximumChainDepth: 16, maximumGraphNodes: 256 }),
    'blocking/history': page([blockingHistoryItem()], { fromUtc, toUtc }),
    'waits/history': page([waitHistoryItem()], { fromUtc, toUtc }),
  };
}

export function unavailableLivePage() {
  return { snapshotId: null, observedUtc: null, repositoryTimeUtc: toUtc, state: 'unavailable',
    truncated: false, rows: [], databases: [], nextCursor: null };
}
