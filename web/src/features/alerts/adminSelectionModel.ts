import { overviewHref, readOverviewScope } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import type { AdminListPage } from "./alertAdminListsApi.ts";

export type AdminPage = "rules" | "maintenance" | "destinations";
const parameters = { rules: "ruleId", maintenance: "windowId", destinations: "destinationId" } as const;
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";

/** Absent, valid, and invalid selections stay distinct; invalid URLs cannot select a default row. */
export function readAdminSelection(hash: string, page: AdminPage, expectedTargetId?: string): string | null | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const params = new URLSearchParams(query);
  const key = parameters[page];
  if (!params.has(key)) return Object.values(parameters).some(other => params.has(other)) ? null : undefined;
  if (path !== page || params.getAll(key).length !== 1 || !guid(params.get(key)) ||
      params.getAll("target").length !== 1 || !guid(params.get("target")) ||
      expectedTargetId !== undefined && (!guid(expectedTargetId) || params.get("target")!.toLowerCase() !== expectedTargetId.toLowerCase()) ||
      Object.values(parameters).some(other => other !== key && params.has(other))) return null;
  return params.get(key)!.toLowerCase();
}

export function adminSelectionHref(scope: OverviewScope, page: AdminPage, id?: string): string {
  const href = overviewHref(scope, page);
  if (id === undefined) return href;
  if (!guid(scope.target) || !guid(id)) throw new Error("Select an administration item on the current server.");
  return `${href}&${parameters[page]}=${id.toLowerCase()}`;
}

export function retainAdminSelectionHref(href: string, currentHash: string): string {
  const page = href.replace(/^#\/?/u, "").split("?")[0];
  if (page !== "rules" && page !== "maintenance" && page !== "destinations") return href;
  const scope = readOverviewScope(href);
  const selected = readAdminSelection(currentHash, page, scope.target);
  if (!selected) return href;
  const params = new URLSearchParams(href.split("?")[1]);
  params.set(parameters[page], selected);
  return `#/${page}?${params}`;
}

export const adminRestorePageLimit = 5;
export function appendAdminPage<T>(current: AdminListPage<T>, next: AdminListPage<T>, id: (item: T) => string): AdminListPage<T> {
  const seen = new Set(current.items.map(item => id(item).toLowerCase()));
  if (current.targetId.toLowerCase() !== next.targetId.toLowerCase() || current.listingStartedUtc !== next.listingStartedUtc ||
      next.items.some(item => seen.has(id(item).toLowerCase())))
    throw new Error("The listing changed. Refresh to load it again.");
  return { ...next, items: [...current.items, ...next.items] };
}

export function adminSelectionMessage(id: string | null | undefined, found: boolean, pending: boolean, hasMore: boolean): string | undefined {
  if (id === undefined || found) return undefined;
  if (id === null) return "The administration selection in this URL is invalid. Select an item from the list.";
  if (pending) return "Finding the selected administration item…";
  return hasMore
    ? "The selected administration item is not in the loaded pages. Load more to continue searching, or select another item."
    : "The selected administration item is unavailable on this server. It may have been removed or fall outside your access. Select another item.";
}
