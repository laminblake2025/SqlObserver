import { EvidenceTable } from "../../components/EvidenceTable";
import { useEffect, useState } from "react";
import { AnalyticsRequestError, getAnalyticsSurface } from "./analyticsApi";
import { analyticsEmptyStateText, analyticsScopeText, analyticsStatusText } from "./analyticsScope";
import { panelStateForRequestError, panelStateForSurface } from "./analyticsState";
import { analyticsSurfaceLabel } from "./analyticsSurfaceCatalog";
import type { AnalyticsPanelState, AnalyticsSurface, AnalyticsSurfacePage } from "./analyticsTypes";

const messages: Record<AnalyticsPanelState, string> = {
  loading: "Loading…", ready: "", "no-data": "No data is available for this window.", degraded: "Data is degraded; some sources were unavailable.", "permission-denied": "You are not authorized to view this surface.", unsupported: "This surface is unsupported for the target.", stale: "This snapshot is stale; refresh to retry.", partial: "Only partial evidence is available.", "low-confidence": "Evidence confidence is low.", backfilling: "Backfill is in progress.", "cursor-invalid": "The page cursor is invalid; restart paging to load the first page.", "retention-blocked": "This data is blocked by retention policy."
};

type Paging = { cursor?: string; fromUtc?: string; toUtc?: string };

function statusOf(error: unknown): number | undefined {
  return error instanceof AnalyticsRequestError ? error.status : undefined;
}

function messageFor(error: unknown, status: number | undefined, hasCursor: boolean): string | null {
  if (status === 400 || status === 422) return hasCursor ? messages["cursor-invalid"] : "The analytics request is invalid. Retry the request.";
  if (error instanceof AnalyticsRequestError) return error.message;
  if (error instanceof Error) return "The analytics response could not be validated. Retry the request.";
  return "The analytics request failed. Retry the request.";
}

function canRetry(state: AnalyticsPanelState): boolean {
  return state === "degraded" || state === "stale" || state === "backfilling" || state === "cursor-invalid";
}

export function AnalyticsSurfacePanel({ targetId, surface, timeWindow }: { targetId: string; surface: AnalyticsSurface; timeWindow?: {fromUtc:string;toUtc:string} }) {
  const [state, setState] = useState<AnalyticsPanelState>("loading");
  const [page, setPage] = useState<AnalyticsSurfacePage | null>(null);
  const [paging, setPaging] = useState<Paging>({});
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setState("loading"); setPage(null); setError(null);
    getAnalyticsSurface(targetId, surface, controller.signal, {...timeWindow,...paging}).then(value => {
      if (controller.signal.aborted) return;
      setPage(value); setState(panelStateForSurface(value.state, value.items.length > 0));
    }).catch((failure: unknown) => {
      if (controller.signal.aborted) return;
      const status = statusOf(failure); const hasCursor = paging.cursor !== undefined;
      setState(panelStateForRequestError(status, hasCursor)); setError(messageFor(failure, status, hasCursor));
    });
    return () => controller.abort();
  }, [targetId, surface, paging, reload, timeWindow?.fromUtc, timeWindow?.toUtc]);

  const retry = () => { setState("loading"); setPage(null); setError(null); setReload(value => value + 1); };
  const restartPaging = () => {
    const hasPaging = paging.cursor !== undefined || paging.fromUtc !== undefined || paging.toUtc !== undefined;
    setState("loading"); setPage(null); setError(null); setPaging({});
    if (!hasPaging) setReload(value => value + 1);
  };
  const actions = canRetry(state) ? <div className="toolbar">
    {state === "cursor-invalid" && paging.cursor !== undefined ? <button type="button" onClick={restartPaging}>Restart paging</button> : null}
    {state !== "cursor-invalid" ? <button type="button" onClick={retry}>{state === "stale" ? "Refresh snapshot" : state === "backfilling" ? "Check again" : "Retry"}</button> : null}
  </div> : null;
  const statusText = analyticsStatusText(surface, state, error, messages);

  if (state !== "ready" && state !== "partial" && state !== "degraded") return <section className="panel analytics-surface"><p role={error ? "alert" : "status"} className="status-message">{statusText}</p>{actions}</section>;
  if (!page) return <section className="panel analytics-surface"><p role={error ? "alert" : "status"} className="status-message">{statusText}</p>{actions}</section>;
  if (page.items.length === 0) return <section className="panel analytics-surface"><p className="empty-state">{surface === "jobs" || surface === "backfill" ? analyticsEmptyStateText(surface) : `${messages[state]} ${analyticsEmptyStateText(surface)}`}</p></section>;
  const rows = surface.startsWith("replication/") ? page.items.map(item => ({
    "Observed (UTC)": new Date(String(item.observedAtUtc)).toISOString(),
    "Agent state": item.synchronizationState === "disabled" ? "Stopped" : item.synchronizationState,
    "Pending commands": item.pendingCommands,
    "Latency (seconds)": item.latencySeconds,
    "Coverage": item.coverage,
    "Evidence": { runId: item.runId, role: item.role, targetRevision: item.targetRevision, visibilityGap: item.visibilityGap }
  })) : page.items;
  const surfaceLabel = analyticsSurfaceLabel(surface);
  return <section className="panel analytics-surface"><h2>{surfaceLabel}</h2><p>{page.state === "visibility_gap" ? "This history includes observations collected without a distribution database binding. Health and queue values are unavailable for those rows." : messages[state]}</p><EvidenceTable label={surfaceLabel} rows={rows}/><p>{analyticsScopeText(surface, page)}</p><div className="toolbar"><button type="button" disabled={!paging.cursor} onClick={restartPaging}>First page</button><button type="button" disabled={!page.nextCursor} onClick={() => setPaging({ cursor: page.nextCursor ?? undefined, fromUtc: page.fromUtc, toUtc: page.toUtc })}>Next evidence page</button></div></section>;
}
