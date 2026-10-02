import { apiFetch } from "../../apiClient.ts";
import { readBoundedBody } from "./activityParser.mjs";
import { utcMicroseconds } from "../../utcTimestamp.ts";
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
const utc = (value: unknown) => utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
const guid = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const text = (value: unknown) => value === null || typeof value === "string";
/** Every metadata page is bounded before it can enter inspection or cursor restoration. */
function validatePage(value: LivePage, parameters: URLSearchParams): void {
  if (!value || !Array.isArray(value.rows) || value.rows.length > 50 || !Array.isArray(value.databases) ||
      typeof value.state !== "string" || typeof value.truncated !== "boolean" ||
      value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length < 1 || value.nextCursor.length > 2048)) throw new Error("Invalid session snapshot metadata.");
  utc(value.repositoryTimeUtc);
  if (value.snapshotId === null) {
    if (value.observedUtc !== null || value.rows.length || value.nextCursor !== null) throw new Error("Invalid unavailable session snapshot.");
  } else {
    if (!guid(value.snapshotId)) throw new Error("Invalid session snapshot identity.");
    utc(value.observedUtc);
    const requestedSnapshot = parameters.get("snapshot");
    if (requestedSnapshot !== null && requestedSnapshot.toLowerCase() !== value.snapshotId.toLowerCase()) throw new Error("Session snapshot did not match the requested capture.");
  }
  const identities = new Set<string>();
  for (const row of value.rows) {
    if (!row || typeof row.identity !== "string" || !/^[0-9a-f]{64}$/iu.test(row.identity) || identities.has(row.identity.toLowerCase()) ||
        !Number.isSafeInteger(row.sessionId) || row.sessionId < 1 || row.sessionId > 32767 ||
        row.requestId !== null && (!Number.isSafeInteger(row.requestId) || row.requestId < 0) ||
        row.databaseId !== null && (!Number.isSafeInteger(row.databaseId) || row.databaseId < 1 || row.databaseId > 32767) ||
        typeof row.status !== "string" || typeof row.queryState !== "string" || typeof row.isUser !== "boolean" ||
        [row.databaseName, row.login, row.clientHost, row.application, row.command, row.waitType, row.queryId, row.engineStartup, row.sessionLogin, row.requestStart].some(item => !text(item)) ||
        row.blocker !== null && (!Number.isSafeInteger(row.blocker) || row.blocker < -32768 || row.blocker > 32767) ||
        [row.cpuMs, row.memoryBytes, row.reads, row.writes, row.logicalReads, row.elapsedMs].some(item => typeof item !== "string" || !/^\d{1,40}$/u.test(item)) ||
        row.delta !== null && (!row.delta || !Number.isFinite(row.delta.seconds) || row.delta.seconds <= 0 ||
          [row.delta.cpuMs, row.delta.reads, row.delta.writes, row.delta.logicalReads].some(item => typeof item !== "string" || !/^\d{1,40}$/u.test(item))))
      throw new Error("Invalid session request metadata.");
    identities.add(row.identity.toLowerCase());
  }
  for (const database of value.databases) if (!database || !Number.isSafeInteger(database.id) || database.id < 1 || database.id > 32767 || !text(database.name)) throw new Error("Invalid session database metadata.");
}
export async function readLive<T>(target: string, path: string, parameters: URLSearchParams, signal: AbortSignal): Promise<T> {
  const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(target)}/activity/live${path}?${parameters}`, {
    credentials: "same-origin", cache: "no-store", signal, headers: { Accept: "application/json" },
  });
  if (!response.ok) throw new Error(response.status === 403 ? "You do not have permission to read this evidence." : "Collection evidence is unavailable. Displayed observations have been retained.");
  if (!(response.headers.get("content-type") ?? "").toLowerCase().includes("application/json")) throw new Error("Invalid activity response.");
  const value = JSON.parse(await readBoundedBody(response, signal, 1024 * 1024));
  if (path === "") validatePage(value as LivePage, parameters);
  else if (path === "/query") {
    const detail = value as QueryDetail;
    if (!detail || !["available", "unavailable"].includes(detail.state) || !text(detail.text) ||
        detail.text !== null && new TextEncoder().encode(detail.text).byteLength > 16 * 1024 ||
        detail.state !== "available" && detail.text !== null) throw new Error("Invalid captured SQL response.");
  } else if (path === "/history") {
    if (!Array.isArray(value) || value.length > 10000) throw new Error("Invalid session history metadata.");
    const from = utc(parameters.get("from")), to = utc(parameters.get("to")), ids = new Set<string>();
    for (const snapshot of value as LiveSnapshot[]) {
      if (!snapshot || !guid(snapshot.id) || ids.has(snapshot.id.toLowerCase()) || typeof snapshot.truncated !== "boolean" ||
          utc(snapshot.observedUtc) < from || utc(snapshot.observedUtc) >= to ||
          snapshot.deadlockEventId != null && !guid(snapshot.deadlockEventId)) throw new Error("Invalid session history capture.");
      if (snapshot.deadlockOccurredUtc != null) utc(snapshot.deadlockOccurredUtc);
      ids.add(snapshot.id.toLowerCase());
    }
  }
  return value as T;
}
