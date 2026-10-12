import type { ActiveAlert, ActiveAlertPage } from "./alertTypes";
import { alertSnapshotKey } from "./fleetAlertJump.ts";

export interface TargetAlertJumpPage {
  readonly items: readonly ActiveAlert[];
  readonly nextCursor?: string;
  readonly found: boolean;
  readonly snapshotUtc?: string;
}

/** Restore one exact alert from at most five current-state pages; never substitute another alert. */
export async function loadTargetAlertJump(
  readPage: (cursor?: string) => Promise<ActiveAlertPage>, targetId: string,
  requestedAlertId: string | undefined, maximumPages = 5,
): Promise<TargetAlertJumpPage> {
  if (!Number.isInteger(maximumPages) || maximumPages < 1 || maximumPages > 5)
    throw new RangeError("Alert jump page limit is invalid.");
  const items: ActiveAlert[] = [];
  const visited = new Set<string>();
  const identities = new Set<string>();
  let cursor: string | undefined;
  let found = false;
  let snapshotUtc: string | undefined;
  const requested = requestedAlertId?.toLowerCase();
  for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
    const page = await readPage(cursor);
    if (page.targetId.toLowerCase() !== targetId.toLowerCase() || !Array.isArray(page.items) || page.items.length > 100)
      throw new Error("Alert page exceeded its target or loaded row bounds.");
    if (pageNumber === 0) snapshotUtc = page.snapshotUtc;
    if (alertSnapshotKey(page.snapshotUtc) !== alertSnapshotKey(snapshotUtc))
      throw new Error("Alert snapshot changed while paging. Refresh the server alerts.");
    for (const item of page.items) {
      const identity = item.alertId.toLowerCase();
      if (!identities.has(identity)) { items.push(item); identities.add(identity); }
      if (identity === requested) found = true;
    }
    cursor = page.nextCursor;
    if (cursor !== undefined && (typeof cursor !== "string" || cursor.length === 0 || cursor.length > 1024))
      throw new Error("Alert paging returned an invalid cursor.");
    if (!requested || found || !cursor) break;
    if (visited.has(cursor)) throw new Error("Alert paging repeated a cursor.");
    visited.add(cursor);
  }
  return { items, nextCursor: cursor, found, snapshotUtc };
}
