import { canonicalUtcTimestamp, utcMicroseconds } from "../utcTimestamp.ts";

const nativeInspectionTime = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
const inspectionScreens = new Set(["overview", "health", "queries", "waits"]);

/** Inspection keeps native microseconds; drawing coordinates remain approximate. */
export function chartInspectionUtc(value: string): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6}|\.\d{6}0)?Z$/u.test(value))
    throw new Error("Enter a valid UTC inspection time ending in Z, with at most six fractional digits.");
  return canonicalUtcTimestamp(value.replace(/(\.\d{6})0Z$/u, "$1Z"));
}
export function chartInspectionInWindow(value: string | null | undefined, fromUtc?: string, toUtc?: string): boolean {
  if (!value || !fromUtc || !toUtc) return false;
  try {
    const time = nativeInspectionTime(value);
    return time >= nativeInspectionTime(fromUtc) && time < nativeInspectionTime(toUtc);
  } catch { return false; }
}
/** Undefined is absent; null is an explicit malformed/ambiguous URL selection. */
export function readChartInspection(hash: string): string | null | undefined {
  const [page, query] = hash.replace(/^#\/?/u, "").split("?");
  if (!inspectionScreens.has(page ?? "")) return undefined;
  const parameters = new URLSearchParams(query), values = parameters.getAll("chartAt");
  if (!values.length) return undefined;
  if (values.length !== 1 || ["target", "range", "from", "to", "databaseId", "compare"].some(key => parameters.getAll(key).length > 1)) return null;
  const target = parameters.get("target");
  if (target !== null && (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(target) || /^0{8}(?:-0{4}){3}-0{12}$/u.test(target)) || page !== "overview" && target === null) return null;
  if (parameters.has("range") && !["1h", "6h", "24h", "7d", "custom"].includes(parameters.get("range")!)) return null;
  if (parameters.has("compare") && !["0", "1"].includes(parameters.get("compare")!)) return null;
  if (parameters.has("databaseId") && (!target || !/^[1-9]\d{0,4}$/u.test(parameters.get("databaseId")!) || Number(parameters.get("databaseId")) > 32767)) return null;
  if (parameters.get("range") === "custom") {
    try {
      const from = nativeInspectionTime(parameters.get("from") ?? ""), to = nativeInspectionTime(parameters.get("to") ?? "");
      if (from >= to || to - from > 31n * 86400n * 1000000n) return null;
    } catch { return null; }
  }
  try { return chartInspectionUtc(values[0]!); } catch { return null; }
}
/** Preserve the existing Live/Rewind window, database, comparison and event selection. */
export function chartInspectionHref(hash: string, value?: string): string {
  const [page, query] = hash.replace(/^#\/?/u, "").split("?");
  if (!inspectionScreens.has(page ?? "")) throw new Error("Chart alignment is unavailable on this screen.");
  const parameters = new URLSearchParams(query);
  if (value === undefined) parameters.delete("chartAt");
  else parameters.set("chartAt", chartInspectionUtc(value));
  return `#/${page}?${parameters}`;
}

/** Carry an inspection through same-server/window navigation, without repairing invalid input. */
export function retainChartInspectionHref(href: string, currentHash: string): string {
  const [nextPage, nextQuery] = href.replace(/^#\/?/u, "").split("?");
  const [currentPage, currentQuery] = currentHash.replace(/^#\/?/u, "").split("?");
  if (!inspectionScreens.has(nextPage ?? "") || !inspectionScreens.has(currentPage ?? "")) return href;
  const next = new URLSearchParams(nextQuery), current = new URLSearchParams(currentQuery);
  if (next.has("chartAt") || !current.has("chartAt")) return href;
  // Ambiguous context must never transfer a selection to a different server/window.
  for (const key of ["target", "range", "from", "to"]) {
    if (next.getAll(key).length > 1 || current.getAll(key).length > 1) return href;
    const a = next.get(key), b = current.get(key);
    if (key === "target") {
      if (!a || !b || a.toLowerCase() !== b.toLowerCase() || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(a) || /^0{8}(?:-0{4}){3}-0{12}$/u.test(a)) return href;
    } else if ((key === "from" || key === "to") && a && b) {
      try { if (nativeInspectionTime(a) !== nativeInspectionTime(b)) return href; } catch { return href; }
    } else if (a !== b) return href;
  }
  for (const value of current.getAll("chartAt")) next.append("chartAt", value);
  return `#/${nextPage}?${next}`;
}
