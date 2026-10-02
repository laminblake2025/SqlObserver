import type { PlanCacheHistorySelection, QueryPerformanceMetric } from "./queryPerformanceTypes";

export type QueryView = "top" | "regressed" | "cache";
export type QueryRankingSource = "mixed" | "query_store" | "plan_cache";
export interface QueryViewSelection {
  readonly view: QueryView;
  readonly metric: QueryPerformanceMetric;
  readonly cacheSelection?: PlanCacheHistorySelection;
  /** A loaded-ranking filter, independent of the inspected query's source. */
  readonly sourceFilter?: QueryRankingSource;
  readonly sourceFilterInvalid?: true;
}
const views = new Set(["top", "regressed", "cache"]);
const metrics = new Set(["cpu", "duration", "executions", "logical_reads", "writes", "rows"]);
const sources = new Set(["mixed", "query_store", "plan_cache"]);
const isQueries = (path: string) => path.replace(/^#\/?/u, "") === "queries";

export function readQueryViewSelection(hash: string): QueryViewSelection {
  const [path = "", query] = hash.split("?");
  if (!isQueries(path)) return { view: "top", metric: "cpu" };
  const parameters = new URLSearchParams(query);
  const sourceValues = parameters.getAll("querySource");
  const sourceSelection = sourceValues.length === 0 ? {} : sourceValues.length !== 1 || !sources.has(sourceValues[0]!)
    ? { sourceFilterInvalid: true as const }
    : sourceValues[0] === "mixed" ? {} : { sourceFilter: sourceValues[0] as QueryRankingSource };
  const view = parameters.get("queryView") ?? "top";
  const metric = parameters.get("queryMetric") ?? "cpu";
  const targetId = parameters.get("target");
  const databaseId = Number(parameters.get("cacheDatabaseId"));
  const databaseIncarnation = parameters.get("cacheIncarnation");
  const queryFingerprint = parameters.get("cacheQuery"); const planFingerprint = parameters.get("cachePlan");
  const validDigest = (value: string | null): value is string => value !== null && /^[0-9a-f]{64}$/u.test(value);
  const selection = view === "cache" && targetId && Number.isInteger(databaseId) && databaseId >= 5 && databaseId <= 32766
    && validDigest(databaseIncarnation) && validDigest(queryFingerprint) && validDigest(planFingerprint)
    ? { cacheSelection: { targetId, databaseId, databaseIncarnation, queryFingerprint, planFingerprint } } : {};
  return { ...selection, ...sourceSelection, view: views.has(view) ? view as QueryView : "top",
    metric: metrics.has(metric) ? metric as QueryPerformanceMetric : "cpu" };
}

/** Preserve the query view when another action rebuilds its target or time URL. */
export function queryViewHref(href: string, selection: QueryViewSelection): string {
  const [path = "", query] = href.split("?");
  if (!isQueries(path)) return href;
  if (!views.has(selection.view) || !metrics.has(selection.metric)) throw new Error("Query view selection is invalid.");
  if (selection.sourceFilter !== undefined && !sources.has(selection.sourceFilter)
      || selection.sourceFilterInvalid !== undefined && selection.sourceFilterInvalid !== true
      || selection.sourceFilterInvalid && selection.sourceFilter !== undefined)
    throw new Error("Query ranking source filter is invalid.");
  const parameters = new URLSearchParams(query);
  if (selection.sourceFilterInvalid) {
    // Preserve an invalid selection through unrelated URL edits; only an explicit
    // source choice repairs it. Rebuilt URLs may no longer contain its raw value.
    const existing = parameters.getAll("querySource");
    if (existing.length === 0 || existing.length === 1 && sources.has(existing[0]!)) parameters.set("querySource", "invalid");
  } else if (!selection.sourceFilter || selection.sourceFilter === "mixed") parameters.delete("querySource");
  else parameters.set("querySource", selection.sourceFilter);
  if (selection.view === "top") parameters.delete("queryView"); else parameters.set("queryView", selection.view);
  if (selection.metric === "cpu") parameters.delete("queryMetric"); else parameters.set("queryMetric", selection.metric);
  const cache = selection.view === "cache" && selection.cacheSelection?.targetId === parameters.get("target") ? selection.cacheSelection : undefined;
  for (const key of ["cacheDatabaseId", "cacheIncarnation", "cacheQuery", "cachePlan"]) parameters.delete(key);
  if (cache) {
    if (!Number.isInteger(cache.databaseId) || cache.databaseId < 5 || cache.databaseId > 32766
        || ![cache.databaseIncarnation, cache.queryFingerprint, cache.planFingerprint].every(value => /^[0-9a-f]{64}$/u.test(value)))
      throw new Error("Cache history selection is invalid.");
    parameters.set("cacheDatabaseId", String(cache.databaseId)); parameters.set("cacheIncarnation", cache.databaseIncarnation);
    parameters.set("cacheQuery", cache.queryFingerprint); parameters.set("cachePlan", cache.planFingerprint);
  }
  return `${path}${parameters.size ? `?${parameters}` : ""}`;
}
