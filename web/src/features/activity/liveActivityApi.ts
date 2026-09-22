import { responseFailure } from "../../api/responseFailure.ts";
import { readBoundedBody } from "./activityParser.mjs";
export interface LiveRow {
  identity: string; sessionId: number; requestId: number | null; databaseId: number | null; databaseName: string | null;
  login: string | null; clientHost: string | null; application: string | null; status: string; command: string | null;
  isUser: boolean; waitType: string | null; blocker: number | null; cpuMs: string; memoryBytes: string; reads: string;
  writes: string; logicalReads: string; elapsedMs: string; queryId: string | null; queryState: string;
  engineStartup: string; sessionLogin: string; requestStart: string | null;
  delta: { seconds: number; cpuMs: string; reads: string; writes: string; logicalReads: string } | null;
}
export interface LivePage {
  snapshotId: string | null; observedUtc: string | null; repositoryTimeUtc: string; state: string; truncated: boolean;
  rows: LiveRow[]; databases: { id: number; name: string | null }[]; nextCursor: string | null;
}
export interface LiveSnapshot { id: string; observedUtc: string; truncated: boolean; deadlockEventId?: string | null; deadlockOccurredUtc?: string | null }
export interface QueryDetail { state: string; text: string | null }
export async function readLive<T>(target: string, path: string, parameters: URLSearchParams, signal: AbortSignal): Promise<T> {
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(target)}/activity/live${path}?${parameters}`, {
    credentials: "same-origin", cache: "no-store", signal, headers: { Accept: "application/json" },
  });
  if (!response.ok) throw responseFailure(response, response.status === 403 ? "You do not have permission to read this evidence." : "Collection evidence is unavailable. Displayed observations have been retained.");
  if (!(response.headers.get("content-type") ?? "").toLowerCase().includes("application/json")) throw new Error("Invalid activity response.");
  return JSON.parse(await readBoundedBody(response, signal, 1024 * 1024)) as T;
}
