const id = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const path = (hash: string) => hash.replace(/^#\/?/u, "").split("?")[0];
const params = (hash: string) => new URLSearchParams(hash.split("?")[1]);

/** Explicit invalid series identities cannot fall back to the newest series. */
export function readMaintenanceSeriesSelection(hash: string, expectedTargetId?: string): string | null | undefined {
  const query = params(hash);
  if (!query.has("seriesId")) return undefined;
  const target = query.get("target"), selected = query.get("seriesId");
  if (path(hash) !== "maintenance" || query.getAll("seriesId").length !== 1 || !id(selected) ||
    query.getAll("target").length !== 1 || !id(target) || expectedTargetId !== undefined &&
    (!id(expectedTargetId) || target.toLowerCase() !== expectedTargetId.toLowerCase())) return null;
  return selected.toLowerCase();
}

export function maintenanceSeriesSelectionHref(hash: string, seriesId: string): string {
  const query = params(hash);
  if (path(hash) !== "maintenance" || !id(seriesId) || query.getAll("target").length !== 1 || !id(query.get("target")))
    throw new Error("Select a maintenance series on the current server.");
  query.set("seriesId", seriesId.toLowerCase());
  return `#/maintenance?${query}`;
}

/** Same-target time/window navigation retains both valid and explicitly invalid series selections. */
export function retainMaintenanceSeriesSelectionHref(href: string, currentHash: string): string {
  if (path(href) !== "maintenance" || path(currentHash) !== "maintenance") return href;
  const next = params(href), current = params(currentHash);
  if (next.getAll("target").length !== 1 || current.getAll("target").length !== 1 ||
    !id(next.get("target")) || !id(current.get("target")) ||
    next.get("target")!.toLowerCase() !== current.get("target")!.toLowerCase() || !current.has("seriesId")) return href;
  next.delete("seriesId");
  for (const selected of current.getAll("seriesId")) next.append("seriesId", selected);
  return `#/maintenance?${next}`;
}
