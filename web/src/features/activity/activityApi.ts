import type { ActivityPage, ActivityRequest, ActivitySession, ActivityWait, BlockingEdge, BlockingHistoryItem } from "./activityTypes";
import { getPage, parseEdge, parseHistory, parseRequest, parseSession, parseWait } from "./activityParser.mjs";
export { ActivityRequestError, safeStatusMessage } from "./activityParser.mjs";

const pageLimit = 25;

export async function getActivitySnapshot(instanceId: string, signal: AbortSignal): Promise<{
  readonly sessions: ActivityPage<ActivitySession>; readonly requests: ActivityPage<ActivityRequest>;
  readonly waits: ActivityPage<ActivityWait>; readonly blocking: ActivityPage<BlockingEdge>;
  readonly history: ActivityPage<BlockingHistoryItem>;
}> {
  const base = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity`;
  const now = Date.now();
  const [sessions, requests, waits, blocking, history] = await Promise.all([
    getPage(`${base}/sessions?limit=${String(pageLimit)}`, parseSession, instanceId, signal),
    getPage(`${base}/requests?limit=${String(pageLimit)}`, parseRequest, instanceId, signal),
    getPage(`${base}/waits?limit=${String(pageLimit)}`, parseWait, instanceId, signal),
    getPage(`${base}/blocking/current?limit=${String(pageLimit)}`, parseEdge, instanceId, signal),
    getPage(`${base}/blocking/history?limit=${String(pageLimit)}&fromUtc=${encodeURIComponent(new Date(now - 3_600_000).toISOString())}&toUtc=${encodeURIComponent(new Date(now).toISOString())}`, parseHistory, instanceId, signal),
  ]);
  return { sessions, requests, waits, blocking, history };
}
