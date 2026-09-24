import type { ActivityPage, ActivityRequest, ActivitySession, ActivityWait, BlockingEdge, BlockingHistoryItem } from "./activityTypes";
import { getPage, parseEdge, parseHistory, parseRequest, parseSession, parseWait } from "./activityParser.mjs";
export { ActivityRequestError, safeStatusMessage } from "./activityParser.mjs";

const pageLimit = 25;

export async function getBlockingHistoryPage(instanceId: string, window: { readonly fromUtc: string; readonly toUtc: string }, signal: AbortSignal, cursor?: string): Promise<ActivityPage<BlockingHistoryItem>> {
  const from = Date.parse(window.fromUtc), to = Date.parse(window.toUtc);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from || to - from > 24 * 3_600_000) throw new Error("Invalid blocking history window.");
  const parameters = new URLSearchParams({ limit: String(pageLimit), fromUtc: window.fromUtc, toUtc: window.toUtc });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/blocking/history?${parameters}`, parseHistory, instanceId, signal);
}

export async function getCurrentBlockingPage(instanceId: string, signal: AbortSignal): Promise<ActivityPage<BlockingEdge>> {
  return getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/blocking/current?limit=${String(pageLimit)}`, parseEdge, instanceId, signal);
}

export async function getActivitySnapshot(instanceId: string, signal: AbortSignal, historySelection: 1 | 6 | 24 | { readonly fromUtc: string; readonly toUtc: string } | null = 1): Promise<{
  readonly sessions?: ActivityPage<ActivitySession>; readonly requests?: ActivityPage<ActivityRequest>;
  readonly waits?: ActivityPage<ActivityWait>; readonly blocking?: ActivityPage<BlockingEdge>;
  readonly history?: ActivityPage<BlockingHistoryItem>; readonly errors: readonly string[];
}> {
  if (typeof historySelection === "number" && ![1, 6, 24].includes(historySelection)) throw new Error("Invalid blocking history window.");
  const base = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity`;
  const now = Date.now();
  const errors: string[] = [];
  const read = async <T,>(name: string, operation: Promise<T>): Promise<T | undefined> => { try { return await operation; } catch (error) { if (signal.aborted) throw error; errors.push(`${name}: ${error instanceof Error ? error.message : "Evidence unavailable."}`); return undefined; } };
  const historyWindow = typeof historySelection === "number"
    ? { fromUtc: new Date(now - historySelection * 3_600_000).toISOString(), toUtc: new Date(now).toISOString() }
    : historySelection;
  const [sessions, requests, waits, blocking, history] = await Promise.all([
    read("Sessions", getPage(`${base}/sessions?limit=${String(pageLimit)}`, parseSession, instanceId, signal)),
    read("Requests", getPage(`${base}/requests?limit=${String(pageLimit)}`, parseRequest, instanceId, signal)),
    read("Waits", getPage(`${base}/waits?limit=${String(pageLimit)}`, parseWait, instanceId, signal)),
    read("Current blocking", getPage(`${base}/blocking/current?limit=${String(pageLimit)}`, parseEdge, instanceId, signal)),
    historyWindow === null ? Promise.resolve(undefined) : read("Blocking history", getBlockingHistoryPage(instanceId, historyWindow, signal)),
  ]);
  return { sessions, requests, waits, blocking, history, errors };
}
