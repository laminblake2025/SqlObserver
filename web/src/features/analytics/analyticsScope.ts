import type { AnalyticsPanelState, AnalyticsSurface } from "./analyticsTypes";

type AnalyticsScopePage = {
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly snapshotUtc?: string;
  readonly cutoffUtc?: string;
};

export function analyticsScopeText(surface: AnalyticsSurface, page: AnalyticsScopePage): string {
  const snapshot = page.snapshotUtc ?? "Unavailable";
  const cutoff = page.cutoffUtc ?? "Unavailable";
  if (surface === "jobs" || surface === "backfill") {
    return `${surface === "backfill" ? "Backfill job inventory" : "Job inventory"}. Records are not filtered by time. Snapshot: ${snapshot}. Cutoff: ${cutoff}.`;
  }
  return `UTC window: ${page.fromUtc} to ${page.toUtc}. Snapshot: ${snapshot}. Cutoff: ${cutoff}.`;
}

export function analyticsEmptyStateText(surface: AnalyticsSurface): string {
  return surface === "jobs" || surface === "backfill"
    ? surface === "backfill" ? "No backfill jobs are available in this response." : "No job inventory rows are available in this response."
    : "No rows are available in this bounded window.";
}

export function analyticsStatusText(
  surface: AnalyticsSurface,
  state: AnalyticsPanelState,
  error: string | null,
  messages: Readonly<Record<AnalyticsPanelState, string>>
): string {
  if (error) return error;
  if (state === "no-data" && (surface === "jobs" || surface === "backfill")) return analyticsEmptyStateText(surface);
  return messages[state];
}
