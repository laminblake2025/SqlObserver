import type { QueryPerformanceMetric } from "./queryPerformanceTypes";

export type QueryView = "top" | "regressed" | "cache";
export interface QueryViewSelection {
  readonly view: QueryView;
  readonly metric: QueryPerformanceMetric;
}
const views = new Set(["top", "regressed", "cache"]);
const metrics = new Set(["cpu", "duration", "executions", "logical_reads", "writes", "rows"]);
const isQueries = (path: string) => path.replace(/^#\/?/u, "") === "queries";

export function readQueryViewSelection(hash: string): QueryViewSelection {
  const [path = "", query] = hash.split("?");
  if (!isQueries(path)) return { view: "top", metric: "cpu" };
  const parameters = new URLSearchParams(query);
  const view = parameters.get("queryView") ?? "top";
  const metric = parameters.get("queryMetric") ?? "cpu";
  return { view: views.has(view) ? view as QueryView : "top",
    metric: metrics.has(metric) ? metric as QueryPerformanceMetric : "cpu" };
}

/** Preserve the query view when another action rebuilds its target or time URL. */
export function queryViewHref(href: string, selection: QueryViewSelection): string {
  const [path = "", query] = href.split("?");
  if (!isQueries(path)) return href;
  if (!views.has(selection.view) || !metrics.has(selection.metric)) throw new Error("Query view selection is invalid.");
  const parameters = new URLSearchParams(query);
  if (selection.view === "top") parameters.delete("queryView"); else parameters.set("queryView", selection.view);
  if (selection.metric === "cpu") parameters.delete("queryMetric"); else parameters.set("queryMetric", selection.metric);
  return `${path}${parameters.size ? `?${parameters}` : ""}`;
}
