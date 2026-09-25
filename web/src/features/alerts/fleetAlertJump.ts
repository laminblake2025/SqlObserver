import type { FleetAlert, FleetAlertPage } from "./alertTypes";

export interface FleetAlertJumpPage {
  readonly items: readonly FleetAlert[];
  readonly nextCursor?: string;
  readonly found: boolean;
}

export function fleetAlertKey(alert: Pick<FleetAlert, "targetId" | "alertId">): string {
  return `${alert.targetId}:${alert.alertId}`;
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
  for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
    const page = await readPage(cursor);
    for (const item of page.items) {
      const identity = fleetAlertKey(item);
      if (!identities.has(identity)) { items.push(item); identities.add(identity); }
      if (identity === requestedKey) found = true;
    }
    cursor = page.nextCursor;
    if (!requestedKey || found || !cursor) break;
    if (visited.has(cursor)) throw new Error("Alert paging repeated a cursor.");
    visited.add(cursor);
  }
  return { items, nextCursor: cursor, found };
}
