import { useMemo } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { OverviewScope } from "../overview/overviewTypes";
import { resolveAgentWindow, type AgentHistoryPage } from "./agentHistoryModel.ts";
import { useAgentHistory } from "./useAgentHistory.ts";

export function AgentFailureEvidence({ page, compact = false }: { readonly page: AgentHistoryPage; readonly compact?: boolean }) {
  const formatTime = useTimeFormatter();
  const rows = compact ? page.rows.filter(row => row.countsAsJobFailure).slice(0, 10) : page.rows;
  return <>
    <p className="activity-evidence">{formatTime(page.window.fromUtc)} to {formatTime(page.window.toUtc)} · {page.state} ·
      Snapshot {formatTime(page.observedAtUtc)} · {page.markers.length} job-outcome failures in this page.
      {page.hasMore ? " More history pages are available." : ""}{page.truncated ? " Source capture was truncated." : ""}</p>
    <p className="table-note">First observed is when monitoring first captured the history row, not its execution time.
      Source execution start is the SQL Server local clock; its timezone is unknown. Job names and messages are unavailable.</p>
    {page.state === "PermissionDenied" && <p role="status">The collector cannot read SQL Agent history. Have a Target Administrator review the server permission script.</p>}
    {page.state === "Unsupported" && <p role="status">SQL Agent history is unsupported for this target. Check the SQL Server edition and Agent availability.</p>}
    {page.state === "NoData" && <p role="status">No SQL Agent snapshot has been captured. Check that the Agent collector is enabled and can connect.</p>}
    {rows.length ? <div className="table-scroll" tabIndex={0} role="region" aria-label="SQL Agent failure observations"><table><caption>{compact ? "Job failures first observed in this window" : "SQL Agent history first observed in the selected window"}</caption>
      <thead><tr><th>Job / history ID</th><th>Outcome / step</th><th>First observed</th><th>Source start (local, timezone unknown)</th><th>Duration (s)</th></tr></thead>
      <tbody>{rows.map(row => <tr key={`${row.jobId}:${row.historyInstanceId}`}><td>{row.jobId}<br />{row.historyInstanceId}</td>
        <td>{row.failureKind} · {row.stepId === 0 ? "Job outcome" : `Step ${row.stepId}`}</td>
        <td>{formatTime(row.firstObservedAtUtc)}</td><td>{row.sourceLocalStart ?? "Unavailable"}</td><td>{row.durationSeconds}</td></tr>)}</tbody></table></div>
      : <p>No {compact ? "job-outcome failure " : "history "}rows were returned in this page. Missing collection coverage can hide failures.</p>}
    {compact && page.markers.length > 10 && <p>Showing ten job failures below the timeline. Open SQL Agent history to browse this page and the rest.</p>}
  </>;
}

export function AgentFailureHistoryPanel({ instanceId, targetRevision, scope, refresh }: {
  readonly instanceId: string; readonly targetRevision: number; readonly scope: OverviewScope; readonly refresh: number;
}) {
  const selection = useMemo(() => {
    try { return { window: resolveAgentWindow(scope, Date.now()), error: undefined }; }
    catch (error) { return { window: undefined, error: error instanceof Error ? error.message : "Choose a valid SQL Agent history window." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const history = useAgentHistory(instanceId, targetRevision, selection.window, refresh);
  return <section className="panel operation-card" aria-label="SQL Agent failures" aria-busy={history.loading}>
    <h3>SQL Agent failures</h3>
    {selection.error && <p role="status">{selection.error}</p>}
    {history.loading && <p role="status">Loading SQL Agent history for the selected window…</p>}
    {history.error && <p role="alert">{history.error} Refresh the history from the first page.</p>}
    {history.page && <AgentFailureEvidence page={history.page} />}
    {selection.window && <nav className="pager" aria-label="SQL Agent history pages">
      <span>Page {history.pageNumber}</span>{" "}
      <button type="button" disabled={history.loading} onClick={() => void history.navigate()}>Refresh first page</button>{" "}
      <button type="button" disabled={history.loading || !history.page?.nextCursor} onClick={() => void history.navigate(history.page!.nextCursor!)}>Next history page</button>
    </nav>}
  </section>;
}
