import type { PlanCacheHistorySelection, QueryPerformanceMetric } from "./queryPerformanceTypes";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";

export type QueryView = "top" | "regressed" | "cache";
export type QueryRankingSource = "mixed" | "query_store" | "plan_cache";
export interface PlanTrendSelection {
  readonly context: string;
  readonly category?: number | null;
  readonly categoryInvalid?: true;
  readonly inspectionUtc?: string;
  readonly inspectionInvalid?: true;
}
export interface QueryViewSelection {
  readonly view: QueryView;
  readonly metric: QueryPerformanceMetric;
  readonly cacheSelection?: PlanCacheHistorySelection;
  /** A loaded-ranking filter, independent of the inspected query's source. */
  readonly sourceFilter?: QueryRankingSource;
  readonly sourceFilterInvalid?: true;
  readonly planTrendSelection?: PlanTrendSelection;
}
const views = new Set(["top", "regressed", "cache"]);
const metrics = new Set(["cpu", "duration", "executions", "logical_reads", "writes", "rows"]);
const sources = new Set(["mixed", "query_store", "plan_cache"]);
const isQueries = (path: string) => path.replace(/^#\/?/u, "") === "queries";
const nativeUtc = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");

/** Bind chart controls to the selected plan and workspace range, independently of ranking filters. */
export function queryPlanTrendContext(hash: string): string | undefined {
  const [path = "", query] = hash.split("?"); const parameters = new URLSearchParams(query);
  const target = parameters.get("target"), database = parameters.get("databaseId"), queryId = parameters.get("queryFingerprint"), plan = parameters.get("planFingerprint");
  if (!isQueries(path) || parameters.get("queryView") === "cache" ||
      ["target", "databaseId", "queryFingerprint", "planFingerprint", "source", "range", "from", "to", "compare"].some(key => parameters.getAll(key).length > 1) ||
      !target || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(target) || target === "00000000-0000-0000-0000-000000000000" ||
      !database || !/^[1-9]\d{0,4}$/u.test(database) || Number(database) > 32767 ||
      ![queryId, plan].every(value => value !== null && /^[0-9a-f]{64}$/iu.test(value)) ||
      parameters.has("source") && parameters.get("source") !== "query_store") return undefined;
  const range = parameters.get("range"); if (!range || !["1h", "6h", "24h", "7d", "custom"].includes(range)) return undefined;
  let from: string | null = null, to: string | null = null;
  if (range === "custom") {
    try {
      const fromTicks = utcMicroseconds(nativeUtc(parameters.get("from") ?? "")), toTicks = utcMicroseconds(nativeUtc(parameters.get("to") ?? ""));
      if (fromTicks >= toTicks || toTicks - fromTicks > 7n * 86400000000n) return undefined;
      from = fromTicks.toString(); to = toTicks.toString();
    } catch { return undefined; }
  } else if (parameters.has("from") || parameters.has("to")) return undefined;
  return JSON.stringify([target.toLowerCase(), Number(database), queryId!.toLowerCase(), plan!.toLowerCase(), range, from, to]);
}

function readPlanTrendSelection(hash: string, parameters: URLSearchParams): PlanTrendSelection | undefined {
  if (!parameters.has("planWaitCategory") && !parameters.has("planInspectUtc")) return undefined;
  const context = queryPlanTrendContext(hash), categories = parameters.getAll("planWaitCategory"), instants = parameters.getAll("planInspectUtc");
  const categoryValid = context !== undefined && categories.length === 1 && (categories[0] === "all" || /^(?:0|[1-9]|[12]\d|3[01])$/u.test(categories[0]!));
  let inspectionUtc: string | undefined;
  if (context !== undefined && instants.length === 1) try {
    const utc = nativeUtc(instants[0]!), time = utcMicroseconds(utc);
    if (parameters.get("range") !== "custom" || time >= utcMicroseconds(nativeUtc(parameters.get("from")!)) && time < utcMicroseconds(nativeUtc(parameters.get("to")!))) inspectionUtc = canonicalUtcTimestamp(utc);
  } catch { /* Malformed selection remains explicit until repaired. */ }
  return { context: context ?? "", ...(categories.length ? categoryValid ? { category: categories[0] === "all" ? null : Number(categories[0]) } : { categoryInvalid: true as const } : {}),
    ...(instants.length ? inspectionUtc === undefined ? { inspectionInvalid: true as const } : { inspectionUtc } : {}) };
}

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
  const planTrendSelection = readPlanTrendSelection(hash, parameters);
  return { ...selection, ...sourceSelection, ...(planTrendSelection ? { planTrendSelection } : {}), view: views.has(view) ? view as QueryView : "top",
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
  const planTrend = selection.planTrendSelection;
  for (const key of ["planWaitCategory", "planInspectUtc"]) parameters.delete(key);
  if (planTrend && queryPlanTrendContext(`${path}?${parameters}`) === planTrend.context) {
    if (planTrend.categoryInvalid) parameters.set("planWaitCategory", "invalid");
    else if (planTrend.category !== undefined) {
      if (planTrend.category !== null && (!Number.isInteger(planTrend.category) || planTrend.category < 0 || planTrend.category > 31)) throw new Error("Plan wait category is invalid.");
      parameters.set("planWaitCategory", planTrend.category === null ? "all" : String(planTrend.category));
    }
    if (planTrend.inspectionInvalid) parameters.set("planInspectUtc", "invalid");
    else if (planTrend.inspectionUtc !== undefined) {
      parameters.set("planInspectUtc", planTrend.inspectionUtc);
      const restored = readPlanTrendSelection(`${path}?${parameters}`, parameters);
      if (restored?.inspectionInvalid) throw new Error("Plan inspection time is outside this context.");
    }
  }
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
