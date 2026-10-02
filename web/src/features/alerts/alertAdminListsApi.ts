import { apiFetch } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface AdminListPage<T> {
  readonly targetId: string;
  readonly listingStartedUtc: string;
  readonly items: readonly T[];
  readonly hasMore: boolean;
  readonly nextCursor?: string;
}

export interface MaintenanceAdminItem {
  readonly windowId: string;
  readonly startsAtUtc: string;
  readonly endsAtUtc: string;
  readonly reason: string;
  readonly revision: number;
  readonly cancelledAtUtc: string | null;
}

export interface DestinationAdminItem {
  readonly destinationId: string;
  readonly kind: "https-webhook" | "windows-event-log";
  readonly enabled: boolean;
  readonly approved: boolean;
  readonly revision: number;
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const utc = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;
const invalid = () => new Error("Administration response is invalid.");
const isUtc = (value: unknown): value is string => typeof value === "string" && utc.test(value) && Number.isFinite(Date.parse(value));
const isRevision = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value > 0;
const preciseUtc = (value: string): bigint => {
  try { return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); }
  catch { throw invalid(); }
};

function parsePage<T>(value: unknown, targetId: string, parseItem: (value: unknown) => T, id: (item: T) => string): AdminListPage<T> {
  if (value === null || typeof value !== "object" || Array.isArray(value) || !uuid.test(targetId)) throw invalid();
  const page = value as Record<string, unknown>;
  if (Object.keys(page).sort().join("|") !== "hasMore|items|listingStartedUtc|nextCursor|targetId" ||
      typeof page.targetId !== "string" || page.targetId.toLowerCase() !== targetId.toLowerCase() ||
      !isUtc(page.listingStartedUtc) || !Array.isArray(page.items) || page.items.length > 100 ||
      typeof page.hasMore !== "boolean" ||
      (page.hasMore && (page.items.length === 0 || typeof page.nextCursor !== "string" ||
        page.nextCursor.length === 0 || page.nextCursor.length > 1024 ||
        !/^[A-Za-z0-9+/]+={0,2}$/.test(page.nextCursor))) ||
      (!page.hasMore && page.nextCursor !== null)) throw invalid();
  const seen = new Set<string>();
  const items = page.items.map(value => {
    const item = parseItem(value);
    const key = id(item).toLowerCase();
    if (seen.has(key)) throw invalid();
    seen.add(key);
    return item;
  });
  return { targetId, listingStartedUtc: page.listingStartedUtc, items,
    hasMore: page.hasMore, nextCursor: page.hasMore ? page.nextCursor as string : undefined };
}

export function parseMaintenanceAdminPage(value: unknown, targetId: string): AdminListPage<MaintenanceAdminItem> {
  return parsePage(value, targetId, row => {
    if (row === null || typeof row !== "object" || Array.isArray(row)) throw invalid();
    const item = row as Record<string, unknown>;
    if (Object.keys(item).sort().join("|") !== "cancelledAtUtc|endsAtUtc|reason|revision|startsAtUtc|windowId" ||
        typeof item.windowId !== "string" || !uuid.test(item.windowId) ||
        !isUtc(item.startsAtUtc) || !isUtc(item.endsAtUtc) ||
        preciseUtc(item.endsAtUtc) <= preciseUtc(item.startsAtUtc) ||
        typeof item.reason !== "string" || item.reason.length === 0 || item.reason.length > 512 ||
        !isRevision(item.revision) ||
        item.cancelledAtUtc !== null && !isUtc(item.cancelledAtUtc)) throw invalid();
    return item as unknown as MaintenanceAdminItem;
  }, item => item.windowId);
}

export function parseDestinationAdminPage(value: unknown, targetId: string): AdminListPage<DestinationAdminItem> {
  return parsePage(value, targetId, row => {
    if (row === null || typeof row !== "object" || Array.isArray(row)) throw invalid();
    const item = row as Record<string, unknown>;
    if (Object.keys(item).sort().join("|") !== "approved|destinationId|enabled|kind|revision" ||
        typeof item.destinationId !== "string" || !uuid.test(item.destinationId) ||
        item.kind !== "https-webhook" && item.kind !== "windows-event-log" ||
        typeof item.enabled !== "boolean" || typeof item.approved !== "boolean" ||
        !isRevision(item.revision)) throw invalid();
    return item as unknown as DestinationAdminItem;
  }, item => item.destinationId);
}

async function getPage<T>(targetId: string, resource: "maintenance" | "destinations", signal: AbortSignal,
    parser: (value: unknown, targetId: string) => AdminListPage<T>, cursor?: string): Promise<AdminListPage<T>> {
  if (!uuid.test(targetId) || cursor !== undefined && (cursor.length === 0 || cursor.length > 1024))
    throw new Error("Invalid administration page request.");
  const query = new URLSearchParams({ limit: "100" });
  if (cursor) query.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/${resource}?${query}`, {
    credentials: "same-origin", headers: { Accept: "application/json" }, signal,
  });
  if (!response.ok) throw new Error(response.status === 403
    ? `A ${resource === "maintenance" ? "Target Administrator" : "Security Administrator"} role on this server is required to view ${resource}.`
    : `${resource === "maintenance" ? "Maintenance windows" : "Destinations"} are temporarily unavailable.`);
  return parser(await readJsonBounded(response), targetId);
}

export const getMaintenanceForAdmin = (targetId: string, signal: AbortSignal, cursor?: string) =>
  getPage(targetId, "maintenance", signal, parseMaintenanceAdminPage, cursor);

export const getDestinationsForAdmin = (targetId: string, signal: AbortSignal, cursor?: string) =>
  getPage(targetId, "destinations", signal, parseDestinationAdminPage, cursor);
