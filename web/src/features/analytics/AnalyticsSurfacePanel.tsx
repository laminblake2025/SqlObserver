import { useEffect, useState } from "react";
import { getAnalyticsSurface } from "./analyticsApi";
import { panelStateForSurface } from "./analyticsState";
import type { AnalyticsPanelState, AnalyticsSurface, AnalyticsSurfacePage } from "./analyticsTypes";

const messages: Record<AnalyticsPanelState, string> = {
  loading: "Loading…", ready: "", "no-data": "No data is available for this window.", degraded: "Data is degraded; some sources were unavailable.", "permission-denied": "You are not authorized to view this surface.", unsupported: "This surface is unsupported for the target.", stale: "This snapshot is stale; refresh to retry.", partial: "Only partial evidence is available.", "low-confidence": "Evidence confidence is low.", backfilling: "Backfill is in progress.", "cursor-invalid": "The page cursor is invalid; restart paging.", "retention-blocked": "This data is blocked by retention policy."
};

export function AnalyticsSurfacePanel({ targetId, surface }: { targetId: string; surface: AnalyticsSurface }) {
  const [state, setState] = useState<AnalyticsPanelState>("loading");
  const [page, setPage] = useState<AnalyticsSurfacePage | null>(null);
  useEffect(() => { const controller = new AbortController(); setState("loading"); setPage(null); getAnalyticsSurface(targetId, surface, controller.signal).then(value => { setPage(value); setState(panelStateForSurface(value.state, value.items.length > 0)); }).catch(error => { if (!controller.signal.aborted) { const status = error?.status; setState(status === 403 ? "permission-denied" : status === 404 ? "no-data" : status === 400 || status === 422 ? "cursor-invalid" : status === 409 ? "stale" : status === 410 ? "retention-blocked" : status === 202 ? "backfilling" : "degraded"); } }); return () => controller.abort(); }, [targetId, surface]);
  if (state !== "ready" && state !== "partial" && state !== "degraded") return <section className="status-message">{messages[state]}</section>;
  if (!page || page.items.length === 0) return <section className="empty-state">{messages["no-data"]}</section>;
  return <section className="target-card"><h2>{surface}</h2><p>{messages[state] || `${page.items.length} bounded records`}</p></section>;
}
