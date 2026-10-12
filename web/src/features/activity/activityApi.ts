import type { ActivityPage, ActivityRequest, ActivitySession, ActivityWait, BlockingEdge, BlockingHistoryItem, ServerWaitHistoryItem } from "./activityTypes";
import { ActivityRequestError, getPage, parseEdge, parseHistory, parseRequest, parseSession, parseWait, parseWaitHistory } from "./activityParser.mjs";
import { utcMicroseconds } from "../../utcTimestamp.ts";
export { ActivityRequestError, safeStatusMessage } from "./activityParser.mjs";

const pageLimit = 25;
const blockingGraphLimit = 256;
const nativeUtc = (value: unknown): bigint => utcMicroseconds(typeof value === "string"
  ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
function validHistoryWindow(window: { readonly fromUtc: string; readonly toUtc: string }): boolean {
  try {
    const from = nativeUtc(window.fromUtc), to = nativeUtc(window.toUtc);
    return to > from && to - from <= 24n * 3_600_000_000n;
  } catch { return false; }
}
function bindHistoryWindow<T>(page: ActivityPage<T>, window: { readonly fromUtc: string; readonly toUtc: string }, message: string): ActivityPage<T> {
  try {
    if (nativeUtc(page.fromUtc) === nativeUtc(window.fromUtc) && nativeUtc(page.toUtc) === nativeUtc(window.toUtc)) return page;
  } catch { /* Never reflect provider-controlled timestamp or parser detail. */ }
  throw new Error(message);
}

export async function getCurrentServerWaitPage(instanceId: string, signal: AbortSignal, cursor?: string): Promise<ActivityPage<ActivityWait>> {
  const parameters = new URLSearchParams({ limit: String(pageLimit) });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/waits?${parameters}`, parseWait, instanceId, signal);
}

export async function getBlockingHistoryPage(instanceId: string, window: { readonly fromUtc: string; readonly toUtc: string }, signal: AbortSignal, cursor?: string): Promise<ActivityPage<BlockingHistoryItem>> {
  if (!validHistoryWindow(window)) throw new Error("Invalid blocking history window.");
  const parameters = new URLSearchParams({ limit: String(pageLimit), fromUtc: window.fromUtc, toUtc: window.toUtc });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  const page = await getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/blocking/history?${parameters}`, parseHistory, instanceId, signal);
  return bindHistoryWindow(page, window, "Blocking history response did not match the selected UTC window.");
}

export async function getServerWaitHistoryPage(instanceId: string, window: { readonly fromUtc: string; readonly toUtc: string }, signal: AbortSignal, cursor?: string): Promise<ActivityPage<ServerWaitHistoryItem>> {
  if (!validHistoryWindow(window)) throw new Error("Invalid wait history window.");
  const parameters = new URLSearchParams({ limit: String(pageLimit), fromUtc: window.fromUtc, toUtc: window.toUtc });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  const page = await getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/waits/history?${parameters}`, parseWaitHistory, instanceId, signal);
  return bindHistoryWindow(page, window, "Wait history response did not match the selected UTC window.");
}

export async function getCurrentBlockingPage(instanceId: string, signal: AbortSignal): Promise<ActivityPage<BlockingEdge>> {
  return getPage(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity/blocking/current?limit=${String(blockingGraphLimit)}`, parseEdge, instanceId, signal, blockingGraphLimit);
}

export type ActivitySnapshotSource = "sessions" | "requests" | "waits" | "blocking" | "history" | "waitHistory";
export async function getActivitySnapshot(instanceId: string, signal: AbortSignal, historySelection: 1 | 6 | 24 | { readonly fromUtc: string; readonly toUtc: string } | null = 1, onAccessDenied?: () => void, readSources?: readonly ActivitySnapshotSource[]): Promise<{
  readonly sessions?: ActivityPage<ActivitySession>; readonly requests?: ActivityPage<ActivityRequest>;
  readonly waits?: ActivityPage<ActivityWait>; readonly blocking?: ActivityPage<BlockingEdge>;
  readonly history?: ActivityPage<BlockingHistoryItem>; readonly waitHistory?: ActivityPage<ServerWaitHistoryItem>; readonly errors: readonly string[];
}> {
  if (typeof historySelection === "number" && ![1, 6, 24].includes(historySelection)) throw new Error("Invalid blocking history window.");
  const base = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/activity`;
  const now = Date.now();
  const errors: string[] = [];
  const read = async <T,>(name: string, operation: Promise<T>): Promise<T | undefined> => {
    try { return await operation; }
    catch (error) {
      if (signal.aborted) throw error;
      if (onAccessDenied && error instanceof ActivityRequestError && (error.status === 401 || error.status === 403)) {
        onAccessDenied(); throw error;
      }
      errors.push(`${name}: ${error instanceof Error ? error.message : "Evidence unavailable."}`); return undefined;
    }
  };
  const historyWindow = typeof historySelection === "number"
    ? { fromUtc: new Date(now - historySelection * 3_600_000).toISOString(), toUtc: new Date(now).toISOString() }
    : historySelection;
  const included = (source: ActivitySnapshotSource) => readSources === undefined || readSources.includes(source);
  const [sessions, requests, waits, blocking, history, waitHistory] = await Promise.all([
    included("sessions") ? read("Sessions", getPage(`${base}/sessions?limit=${String(pageLimit)}`, parseSession, instanceId, signal)) : undefined,
    included("requests") ? read("Requests", getPage(`${base}/requests?limit=${String(pageLimit)}`, parseRequest, instanceId, signal)) : undefined,
    included("waits") ? read("Waits", getPage(`${base}/waits?limit=${String(pageLimit)}`, parseWait, instanceId, signal)) : undefined,
    included("blocking") ? read("Current blocking", getCurrentBlockingPage(instanceId, signal)) : undefined,
    historyWindow === null || !included("history") ? undefined : read("Blocking history", getBlockingHistoryPage(instanceId, historyWindow, signal)),
    historyWindow === null || !included("waitHistory") ? undefined : read("Wait history", getServerWaitHistoryPage(instanceId, historyWindow, signal)),
  ]);
  return { sessions, requests, waits, blocking, history, waitHistory, errors };
}
