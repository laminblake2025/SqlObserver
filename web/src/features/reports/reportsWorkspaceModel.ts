import { overviewHref, overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { validateReportWindow, type ReportKind } from "./reportsApi.ts";

export type ReportSelection = { readonly state: "valid"; readonly kind: ReportKind }
  | { readonly state: "invalid"; readonly message: string };
export interface ReportWindow { readonly fromUtc: string; readonly toUtc: string; }

const kinds = new Set<ReportKind>(["instance-health", "performance-window", "incident-evidence", "capacity-readiness"]);
const invalidSelection = "The report type in this URL is invalid. Choose a supported report type.";

export function readReportSelection(hash: string): ReportSelection {
  const values = new URLSearchParams(hash.split("?")[1]).getAll("reportKind");
  if (values.length === 0) return { state: "valid", kind: "instance-health" };
  if (values.length !== 1 || !kinds.has(values[0] as ReportKind)) return { state: "invalid", message: invalidSelection };
  return { state: "valid", kind: values[0] as ReportKind };
}

export function reportSelectionHref(scope: OverviewScope, kind: ReportKind): string {
  if (!kinds.has(kind)) throw new Error("Choose a supported report type.");
  return `${overviewHref(scope, "reports")}&reportKind=${encodeURIComponent(kind)}`;
}

export function retainReportSelectionHref(href: string, currentHash: string): string {
  if (href.split("?")[0] !== "#/reports" || currentHash.split("?")[0] !== "#/reports") return href;
  const parameters = new URLSearchParams(href.split("?")[1]);
  // Explicit destination selections come from the picker and may repair a bad URL.
  if (parameters.has("reportKind")) return href;
  const values = new URLSearchParams(currentHash.split("?")[1]).getAll("reportKind");
  if (values.length === 0) return href;
  for (const value of values) parameters.append("reportKind", value);
  return `#/reports?${parameters}`;
}

/** Snapshot reports deliberately omit the workspace's selected historical window. */
export function resolveReportWindow(scope: OverviewScope, kind: ReportKind, now: number): ReportWindow | undefined {
  if (kind === "instance-health") { validateReportWindow(kind); return undefined; }
  try {
    const window = overviewWindow(scope, now);
    validateReportWindow(kind, window.fromUtc, window.toUtc);
    return window;
  } catch (error) {
    const reason = error instanceof Error ? error.message : "Choose a valid UTC report window.";
    throw new Error(`Update the shared time range to generate this report. ${reason}`);
  }
}

/** Display preferences, comparison/database controls and moving Live clocks do not change an attempt. */
export function reportContextKey(instanceId: string, selection: ReportSelection, scope: OverviewScope): string {
  return JSON.stringify([instanceId.toLowerCase(), selection.state,
    selection.state === "valid" ? selection.kind : selection.message, scope.range, scope.from ?? null, scope.to ?? null]);
}
