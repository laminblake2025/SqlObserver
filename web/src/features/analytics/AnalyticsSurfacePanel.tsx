import { EvidenceTable } from "../../components/EvidenceTable";
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
  const [paging, setPaging] = useState<{cursor?: string; fromUtc?: string; toUtc?: string}>({});
  useEffect(() => { const controller = new AbortController(); setState("loading"); setPage(null); getAnalyticsSurface(targetId, surface, controller.signal, paging).then(value => { if (controller.signal.aborted) return; setPage(value); setState(panelStateForSurface(value.state, value.items.length > 0)); }).catch(error => { if (!controller.signal.aborted) { const status = error?.status; setState(status === 403 ? "permission-denied" : status === 404 ? "no-data" : status === 400 || status === 422 ? "cursor-invalid" : status === 409 ? "stale" : status === 410 ? "retention-blocked" : status === 202 ? "backfilling" : "degraded"); } }); return () => controller.abort(); }, [targetId, surface, paging]);
  if (state !== "ready" && state !== "partial" && state !== "degraded") return <section className="status-message">{messages[state]}</section>;
  if (!page) return <section className="status-message">{messages[state]}</section>;
  if (page.items.length === 0) return <section className="empty-state">{messages[state]} No rows are available in this bounded window.</section>;
  const rows = surface.startsWith("replication/") ? page.items.map(item => ({
    "Observed (UTC)": new Date(String(item.observedAtUtc)).toISOString(),
    "Agent state": item.synchronizationState === "disabled" ? "Stopped" : item.synchronizationState,
    "Pending commands": item.pendingCommands,
    "Latency (seconds)": item.latencySeconds,
    "Coverage": item.coverage,
    "Evidence": {runId:item.runId, role:item.role, targetRevision:item.targetRevision, visibilityGap:item.visibilityGap}
  })) : page.items;
  return <section className="panel"><h2>{surface.replaceAll("/", " · ")}</h2><p>{page.state === "visibility_gap" ? "This history includes observations collected without a distribution database binding. Health and queue values are unavailable for those rows." : messages[state]}</p><EvidenceTable label={surface} rows={rows}/><p>UTC window: {page.fromUtc} to {page.toUtc}. Snapshot: {page.snapshotUtc}. Cutoff: {page.cutoffUtc}.</p><div className="toolbar"><button disabled={!paging.cursor} onClick={() => setPaging({})}>First page</button><button disabled={!page.nextCursor} onClick={() => setPaging({cursor:page.nextCursor ?? undefined,fromUtc:page.fromUtc,toUtc:page.toUtc})}>Next evidence page</button></div></section>;
}
