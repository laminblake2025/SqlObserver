import { activityUtc, validateActivitySelectionPage } from "./activitySelectionModel.ts";
import { validateLivePage, type LivePage, type LiveRow, type LiveSnapshot } from "./liveActivityApi.ts";
import { activityAccessDenied } from "./activityAccess.ts";

/** A UI collection, never a wire page. Every native page retains its 50-row bound. */
export interface ActivityCapture extends Omit<LivePage, "nextCursor"> {
  nextCursor: null;
  pagesRead: number;
  limited: boolean;
  partial: boolean;
  coverageMessage?: string;
}
export const inclusiveActivityFilters = () => new URLSearchParams({ includeIdle: "true", includeSystem: "true", blockedOnly: "false", sort: "session", descending: "false" });
export async function loadActivityCapture(reader: (parameters: URLSearchParams, signal: AbortSignal) => Promise<LivePage>, signal: AbortSignal, snapshot?: Pick<LiveSnapshot, "id" | "observedUtc">): Promise<ActivityCapture> {
  const parameters = inclusiveActivityFilters();
  if (snapshot) parameters.set("snapshot", snapshot.id);
  let first: LivePage | undefined, binding = snapshot ? { snapshotId: snapshot.id, observedUtc: snapshot.observedUtc } : undefined;
  const rows: LiveRow[] = [], identities = new Set<string>(), cursors = new Set<string>();
  let pagesRead = 0;
  for (let count = 0; count < 11; count++) {
    signal.throwIfAborted();
    let page: LivePage;
    try { page = await reader(new URLSearchParams(parameters), signal); }
    catch (error) {
      signal.throwIfAborted();
      if (activityAccessDenied(error) || !first) throw error;
      return { ...first, rows, nextCursor: null, pagesRead, limited: true, partial: true, coverageMessage: "Metadata paging failed. Ranking and counts cover only the validated rows received. Retry this capture." };
    }
    signal.throwIfAborted(); validateLivePage(page, parameters);
    if (!["available", "stale", "unavailable"].includes(page.state)) throw new Error("Unknown session capture state.");
    if (page.snapshotId === null) {
      if (page.state !== "unavailable") throw new Error("Unavailable capture identity has an inconsistent evidence state.");
      if (first) throw new Error("Session capture expired while paging. No current capture was substituted.");
      return { ...page, nextCursor: null, pagesRead: 1, limited: false, partial: false };
    }
    binding ??= { snapshotId: page.snapshotId, observedUtc: page.observedUtc! };
    validateActivitySelectionPage(page, binding);
    if (first && page.truncated !== first.truncated || page.state === "unavailable") throw new Error("Capture evidence changed while paging.");
    if (activityUtc(page.repositoryTimeUtc) - activityUtc(page.observedUtc) >= 86400n * 1000000n) throw new Error("Session capture is outside retention.");
    if (!first) first = page;
    else {
      const databases = new Map(first.databases.map(database => [database.id, database]));
      for (const database of page.databases) databases.set(database.id, database);
      first = { ...first, databases: [...databases.values()].sort((a, b) => a.id - b.id) };
    }
    pagesRead++;
    if (page.state === "stale") first = { ...first, state: "stale" };
    for (const row of page.rows) {
      if (identities.has(row.identity.toLowerCase()) || rows.length === 512) throw new Error("Session capture repeated a request or exceeded its 512-row bound.");
      identities.add(row.identity.toLowerCase()); rows.push(row);
    }
    if (!page.nextCursor) return { ...first, rows, nextCursor: null, pagesRead, limited: false, partial: false };
    if (!page.rows.length || cursors.has(page.nextCursor) || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(page.nextCursor)) throw new Error("Session capture cursor is invalid or repeated.");
    cursors.add(page.nextCursor);
    parameters.set("snapshot", binding.snapshotId); parameters.set("cursor", page.nextCursor);
    if (rows.length === 512 || pagesRead === 11) return { ...first, rows, nextCursor: null, pagesRead, limited: true, partial: true, coverageMessage: "Metadata stopped at the 11-page / 512-row bound. Additional evidence may be omitted." };
  }
  throw new Error("Session capture exceeded its page bound.");
}
