import { overviewHref, readOverviewScope } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import type { LivePage, LiveRow } from "./liveActivityApi.ts";

export interface ActivitySelection {
  readonly snapshotId: string;
  readonly observedUtc: string;
  readonly identity?: string;
  readonly chain?: string;
}
const parameterNames = ["sessionSnapshot", "sessionObserved", "sessionIdentity", "sessionChain"] as const;
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const identity = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{64}$/iu.test(value);
export function validActivityChain(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = /^(session|cycle|ambiguous|lifetime|special):(-?[1-9]\d{0,4})$/u.exec(value);
  if (!match) return false;
  const number = Number(match[2]);
  return match[1] === "special" ? number < 0 && number >= -32768 : number > 0 && number <= 32767;
}
const normalized = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
export function activityUtc(value: unknown): bigint {
  return utcMicroseconds(typeof value === "string" ? normalized(value) : value);
}
function validSelection(scope: OverviewScope, selection: ActivitySelection): boolean {
  if (!guid(scope.target) || !guid(selection.snapshotId) || selection.identity !== undefined && !identity(selection.identity) ||
      selection.chain !== undefined && !validActivityChain(selection.chain)) return false;
  try {
    const observed = activityUtc(selection.observedUtc);
    if (scope.range !== "custom") return ["1h", "6h", "24h", "7d"].includes(scope.range);
    const from = activityUtc(scope.from), to = activityUtc(scope.to);
    return from < to && to - from <= 31n * 86400n * 1000000n && observed >= from && observed < to;
  } catch { return false; }
}

/** Undefined is absent; null is malformed, ambiguous, or contradictory to the shared context. */
export function readActivitySelection(hash: string, expectedTargetId?: string): ActivitySelection | null | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const parameters = new URLSearchParams(query);
  if (!parameterNames.some(name => parameters.has(name))) return undefined;
  if (path !== "activity" || parameterNames.some(name => parameters.getAll(name).length > 1) ||
      parameters.getAll("target").length !== 1 || !guid(parameters.get("target")) ||
      expectedTargetId !== undefined && (!guid(expectedTargetId) || parameters.get("target")!.toLowerCase() !== expectedTargetId.toLowerCase()) ||
      parameters.has("at") || parameters.has("event") || parameters.getAll("range").length !== 1 ||
      !["1h", "6h", "24h", "7d", "custom"].includes(parameters.get("range")!) ||
      ["from", "to", "databaseId", "compare"].some(name => parameters.getAll(name).length > 1) ||
      parameters.has("databaseId") && (!/^[1-9]\d{0,4}$/u.test(parameters.get("databaseId")!) || Number(parameters.get("databaseId")) > 32767) ||
      parameters.has("compare") && !["0", "1"].includes(parameters.get("compare")!)) return null;
  const snapshot = parameters.get("sessionSnapshot"), observed = parameters.get("sessionObserved");
  if (!guid(snapshot) || !observed || parameters.has("sessionIdentity") && !identity(parameters.get("sessionIdentity")) ||
      parameters.has("sessionChain") && !validActivityChain(parameters.get("sessionChain"))) return null;
  const selection: ActivitySelection = {
    snapshotId: snapshot.toLowerCase(), observedUtc: observed,
    ...(parameters.has("sessionIdentity") ? { identity: parameters.get("sessionIdentity")!.toLowerCase() } : {}),
    ...(parameters.has("sessionChain") ? { chain: parameters.get("sessionChain")! } : {}),
  };
  if (!validSelection(readOverviewScope(hash), selection)) return null;
  return { ...selection, observedUtc: canonicalUtcTimestamp(normalized(observed)) };
}

/** Keep workspace Live/Rewind mode; only the selected capture is pinned. */
export function activitySelectionHref(scope: OverviewScope, selection?: ActivitySelection): string {
  const href = overviewHref(scope, "activity", scope.target);
  if (selection === undefined) return href;
  if (!validSelection(scope, selection)) throw new Error("Select a retained session snapshot inside the shared context.");
  const parameters = new URLSearchParams(href.split("?")[1]);
  parameters.set("sessionSnapshot", selection.snapshotId.toLowerCase());
  parameters.set("sessionObserved", canonicalUtcTimestamp(normalized(selection.observedUtc)));
  if (selection.identity !== undefined) parameters.set("sessionIdentity", selection.identity.toLowerCase());
  if (selection.chain !== undefined) parameters.set("sessionChain", selection.chain);
  return `#/activity?${parameters}`;
}

export function retainActivitySelectionHref(href: string, currentHash: string): string {
  if (href.split("?")[0] !== "#/activity") return href;
  const parameters = new URLSearchParams(href.split("?")[1]);
  if (parameters.has("at") || parameters.has("event")) return href;
  // Database filtering changes presentation; the capture still covers the same server.
  const scope = readOverviewScope(href);
  const selection = readActivitySelection(currentHash, scope.target);
  if (!selection) return href;
  try { return activitySelectionHref(scope, selection); } catch { return href; }
}

export function activitySelectionExpired(selection: ActivitySelection, now: number): boolean {
  return activityUtc(selection.observedUtc) <= BigInt(Math.trunc(now)) * 1000n - 24n * 3600n * 1000000n;
}

/** Validate capture binding before metadata can restore a request or chain. */
export function validateActivitySelectionPage(page: LivePage, selection: ActivitySelection): void {
  if (!page || !Array.isArray(page.rows) || page.rows.length > 50 || !Array.isArray(page.databases) ||
      typeof page.truncated !== "boolean" || typeof page.state !== "string" ||
      page.nextCursor !== null && (typeof page.nextCursor !== "string" || page.nextCursor.length < 1 || page.nextCursor.length > 2048))
    throw new Error("Session snapshot metadata was invalid.");
  activityUtc(page.repositoryTimeUtc);
  if (page.snapshotId === null && page.observedUtc === null && page.rows.length === 0) return;
  if (!guid(page.snapshotId) || page.snapshotId.toLowerCase() !== selection.snapshotId.toLowerCase() ||
      activityUtc(page.observedUtc) !== activityUtc(selection.observedUtc)) throw new Error("Session snapshot did not match the requested capture and observation time.");
  const identities = new Set<string>();
  for (const row of page.rows) {
    if (!row || !identity(row.identity) || identities.has(row.identity.toLowerCase()) ||
        !Number.isSafeInteger(row.sessionId) || row.sessionId < 1 || row.sessionId > 32767 ||
        row.requestId !== null && (!Number.isSafeInteger(row.requestId) || row.requestId < 0) ||
        row.databaseId !== null && (!Number.isSafeInteger(row.databaseId) || row.databaseId < (row.isUser === false ? 0 : 1) || row.databaseId > 32767))
      throw new Error("Session snapshot contained invalid or duplicate request identities.");
    identities.add(row.identity.toLowerCase());
  }
}

export interface ActivitySelectionLoad {
  readonly page: LivePage;
  readonly row?: LiveRow;
  readonly cursor?: string;
  readonly cursorTrail: readonly (string | undefined)[];
  readonly pagesRead: number;
  readonly limited: boolean;
}
export type ActivitySelectionReader = (parameters: URLSearchParams, signal: AbortSignal) => Promise<LivePage>;
/** Metadata only. Cursor pages stay bound to the same snapshot and server filters. */
export async function loadActivitySelection(selection: ActivitySelection, filters: URLSearchParams,
    reader: ActivitySelectionReader, signal: AbortSignal): Promise<ActivitySelectionLoad> {
  const parameters = new URLSearchParams(filters);
  parameters.delete("cursor"); parameters.set("snapshot", selection.snapshotId);
  const cursorTrail: (string | undefined)[] = [], cursors = new Set<string>(), identities = new Set<string>();
  let cursor: string | undefined, count = 0;
  for (let pagesRead = 1; pagesRead <= 11; pagesRead++) {
    signal.throwIfAborted();
    const page = await reader(new URLSearchParams(parameters), signal);
    signal.throwIfAborted();
    validateActivitySelectionPage(page, selection);
    count += page.rows.length;
    if (count > 512) throw new Error("Session snapshot exceeded its 512-row capture bound.");
    for (const row of page.rows) {
      if (identities.has(row.identity.toLowerCase())) throw new Error("Session snapshot repeated a request across pages.");
      identities.add(row.identity.toLowerCase());
    }
    const row = selection.identity === undefined ? undefined : page.rows.find(item => item.identity.toLowerCase() === selection.identity!.toLowerCase());
    if (row || selection.identity === undefined || !page.nextCursor || pagesRead === 11 || count === 512)
      return { page, row, cursor, cursorTrail, pagesRead, limited: !row && selection.identity !== undefined && Boolean(page.nextCursor) };
    if (cursors.has(page.nextCursor)) throw new Error("Session snapshot cursor repeated.");
    cursors.add(page.nextCursor); cursorTrail.push(cursor); cursor = page.nextCursor; parameters.set("cursor", cursor);
  }
  throw new Error("Session snapshot lookup exceeded its capture bound.");
}
