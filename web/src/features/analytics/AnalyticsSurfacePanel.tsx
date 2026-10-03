import { EvidenceTable } from "../../components/EvidenceTable";
import { useTimeDisplay, useTimeFormatter } from "../../TimeDisplayContext";
import { analyticsEmptyStateText, analyticsScopeText } from "./analyticsScope";
import { panelStateForSurface } from "./analyticsState";
import { analyticsSurfaceLabel } from "./analyticsSurfaceCatalog";
import { analyticsInventory, type AnalyticsWindow } from "./analyticsSurfacePaging";
import { useAnalyticsSurface } from "./useAnalyticsSurface";
import type { AnalyticsPanelState, AnalyticsSurface } from "./analyticsTypes";
import "./analyticsWorkflow.css";

const messages: Record<AnalyticsPanelState, string> = {
  loading: "Loading…", ready: "", "no-data": "No data is available for this window.",
  degraded: "Data is degraded; some sources were unavailable.",
  "permission-denied": "This receipt reports a permission gap in the collected evidence. Check the collection identity's permissions.", unsupported: "This surface is unsupported for the target.",
  stale: "This snapshot is stale. Return to latest to read a new snapshot.", partial: "Only partial evidence is available.",
  "low-confidence": "Evidence confidence is low.", backfilling: "Backfill is in progress. Return to latest to check again.",
  "cursor-invalid": "The analytics continuation is invalid. Received pages remain available.",
  "retention-blocked": "This data is blocked by retention policy.",
};

export function AnalyticsSurfacePanel({ targetId, surface, timeWindow, targetRevision, contextKey, refresh = 0, active = true,
  denialEpoch = 0, onAccessDenied }: {
  targetId: string; surface: AnalyticsSurface; timeWindow?: AnalyticsWindow; targetRevision?: number;
  contextKey?: string; refresh?: number; active?: boolean; denialEpoch?: number; onAccessDenied?: () => void;
}) {
  const { mode } = useTimeDisplay();
  const formatTime = useTimeFormatter();
  // Direct callers get a strict explicit-window boundary. AnalyticsPage supplies a stable
  // workspace context so computed Live windows can move without disturbing held reads.
  const context = JSON.stringify([targetId.toLowerCase(), targetRevision ?? null, surface,
    contextKey ?? `${timeWindow?.fromUtc ?? ""}:${timeWindow?.toUtc ?? ""}`, denialEpoch]);
  const inbox = useAnalyticsSurface(targetId, surface, context, timeWindow, targetRevision, refresh, active,
    denialEpoch > 0, onAccessDenied);
  if (!active) return null;
  const page = inbox.receipt?.page;
  const state = page ? panelStateForSurface(page.state, page.items.length > 0) : "loading";
  const surfaceLabel = analyticsSurfaceLabel(surface);
  const rows = !page ? [] : surface.startsWith("replication/") ? page.items.map(item => ({
    "Observed": formatTime(String(item.observedAtUtc)),
    "Agent state": item.synchronizationState === "disabled" ? "Stopped" : item.synchronizationState,
    "Pending commands": item.pendingCommands,
    "Latency (seconds)": item.latencySeconds,
    "Coverage": item.coverage,
    "Evidence": { runId: item.runId, role: item.role, targetRevision: item.targetRevision, visibilityGap: item.visibilityGap },
  })) : page.items.map(item => Object.fromEntries(Object.entries(item).map(([key, value]) =>
    [key.endsWith("Utc") && typeof value === "string" ? `${key.slice(0, -3)} (${mode === "local" ? "local" : "UTC"})` : key,
      key.endsWith("Utc") && typeof value === "string" ? formatTime(value) : value])));
  return <section className="panel analytics-surface" aria-label={surfaceLabel}>
    <div className="analytics-workflow-heading"><h2>{surfaceLabel}</h2>
      <span className="analytics-workflow-mode">{inbox.retained ? "Retained analytics evidence" : "Latest received analytics evidence"}</span>
    </div>
    <div className="analytics-workflow-controls" aria-label="Analytics evidence controls">
      {surface !== "incidents" && <>
        <button type="button" disabled={inbox.loading || inbox.selected === 0} onClick={inbox.first}>First evidence page</button>
        <button type="button" disabled={inbox.loading || inbox.selected === 0} onClick={inbox.previous}>Previous evidence page</button>
        <button type="button" disabled={inbox.loading || !inbox.canNext} onClick={inbox.next}>Next evidence page</button>
      </>}
      <button type="button" disabled={inbox.loading} onClick={inbox.latest}>Return analytics to latest</button>
      {inbox.loading && <button type="button" onClick={inbox.cancel}>Cancel analytics read</button>}
      {!inbox.loading && inbox.canRetry && <button type="button" onClick={inbox.retry}>Retry analytics read</button>}
    </div>
    {inbox.loading && <p role="status" className="status-message">Reading analytics evidence…{page ? " The received page remains visible." : ""}</p>}
    {inbox.error && <p role="alert" className="status-message">{inbox.error}</p>}
    {inbox.bounded && <p role="status">Retained analytics limit reached: 20 pages / 4,000 rows. Visited pages remain available; return to latest for a new read.</p>}
    {page && <>
      <p className="analytics-workflow-receipt">Received page {inbox.selected + 1} of {inbox.receipts.length}. {page.items.length} rows on this page.
        {page.targetRevision !== undefined && <> Target revision: {page.targetRevision}.</>}
        {page.generation !== undefined && <> Generation: {page.generation}.</>}
        {inbox.receipt && <> Received: {formatTime(inbox.receipt.receivedAtUtc)}.</>}
      </p>
      <p className="analytics-workflow-scope">{analyticsScopeText(surface, page, formatTime)}</p>
      {surface === "incidents" ? <p className="analytics-workflow-note">Bounded incident list. The native route does not provide continuation or a snapshot fence. At most 100 incident rows are returned; this list does not prove that every incident is included.</p>
        : <p className="analytics-workflow-note">Visited pages retain the evidence received for this target, revision, generation and native read boundary. Inventory state and access can change between reads; this is not a frozen repository snapshot.{analyticsInventory(surface) ? " The effective request window binds continuation only; job inventory is not filtered by time." : ""}</p>}
      {(page.state === "visibility_gap" || messages[state]) && <p role="status">{page.state === "visibility_gap"
        ? "This history includes observations collected without a distribution database binding. Health and queue values are unavailable for those rows."
        : messages[state]}</p>}
      {state === "permission-denied" ? null : rows.length > 0 ? <EvidenceTable label={surfaceLabel} rows={rows} /> : <p className="empty-state">{analyticsEmptyStateText(surface)}</p>}
    </>}
  </section>;
}
