import { RequestStatus } from "../../components/RequestStatus";
import { useEvidenceResource } from "../../hooks/useEvidenceResource";
import { windowLimitMessage, type InvestigationWindow } from "../../investigationWindow";
import { useEffect, useRef, useState } from "react";
import { LiveSessionsPanel } from "./LiveSessionsPanel";

import { getActivitySnapshot, getBlockingHistoryPage } from "./activityApi";
import type { ActivityPage, BlockingHistoryItem } from "./activityTypes";

export interface TargetActivityPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly refresh?: number;
  readonly timeWindow?: InvestigationWindow;
  readonly windowError?: string;
  readonly initialHistoryAtUtc?: string;
  readonly initialHistoryEventId?: string;
}

export function TargetActivityPanel({ instanceId, displayName, onClose, initialHistoryAtUtc, initialHistoryEventId, refresh = 0, timeWindow, windowError }: TargetActivityPanelProps) {
  const rangeError = windowError ?? (timeWindow ? windowLimitMessage(timeWindow, 1, "Activity history") : undefined);
  const result = useEvidenceResource(`${instanceId}:${timeWindow?.fromUtc}:${timeWindow?.toUtc}`, refresh, signal => getActivitySnapshot(instanceId, signal, 1, rangeError ? null : timeWindow));
  const snapshot = result.data;

  return (
    <section className="activity-screen" aria-labelledby="activity-heading">
      <LiveSessionsPanel key={`${instanceId}:${initialHistoryAtUtc ?? "live"}:${initialHistoryEventId ?? ""}`} instanceId={instanceId} displayName={displayName} externalRefresh={refresh} timeWindow={timeWindow} windowError={rangeError} initialHistoryAtUtc={initialHistoryAtUtc} initialHistoryEventId={initialHistoryEventId} />
      <div className="screen-intro"><div><p className="eyebrow">Activity · supporting snapshot evidence</p><h2 id="activity-heading">Activity evidence for {displayName}</h2><p>Live sessions stay in the primary workspace. Historical waits and blocking pages remain bounded and expandable.</p></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
      <details className="supporting-evidence"><summary>Open supporting waits, blocking, and history evidence</summary><div className="supporting-evidence-content">
      <p className="table-note">Sessions, waits, requests, and current blocking show latest snapshots. Blocking history uses the selected investigation window (maximum 24 hours).</p>
      {rangeError ? <p role="alert">{rangeError} Current activity remains available.</p> : null}
      <RequestStatus loading={result.loading} error={result.error} updatedAt={result.updatedAt} hasData={Boolean(snapshot)} label="Activity evidence" onRetry={result.retry} />
      {snapshot === undefined ? null : <>
        {snapshot.errors.map(error => <RequestStatus key={error} loading={false} error={error} label="Activity section" onRetry={result.retry} />)}
        {snapshot.sessions && <Evidence page={snapshot.sessions} />}
        {snapshot.requests && <Evidence page={snapshot.requests} />}
        {snapshot.sessions && <ActivityTable title="Sessions" columns={["Session", "Status", "Database", "CPU ms", "Memory pages", "Reads/writes", "Elapsed ms"]} rows={snapshot.sessions.items.map((item) => [String(item.sessionId), item.status, String(item.databaseId ?? "—"), item.cpuMilliseconds, item.memoryUsagePages, `${item.reads}/${item.writes}`, item.totalElapsedMilliseconds])} />}
        {snapshot.requests && <ActivityTable title="Active requests" columns={["Session/request", "Status", "Command", "CPU ms", "Reads/writes", "Rows", "% complete"]} rows={snapshot.requests.items.map((item) => [`${String(item.sessionId)}/${String(item.requestId)}`, item.status, item.command, item.cpuMilliseconds, `${item.reads}/${item.writes}`, item.rowCount, String(item.percentComplete)])} />}
        {snapshot.waits && <Evidence page={snapshot.waits} />}
        {snapshot.waits && <section className="panel wait-chart"><h4>Wait deltas · loaded page</h4><p>Up to 10 largest wait-time deltas in this page, in milliseconds. Missing baselines and resets are excluded.</p>{snapshot.waits!.items.filter(item => item.baselineAvailable && !item.resetDetected && item.waitTimeMillisecondsDelta != null).sort((a,b) => Number(b.waitTimeMillisecondsDelta)-Number(a.waitTimeMillisecondsDelta)).slice(0,10).map(item => <label key={item.waitType}>{item.waitType}<meter min={0} max={Math.max(1,...snapshot.waits!.items.filter(x => x.baselineAvailable && !x.resetDetected).map(x => Number(x.waitTimeMillisecondsDelta ?? 0)))} value={Number(item.waitTimeMillisecondsDelta)} />{item.waitTimeMillisecondsDelta} ms</label>)}{!snapshot.waits!.items.some(item => item.baselineAvailable && !item.resetDetected && item.waitTimeMillisecondsDelta != null) && <p>No comparable wait deltas are available.</p>}</section>}
        {snapshot.waits && <ActivityTable title="Server waits" columns={["Wait type", "Tasks", "Wait ms", "Max/signal", "Deltas", "Baseline"]} rows={snapshot.waits.items.map((item) => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds, `${item.maximumWaitTimeMilliseconds}/${item.signalWaitTimeMilliseconds}`, item.resetDetected ? "reset" : `${item.waitingTasksDelta ?? "—"}/${item.waitTimeMillisecondsDelta ?? "—"}/${item.signalWaitTimeMillisecondsDelta ?? "—"}`, item.baselineAvailable ? "available" : "not available"])} />}
        {snapshot.blocking && <Evidence page={snapshot.blocking} />}
        {snapshot.blocking && <ActivityTable title="Current blocking" columns={["Blocked", "Blocker/root", "Wait type", "Tasks/duration", "Depth", "Root resolution"]} rows={snapshot.blocking.items.map((item) => [String(item.blockedSessionId), item.blockerSessionId === undefined ? item.blockerKind : `${String(item.blockerSessionId)}/${String(item.rootBlockerSessionId ?? "—")}`, item.waitType, `${item.waitingTaskCount}/${item.waitDurationMilliseconds}`, String(item.chainDepth), item.chainState])} />}
        {snapshot.history && <BlockingHistory key={`${instanceId}/${timeWindow?.fromUtc}/${timeWindow?.toUtc}`} instanceId={instanceId} initialPage={snapshot.history} refresh={refresh} />}
      </>}
      </div></details>
    </section>
  );
}

function BlockingHistory({ instanceId, initialPage, refresh }: { readonly instanceId: string; readonly initialPage: ActivityPage<BlockingHistoryItem>; readonly refresh: number }) {
  const [page, setPage] = useState(initialPage);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  const previousRefresh = useRef(refresh);
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => { if (pageIndex === 0) setPage(initialPage); }, [initialPage]);
  useEffect(() => { if (previousRefresh.current === refresh) return; previousRefresh.current = refresh; if (pageIndex > 0) void navigate(pageIndex, cursors[pageIndex]); }, [refresh]);
  const window = initialPage.fromUtc && initialPage.toUtc ? { fromUtc: initialPage.fromUtc, toUtc: initialPage.toUtc } : undefined;
  async function navigate(index: number, cursor?: string) {
    if (!window || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setError(undefined);
    try {
      const next = await getBlockingHistoryPage(instanceId, window, controller.signal, cursor);
      if (controller.signal.aborted) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (failure: unknown) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "History could not be loaded. Try again.");
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    }
  }
  return <section aria-label="Blocking history" aria-busy={loading}>
    <HistoryEvidence page={page} />
    <ActivityTable title="Blocking history" columns={["Observed UTC", "Blocked", "Blocker", "Wait type", "Tasks/duration", "Depth", "Root resolution/evidence"]} rows={page.items.map(item => [item.edge.observedAtUtc, String(item.edge.blockedSessionId), item.edge.blockerSessionId === undefined ? item.edge.blockerKind : String(item.edge.blockerSessionId), item.edge.waitType, `${item.edge.waitingTaskCount}/${item.edge.waitDurationMilliseconds}`, String(item.edge.chainDepth), `${item.edge.chainState} (${item.evidence.freshness}/${item.evidence.outcome})`])} />
    <p role="status">Page {pageIndex + 1} · {page.items.length} observations{loading ? " · Loading history…" : page.nextCursor === undefined ? " · End of this window" : ""}</p>
    {error && <p role="alert">{error} The displayed page is unchanged. Retry using the navigation buttons.</p>}
    <nav aria-label="Blocking history pages">
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(0)}>First history page</button>{" "}
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Newer history</button>{" "}
      <button type="button" disabled={loading || page.nextCursor === undefined || !window} onClick={() => void navigate(pageIndex + 1, page.nextCursor)}>Older history</button>
    </nav>
  </section>;
}

function Evidence<T>({ page }: { readonly page: ActivityPage<T> }) {
  const evidence = page.evidence;
  if (evidence === undefined) return <p className="activity-evidence">No current evidence. Repository time: {page.repositoryTimeUtc}</p>;
  const loss = evidence.loss;
  const lossText = loss === undefined ? "" : ` — ${loss.kind}: at least ${String(loss.minimumLostItems)} item(s), ${String(loss.minimumLostBytes)} byte(s)${loss.countIsExact ? " (exact count)" : " (minimum)"}`;
  const graphLimit = page.maximumChainDepth === undefined ? "" : ` Graph bounds: depth ${String(page.maximumChainDepth)}, nodes ${String(page.maximumGraphNodes ?? "—")}.`;
  const baseline = page.baselineRunId === undefined ? "" : ` Baseline run: ${page.baselineRunId}.`;
  return <p className="activity-evidence">{evidence.collectorId}: {evidence.freshness}; {evidence.outcome} ({evidence.reason}), completed {evidence.completedAtUtc}{evidence.isPartial || loss !== undefined ? lossText : ""}. Repository time: {page.repositoryTimeUtc}{baseline}{graphLimit}{page.nextCursor === undefined ? "" : " More bounded rows available."}</p>;
}

function HistoryEvidence({ page }: { readonly page: ActivityPage<BlockingHistoryItem> }) {
  const range = page.fromUtc === undefined || page.toUtc === undefined ? "bounded window unavailable" : `${page.fromUtc} to ${page.toUtc}`;
  const evidence = page.evidence ?? page.items[0]?.evidence;
  return <p className="activity-evidence">Blocking history window: {range}. {evidence === undefined ? "No historical evidence." : `${evidence.collectorId}: ${evidence.freshness}; ${evidence.outcome}${evidence.isPartial ? " — partial/loss evidence" : ""}.`}{page.nextCursor === undefined ? "" : " More bounded history is available."}</p>;
}

function ActivityTable({ title, columns, rows }: { readonly title: string; readonly columns: readonly string[]; readonly rows: readonly (readonly string[])[] }) {
  return <section className="activity-section"><h4>{title}</h4>{rows.length === 0 ? <p className="empty-state">No bounded rows reported.</p> : <table><thead><tr>{columns.map(column => <th scope="col" key={column}>{column}</th>)}</tr></thead><tbody>{rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table>}</section>;
}
