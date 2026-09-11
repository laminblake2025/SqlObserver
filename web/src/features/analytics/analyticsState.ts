import type { AnalyticsPanelState, AnalyticsSurfaceState } from "./analyticsTypes";
export interface AnalyticsState<T> { readonly state: AnalyticsPanelState; readonly data: T | null; readonly error: string | null; }
export const loading = <T>(): AnalyticsState<T> => ({ state: "loading", data: null, error: null });
export function panelStateForRequestError(status: number | undefined, hasCursor: boolean): AnalyticsPanelState { return status === 403 ? "permission-denied" : status === 404 ? "no-data" : status === 409 ? "stale" : status === 400 || status === 422 ? hasCursor ? "cursor-invalid" : "degraded" : status === 410 ? "retention-blocked" : status === 202 ? "backfilling" : "degraded"; }
export function stateForError<T>(status: number, error: string): AnalyticsState<T> { const state: AnalyticsPanelState = status === 403 ? "permission-denied" : status === 404 ? "no-data" : status === 409 ? "stale" : status === 422 || status === 400 ? "cursor-invalid" : status === 410 ? "retention-blocked" : status === 202 ? "backfilling" : "degraded"; return { state, data: null, error }; }
export function panelStateForSurface(state: AnalyticsSurfaceState, hasData: boolean): AnalyticsPanelState {
  if (state === "permission_denied") return "permission-denied";
  if (state === "unsupported") return "unsupported";
  if (state === "no_data" || !hasData) return "no-data";
  if (state === "visibility_gap") return "degraded";
  if (state === "stale") return "stale";
  if (state === "backfilling") return "backfilling";
  if (state === "retention_blocked") return "retention-blocked";
  return state === "partial" || state === "unavailable" ? "partial" : "ready";
}
