import type { LivePage, LiveRow, LiveSnapshot } from "./liveActivityApi.ts";
import { activityUtc, validateActivitySelectionPage } from "./activitySelectionModel.ts";
import { overviewHref, readOverviewScope } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import { canonicalUtcTimestamp } from "../../utcTimestamp.ts";

export const sessionWaitCategories = ["Lock", "Log", "I/O", "CPU/signal", "Memory", "Parallelism", "Other", "Idle"] as const;
export type SessionWaitCategory = typeof sessionWaitCategories[number];
const guid = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const idle = new Set(["LAZYWRITER_SLEEP", "SQLTRACE_BUFFER_FLUSH", "SQLTRACE_INCREMENTAL_FLUSH_SLEEP", "SQLTRACE_WAIT_ENTRIES", "FT_IFTS_SCHEDULER_IDLE_WAIT", "XE_DISPATCHER_WAIT", "REQUEST_FOR_DEADLOCK_SEARCH", "LOGMGR_QUEUE", "ONDEMAND_TASK_QUEUE", "CHECKPOINT_QUEUE", "XE_TIMER_EVENT"]);
/** Mirrors telemetry.server_wait_category_v1, migration 0137; not Query Store categories. */
export function capturedSessionWaitCategory(waitType: string | null): SessionWaitCategory | undefined {
  if (waitType === null || waitType === "") return undefined;
  if (waitType.startsWith("SLEEP_") || idle.has(waitType)) return "Idle";
  if (waitType.startsWith("LCK_M_")) return "Lock";
  if (["WRITELOG", "LOGBUFFER", "LOG_RATE_GOVERNOR"].includes(waitType) || waitType.startsWith("LOGMGR_")) return "Log";
  if (waitType.startsWith("PAGEIOLATCH_") || ["IO_COMPLETION", "ASYNC_IO_COMPLETION", "BACKUPIO", "WRITE_COMPLETION"].includes(waitType)) return "I/O";
  if (waitType === "SOS_SCHEDULER_YIELD") return "CPU/signal";
  if (waitType.startsWith("RESOURCE_SEMAPHORE") || waitType === "MEMORY_ALLOCATION_EXT") return "Memory";
  if (["CXPACKET", "CXCONSUMER", "EXCHANGE"].includes(waitType) || waitType.startsWith("CXSYNC_")) return "Parallelism";
  return "Other";
}
export function readSessionWaitCategory(hash: string, targetId?: string): SessionWaitCategory | null | undefined {
  const [path, query] = hash.split("?"); const parameters = new URLSearchParams(query);
  const values = parameters.getAll("sessionWaitCategory");
  if (!values.length) return undefined;
  if (path !== "#/waits" || values.length !== 1 || !sessionWaitCategories.includes(values[0] as SessionWaitCategory)
      || parameters.getAll("target").length !== 1 || !guid(parameters.get("target"))
      || targetId !== undefined && parameters.get("target")!.toLowerCase() !== targetId.toLowerCase()
      || ["range", "from", "to", "databaseId", "compare"].some(key => parameters.getAll(key).length > 1)) return null;
  const scope = readOverviewScope(hash);
  if (!parameters.has("range") || !["1h", "6h", "24h", "7d", "custom"].includes(parameters.get("range")!)) return null;
  if (scope.range === "custom") {
    try { const from = activityUtc(scope.from), to = activityUtc(scope.to); if (from >= to || to - from > 31n * 86400000000n) return null; } catch { return null; }
  }
  if (parameters.has("databaseId") && (!/^[1-9]\d{0,4}$/u.test(parameters.get("databaseId")!) || Number(parameters.get("databaseId")) > 32767)
      || parameters.has("compare") && !["0", "1"].includes(parameters.get("compare")!)) return null;
  return values[0] as SessionWaitCategory;
}
export function sessionWaitCategoryHref(scope: OverviewScope, category: SessionWaitCategory, baseHref = overviewHref(scope, "waits", scope.target)): string {
  if (!guid(scope.target) || !sessionWaitCategories.includes(category) || baseHref.split("?")[0] !== "#/waits") throw new Error("Choose a supported session wait category on this server.");
  const parameters = new URLSearchParams(baseHref.split("?")[1]);
  if (parameters.get("target")?.toLowerCase() !== scope.target.toLowerCase()) throw new Error("Session wait category target changed.");
  parameters.set("sessionWaitCategory", category);
  return `#/waits?${parameters}`;
}
export function retainSessionWaitCategoryHref(href: string, hash: string): string {
  if (href.split("?")[0] !== "#/waits") return href;
  const scope = readOverviewScope(href), category = readSessionWaitCategory(hash, scope.target);
  return category ? sessionWaitCategoryHref(scope, category, href) : href;
}
export interface SessionWaitCapture { readonly id: string; readonly observedUtc: string; }
const captureFields = ["sessionSnapshot", "sessionObservedUtc"] as const;
/** Absent, invalid and a real requested capture are distinct: invalid never selects newest. */
export function readSessionWaitCapture(hash: string, expectedTarget?: string): SessionWaitCapture | null | undefined {
  const [path, query] = hash.split("?"); const parameters = new URLSearchParams(query);
  if (!captureFields.some(field => parameters.has(field))) return undefined;
  if (path !== "#/waits" || captureFields.some(field => parameters.getAll(field).length !== 1)
      || parameters.getAll("target").length !== 1 || !guid(parameters.get("target"))
      || expectedTarget !== undefined && (!guid(expectedTarget) || expectedTarget.toLowerCase() !== parameters.get("target")!.toLowerCase())
      || parameters.getAll("range").length !== 1 || !["1h", "6h", "24h", "7d", "custom"].includes(parameters.get("range")!)
      || ["from", "to", "databaseId", "compare"].some(field => parameters.getAll(field).length > 1)
      || parameters.has("at") || parameters.has("event")
      || parameters.has("databaseId") && (!/^[1-9]\d{0,4}$/u.test(parameters.get("databaseId")!) || Number(parameters.get("databaseId")) > 32767)
      || parameters.has("compare") && !["0", "1"].includes(parameters.get("compare")!)) return null;
  const id = parameters.get("sessionSnapshot"), observedUtc = parameters.get("sessionObservedUtc");
  if (!guid(id) || !observedUtc) return null;
  try {
    const observed = activityUtc(observedUtc);
    if (parameters.get("range") === "custom") {
      const from = activityUtc(parameters.get("from")), to = activityUtc(parameters.get("to"));
      if (from >= to || to - from > 31n * 86400000000n || observed < from || observed >= to) return null;
    } else if (parameters.has("from") || parameters.has("to")) return null;
    return { id: id.toLowerCase(), observedUtc: canonicalUtcTimestamp(observedUtc.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")) };
  } catch { return null; }
}
export function sessionWaitCaptureHref(scope: OverviewScope, capture: SessionWaitCapture | undefined,
    baseHref = overviewHref(scope, "waits", scope.target)): string {
  const parameters = new URLSearchParams(baseHref.split("?")[1]);
  for (const field of captureFields) parameters.delete(field);
  if (baseHref.split("?")[0] !== "#/waits" || !guid(scope.target) || parameters.get("target")?.toLowerCase() !== scope.target.toLowerCase())
    throw new Error("The captured-session workspace changed.");
  if (capture) { parameters.set("sessionSnapshot", capture.id); parameters.set("sessionObservedUtc", capture.observedUtc); }
  if (capture) {
    const validated = readSessionWaitCapture(`#/waits?${parameters}`, scope.target);
    if (!validated) throw new Error("Select a retained capture inside this workspace window.");
    parameters.set("sessionSnapshot", validated.id);
    parameters.set("sessionObservedUtc", validated.observedUtc);
  }
  return `#/waits?${parameters}`;
}
export function retainSessionWaitCaptureHref(href: string, currentHash: string): string {
  if (href.split("?")[0] !== "#/waits") return href;
  const scope = readOverviewScope(href), previous = readOverviewScope(currentHash);
  if (scope.databaseId !== previous.databaseId) return href;
  const capture = readSessionWaitCapture(currentHash, scope.target);
  if (!capture || scope.range !== "custom" && (scope.range !== previous.range || scope.from !== previous.from || scope.to !== previous.to)) return href;
  try { return sessionWaitCaptureHref(scope, capture, href); } catch { return href; }
}
/** Exact loaded-window and native repository retention gates; URL validity alone cannot establish retention. */
export function validateWaitCaptureWindow(capture: SessionWaitCapture, window: { fromUtc: string; toUtc: string }, now: number): void {
  const observed = activityUtc(capture.observedUtc);
  if (observed < activityUtc(window.fromUtc) || observed >= activityUtc(window.toUtc)) throw new Error("The requested session capture is outside the loaded window. No other capture has been selected.");
  if (observed <= BigInt(Math.trunc(now)) * 1000n - 86400000000n) throw new Error("The requested session capture has expired beyond the 24-hour retention. No other capture has been selected.");
}
const timestamp = (value: bigint) => {
  const second = value - (value % 1000000n + 1000000n) % 1000000n;
  return `${new Date(Number(second / 1000n)).toISOString().slice(0, 19)}.${String(value - second).padStart(6, "0")}Z`;
};
export function waitSessionHistoryWindow(window: { fromUtc: string; toUtc: string }, now: number): { fromUtc: string; toUtc: string } | undefined {
  if (!Number.isFinite(now)) throw new Error("Session retention time was invalid.");
  const from = activityUtc(window.fromUtc), to = activityUtc(window.toUtc), clock = BigInt(Math.trunc(now)) * 1000n;
  if (from >= to) throw new Error("Session history window was invalid.");
  const retainedFrom = from > clock - 86400000000n + 1n ? from : clock - 86400000000n + 1n;
  const retainedTo = to < clock + 1n ? to : clock + 1n;
  return retainedFrom < retainedTo ? { fromUtc: timestamp(retainedFrom), toUtc: timestamp(retainedTo) } : undefined;
}
export function boundedWaitSessionCaptures(snapshots: readonly LiveSnapshot[], window: { fromUtc: string; toUtc: string }): { captures: readonly LiveSnapshot[]; omitted: number } {
  if (!Array.isArray(snapshots) || snapshots.length > 10000) throw new Error("Retained session history exceeded its bound.");
  const from = activityUtc(window.fromUtc), to = activityUtc(window.toUtc), seen = new Set<string>();
  for (const capture of snapshots) {
    if (!capture || !guid(capture.id) || seen.has(capture.id.toLowerCase()) || typeof capture.truncated !== "boolean"
        || activityUtc(capture.observedUtc) < from || activityUtc(capture.observedUtc) >= to) throw new Error("Retained session history did not match this window.");
    seen.add(capture.id.toLowerCase());
  }
  const ordered = [...snapshots].sort((a, b) => activityUtc(a.observedUtc) > activityUtc(b.observedUtc) ? -1 : activityUtc(a.observedUtc) < activityUtc(b.observedUtc) ? 1 : a.id.localeCompare(b.id));
  return { captures: ordered.slice(0, 100), omitted: Math.max(0, ordered.length - 100) };
}
export interface CapturedWaitSessions { readonly page: LivePage; readonly rows: readonly LiveRow[]; readonly pagesRead: number; readonly limited: boolean; }
export function capturedWaitSnapshotExpired(page: LivePage, now: number): boolean {
  if (page.snapshotId === null || page.observedUtc === null) return false;
  const repository = activityUtc(page.repositoryTimeUtc), local = BigInt(Math.trunc(now)) * 1000n;
  return activityUtc(page.observedUtc) <= (repository > local ? repository : local) - 86400000000n;
}
export type WaitSessionReader = (parameters: URLSearchParams, signal: AbortSignal) => Promise<LivePage>;
/** Metadata only; all continuation pages stay bound to one real capture. */
export async function loadCapturedWaitSessions(reader: WaitSessionReader, filters: URLSearchParams, signal: AbortSignal, capture?: LiveSnapshot): Promise<CapturedWaitSessions> {
  const parameters = new URLSearchParams(filters); parameters.delete("cursor");
  if (capture) parameters.set("snapshot", capture.id); else parameters.delete("snapshot");
  const rows: LiveRow[] = [], identities = new Set<string>(), cursors = new Set<string>();
  let first: LivePage | undefined, binding = capture ? { snapshotId: capture.id, observedUtc: capture.observedUtc } : undefined;
  for (let pagesRead = 1; pagesRead <= 11; pagesRead++) {
    signal.throwIfAborted(); const page = await reader(new URLSearchParams(parameters), signal); signal.throwIfAborted();
    if (!["available", "stale", "unavailable"].includes(page.state)) throw new Error("Session capture returned an unknown evidence state.");
    if (!first) first = page;
    if (page.snapshotId === null) {
      if (page.state !== "unavailable" || page.observedUtc !== null || page.rows.length || page.nextCursor !== null) throw new Error("Unavailable session capture returned rows.");
      if (pagesRead > 1) throw new Error("The retained session capture expired while paging. Refresh to retry.");
      return { page, rows: [], pagesRead, limited: false };
    }
    binding ??= { snapshotId: page.snapshotId, observedUtc: page.observedUtc! };
    validateActivitySelectionPage(page, binding);
    if (page.truncated !== first.truncated || page.state === "unavailable") throw new Error("Session capture evidence changed while paging.");
    for (const row of page.rows) {
      if (identities.has(row.identity.toLowerCase()) || rows.length === 512 || filters.has("databaseId") && row.databaseId !== Number(filters.get("databaseId"))
          || row.waitType !== null && (typeof row.waitType !== "string" || row.waitType.length > 128)) throw new Error("Session capture returned duplicate or out-of-scope request metadata.");
      identities.add(row.identity.toLowerCase()); rows.push(row);
    }
    if (!page.nextCursor || pagesRead === 11 || rows.length === 512) return { page: first, rows, pagesRead, limited: Boolean(page.nextCursor) };
    if (!page.rows.length || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(page.nextCursor)
        || cursors.has(page.nextCursor)) throw new Error("Session capture cursor was invalid or repeated.");
    cursors.add(page.nextCursor); parameters.set("snapshot", binding.snapshotId); parameters.set("cursor", page.nextCursor);
  }
  throw new Error("Session capture exceeded its metadata page bound.");
}
