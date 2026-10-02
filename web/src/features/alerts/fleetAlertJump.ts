import type { FleetAlert, FleetAlertPage } from "./alertTypes";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface FleetAlertJumpPage {
  readonly items: readonly FleetAlert[];
  readonly nextCursor?: string;
  readonly found: boolean;
  readonly snapshotUtc?: string;
}

export function fleetAlertKey(alert: Pick<FleetAlert, "targetId" | "alertId">): string {
  return `${alert.targetId.toLowerCase()}:${alert.alertId.toLowerCase()}`;
}

export function alertSnapshotKey(value: string | undefined): bigint | undefined {
  return value === undefined ? undefined : utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
}

export async function loadFleetAlertJump(
  readPage: (cursor?: string) => Promise<FleetAlertPage>,
  requestedKey: string | undefined,
  maximumPages = 5,
): Promise<FleetAlertJumpPage> {
  if (!Number.isInteger(maximumPages) || maximumPages < 1 || maximumPages > 5)
    throw new RangeError("Alert jump page limit is invalid.");
  const items: FleetAlert[] = [];
  const visited = new Set<string>();
  const identities = new Set<string>();
  let cursor: string | undefined;
  let found = false;
  let snapshotUtc: string | undefined;
  const normalizedKey = requestedKey?.toLowerCase();
  for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
    const page = await readPage(cursor);
    if (!Array.isArray(page.items) || page.items.length > 100)
      throw new Error("Alert page exceeded its loaded row limit.");
    if (pageNumber === 0) snapshotUtc = page.snapshotUtc;
    if (alertSnapshotKey(page.snapshotUtc) !== alertSnapshotKey(snapshotUtc))
      throw new Error("Alert snapshot changed while paging. Refresh the inbox.");
    for (const item of page.items) {
      const identity = fleetAlertKey(item);
      if (!identities.has(identity)) { items.push(item); identities.add(identity); }
      if (identity === normalizedKey) found = true;
    }
    cursor = page.nextCursor;
    if (cursor !== undefined && (typeof cursor !== "string" || cursor.length === 0 || cursor.length > 1024))
      throw new Error("Alert paging returned an invalid cursor.");
    if (!requestedKey || found || !cursor) break;
    if (visited.has(cursor)) throw new Error("Alert paging repeated a cursor.");
    visited.add(cursor);
  }
  return { items, nextCursor: cursor, found, snapshotUtc };
}
