import { analyticsUtcTicks, decodeAnalyticsSurfaceCursor, sameAnalyticsUtc } from "./analyticsParser.ts";
import type { AnalyticsSurface, AnalyticsSurfacePage } from "./analyticsTypes.ts";

export const maximumAnalyticsPages = 20;
export const maximumAnalyticsRows = 4_000;
export interface AnalyticsWindow { readonly fromUtc: string; readonly toUtc: string; }
export interface AnalyticsReceipt {
  readonly page: AnalyticsSurfacePage;
  readonly requestedCursor?: string;
  readonly receivedAtUtc: string;
}

export function analyticsInventory(surface: AnalyticsSurface): boolean {
  return surface === "jobs" || surface === "backfill";
}
export function analyticsRetainedRowCount(receipts: readonly AnalyticsReceipt[]): number {
  return receipts.reduce((total, receipt) => total + receipt.page.items.length, 0);
}
function canonical(value: unknown): string {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return JSON.stringify(Object.fromEntries(Object.entries(value).sort(([left], [right]) => left.localeCompare(right))));
  }
  return JSON.stringify(value);
}
function identity(surface: AnalyticsSurface, row: Record<string, unknown>): string | undefined {
  switch (surface) {
    case "host/status": return String(row.hostId).toLowerCase();
    case "host/metrics": return `${analyticsUtcTicks(String(row.observedAtUtc))}:${String(row.runId).toLowerCase()}:${row.metricKey}:${canonical(row.dimensions)}`;
    case "diagnostics/search": return String(row.eventId).toLowerCase();
    case "jobs": case "backfill": return String(row.jobId).toLowerCase();
    case "incidents": return String(row.threadId).toLowerCase();
    case "evidence-packets": return String(row.packetId).toLowerCase();
    // The native replication tie includes a topology fingerprint which is not exposed on
    // every row. Equal visible rows can be distinct; do not invent their identities.
    case "replication/status": case "replication/evidence": return undefined;
  }
}
function boundaryTie(surface: AnalyticsSurface, row: Record<string, unknown>): { at: bigint; id: string } {
  const atKey = analyticsInventory(surface) ? "requestedAtUtc" : surface === "incidents" ? "openedAtUtc"
    : surface === "diagnostics/search" || surface === "evidence-packets" ? "occurredAtUtc" : "observedAtUtc";
  const idKey = analyticsInventory(surface) ? "jobId" : surface === "incidents" ? "threadId"
    : surface === "host/status" ? "hostId" : surface === "diagnostics/search" ? "eventId"
    : surface === "evidence-packets" ? "packetId" : "runId";
  return { at: analyticsUtcTicks(String(row[atKey])), id: String(row[idKey]).toLowerCase() };
}
export function validateAnalyticsReceipt(
  page: AnalyticsSurfacePage, surface: AnalyticsSurface, targetId: string, targetRevision: number | undefined,
  requestedWindow: AnalyticsWindow | undefined, receipts: readonly AnalyticsReceipt[], requestedCursor?: string,
): void {
  if (page.targetId.toLowerCase() !== targetId.toLowerCase() || page.surface !== surface
      || targetRevision !== undefined && page.targetRevision !== undefined && page.targetRevision !== targetRevision) {
    throw new Error("Analytics evidence does not match this target or revision.");
  }
  if (requestedWindow && (!sameAnalyticsUtc(page.fromUtc, requestedWindow.fromUtc)
      || !sameAnalyticsUtc(page.toUtc, requestedWindow.toUtc))) {
    throw new Error("Analytics evidence does not match the requested window.");
  }
  if (surface === "incidents" && (requestedCursor || page.nextCursor)) {
    throw new Error("The native incident list does not provide continuation.");
  }
  if (requestedCursor) {
    const first = receipts[0]?.page;
    const previous = receipts.at(-1)?.page;
    const cursor = decodeAnalyticsSurfaceCursor(requestedCursor);
    if (!first || previous?.nextCursor !== requestedCursor || cursor.surface !== surface
        || cursor.targetId.toLowerCase() !== targetId.toLowerCase()
        || cursor.targetRevision !== first.targetRevision || cursor.generation !== first.generation
        || !first.snapshotUtc || !first.cutoffUtc || !page.snapshotUtc || !page.cutoffUtc
        || !sameAnalyticsUtc(cursor.snapshotUtc, first.snapshotUtc)
        || !sameAnalyticsUtc(cursor.sourceCutoffUtc, first.cutoffUtc)
        || !sameAnalyticsUtc(cursor.fromUtc, first.fromUtc) || !sameAnalyticsUtc(cursor.toUtc, first.toUtc)
        || page.targetRevision !== first.targetRevision || page.generation !== first.generation
        || !sameAnalyticsUtc(page.snapshotUtc, first.snapshotUtc) || !sameAnalyticsUtc(page.cutoffUtc, first.cutoffUtc)
        || !sameAnalyticsUtc(page.fromUtc, first.fromUtc) || !sameAnalyticsUtc(page.toUtc, first.toUtc)) {
      throw new Error("Analytics continuation changed its target, generation, snapshot, cutoff or window. Received pages remain available.");
    }
    const previousTail = previous?.items.at(-1);
    const nextHead = page.items[0];
    if (previousTail && nextHead) {
      const before = boundaryTie(surface, previousTail), after = boundaryTie(surface, nextHead);
      if ((surface === "host/status" ? before.at < after.at : before.at > after.at)
          || before.at === after.at && before.id > after.id
          || surface === "host/metrics" && before.at === after.at && before.id === after.id
            && String(previousTail.metricKey) > String(nextHead.metricKey)) {
        throw new Error("Analytics continuation is outside its native ordering boundary. Received pages remain available.");
      }
    }
  }
  if (page.nextCursor && (page.nextCursor === requestedCursor
      || receipts.some(receipt => receipt.requestedCursor === page.nextCursor))) {
    throw new Error("Analytics continuation repeated a received cursor. Received pages remain available.");
  }
  const seen = new Set<string>();
  for (const receipt of receipts) for (const row of receipt.page.items) {
    const key = identity(surface, row); if (key !== undefined) seen.add(key);
  }
  for (const row of page.items) {
    const key = identity(surface, row);
    if (key !== undefined && seen.has(key)) throw new Error("Analytics continuation repeated received evidence. Received pages remain available.");
    if (key !== undefined) seen.add(key);
  }
  if (receipts.length + 1 > maximumAnalyticsPages || analyticsRetainedRowCount(receipts) + page.items.length > maximumAnalyticsRows) {
    throw new Error("Retained analytics limit reached: 20 pages / 4,000 rows. Return to latest to continue.");
  }
}
