// Synthetic, loopback-only browser QA. No production import or source job content.
const requests = [];
const cursors = new Map();
let delayMilliseconds = 0;
let sequence = 0;
const runId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export async function handleAgentHistoryFixture(url, res, targets, send) {
  if (url.pathname === '/__agent_fixture/observations') {
    if (url.searchParams.get('clear') === '1') requests.length = 0;
    if (url.searchParams.has('delay')) delayMilliseconds = Math.min(2500, Math.max(0, Number(url.searchParams.get('delay')) || 0));
    send(res, { requests, delayMilliseconds }); return true;
  }
  if (!/^\/api\/v1\/observation-targets\/[^/]+\/sql-agent\/failures$/.test(url.pathname)) return false;
  const targetId = url.pathname.split('/')[4];
  const fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
  const cursor = url.searchParams.get('cursor'), limit = Number(url.searchParams.get('limit'));
  const from = Date.parse(fromUtc), to = Date.parse(toUtc);
  const binding = JSON.stringify([targetId, fromUtc, toUtc, limit]);
  if (!targets.some(target => target.instanceId === targetId) || limit !== 50 || !Number.isFinite(from) || !Number.isFinite(to)
    || to <= from || to - from > 7 * 86400000 || cursor && cursors.get(cursor) !== binding) {
    send(res, {}, 400); return true;
  }
  requests.push({ targetId, fromUtc, toUtc, limit, cursor });
  if (delayMilliseconds) await new Promise(resolve => setTimeout(resolve, delayMilliseconds));
  const all = Array.from({ length: 64 }, (_, index) => {
    const outcome = index % 3 === 0, retry = index % 3 === 2;
    return {
      jobId: `22222222-2222-4222-8222-${String(Math.floor(index / 3) + 1).padStart(12, '0')}`,
      historyInstanceId: 1000 + index, stepId: outcome ? 0 : 1, runStatus: retry ? 2 : 0,
      isJobOutcome: outcome, countsAsJobFailure: outcome, failureKind: retry ? 'Retry' : 'Failed',
      messageId: null, severity: null, retryAttempt: retry ? 1 : 0, durationSeconds: 42 + index,
      firstObservedAtUtc: new Date(from + Math.floor((to - from) * (index + 1) / 65)).toISOString(),
      sourceLocalStart: '2026-09-29T21:20:00', contentAvailable: false, failureFingerprint: 'a'.repeat(64),
    };
  });
  const nextCursor = cursor ? null : `synthetic-agent-${++sequence}`;
  if (nextCursor) cursors.set(nextCursor, binding);
  send(res, { targetId, targetRevision: 1, runId, observedAtUtc: new Date().toISOString(), state: 'Partial',
    coverageFromUtc: fromUtc, coverageToUtc: toUtc, evidence: null, items: cursor ? all.slice(50) : all.slice(0, 50),
    hasMore: !cursor, nextCursor, truncated: true });
  return true;
}
