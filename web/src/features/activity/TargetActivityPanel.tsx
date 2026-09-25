import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { LiveSessionsPanel } from "./LiveSessionsPanel";

import { getActivitySnapshot, getBlockingHistoryPage, getServerWaitHistoryPage } from "./activityApi";
import { resolveActivityWindow } from "./activityWindowModel";
import { groupWaitDeltas } from "./waitCategoryModel";
import type { ActivityPage, ActivityWait, BlockingHistoryItem, ServerWaitHistoryItem } from "./activityTypes";
import type { OverviewScope } from "../overview/overviewTypes";

export interface TargetActivityPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly initialHistoryAtUtc?: string;
  readonly initialHistoryEventId?: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly manualRefresh: number;
  readonly sessionTick: number;
  readonly livePaused: boolean;
}

export function TargetActivityPanel({ instanceId, displayName, onClose, initialHistoryAtUtc, initialHistoryEventId, scope, refresh, manualRefresh, sessionTick, livePaused }: TargetActivityPanelProps) {
  const { mode } = useTimeDisplay();
  const [snapshot, setSnapshot] = useState<Awaited<ReturnType<typeof getActivitySnapshot>>>();
  const [message, setMessage] = useState<string>();
  const selected = useMemo(() => resolveActivityWindow(scope, Date.now()), [scope.range, scope.from, scope.to, refresh]);
  const window = selected.state === "available" ? selected.window : undefined;
  const historyUnavailableReason = selected.state === "unavailable" ? selected.message : selected.liveSnapshotsAvailable ? undefined : "Session snapshots are retained for 24 hours; this selected window is older. Wait and blocking history may still be available below.";
  const historySelection = window ?? null;
  useEffect(() => {
    const controller = new AbortController();
    setSnapshot(undefined);
    setMessage(undefined);
    void getActivitySnapshot(instanceId, controller.signal, historySelection)
      .then((next) => { if (!controller.signal.aborted) { setSnapshot(next); setMessage(undefined); } })
      .catch((error: unknown) => { if (!controller.signal.aborted) { setMessage(error instanceof Error ? error.message : "Activity evidence is unavailable."); } });
    return () => controller.abort();
  }, [instanceId, window?.fromUtc, window?.toUtc, refresh]);

  return (
    <section className="activity-screen" aria-labelledby="activity-heading">
      <LiveSessionsPanel key={`${instanceId}:${initialHistoryAtUtc ?? "live"}:${initialHistoryEventId ?? ""}:${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}`} instanceId={instanceId} displayName={displayName} initialHistoryAtUtc={initialHistoryAtUtc} initialHistoryEventId={initialHistoryEventId} selectedWindow={window} historyUnavailableReason={historyUnavailableReason} defaultHistorical={scope.range === "custom"} refreshToken={manualRefresh} clockTick={sessionTick} workspacePaused={livePaused} />
      <div className="screen-intro"><div><p className="eyebrow">Activity · supporting snapshot evidence</p><h2 id="activity-heading">Activity evidence for {displayName}</h2><p>Current sessions, requests, waits, and blocking are live snapshots. Wait and blocking history follow the selected time range when it is 24 hours or shorter.</p></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
      <details className="supporting-evidence"><summary>Open supporting waits, blocking, and history evidence</summary><div className="supporting-evidence-content">
      <p className="activity-evidence">{window ? `Selected activity-history window: ${formatDisplayTime(window.fromUtc, mode)} to ${formatDisplayTime(window.toUtc, mode)}.` : selected.state === "unavailable" ? selected.message : null}</p>
      {message === undefined ? null : <p className="status-message">{message}</p>}
      {snapshot === undefined && message === undefined ? <p>Loading bounded activity evidence…</p> : null}
      {snapshot === undefined ? null : <>
        {snapshot.errors.map(error => <p role="status" key={error}>{error} Refresh to retry this section.</p>)}
        {snapshot.sessions && <Evidence page={snapshot.sessions} />}
        {snapshot.requests && <Evidence page={snapshot.requests} />}
        {snapshot.sessions && <ActivityTable title="Sessions" columns={["Session", "Status", "Database", "CPU ms", "Memory pages", "Reads/writes", "Elapsed ms"]} rows={snapshot.sessions.items.map((item) => [String(item.sessionId), item.status, String(item.databaseId ?? "—"), item.cpuMilliseconds, item.memoryUsagePages, `${item.reads}/${item.writes}`, item.totalElapsedMilliseconds])} />}
        {snapshot.requests && <ActivityTable title="Active requests" columns={["Session/request", "Status", "Command", "CPU ms", "Reads/writes", "Rows", "% complete"]} rows={snapshot.requests.items.map((item) => [`${String(item.sessionId)}/${String(item.requestId)}`, item.status, item.command, item.cpuMilliseconds, `${item.reads}/${item.writes}`, item.rowCount, String(item.percentComplete)])} />}
        {snapshot.waits && <Evidence page={snapshot.waits} />}
        {snapshot.waits && <WaitCategoryChart page={snapshot.waits} />}
        {snapshot.waits && <ActivityTable title="Server waits" columns={["Wait type", "Tasks", "Wait ms", "Max/signal", "Deltas", "Baseline"]} rows={snapshot.waits.items.map((item) => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds, `${item.maximumWaitTimeMilliseconds}/${item.signalWaitTimeMilliseconds}`, item.resetDetected ? "reset" : `${item.waitingTasksDelta ?? "—"}/${item.waitTimeMillisecondsDelta ?? "—"}/${item.signalWaitTimeMillisecondsDelta ?? "—"}`, item.baselineAvailable ? "available" : "not available"])} />}
        {snapshot.blocking && <Evidence page={snapshot.blocking} />}
        {snapshot.blocking && <ActivityTable title="Current blocking" columns={["Blocked", "Blocker/root", "Wait type", "Tasks/duration", "Depth", "Root resolution"]} rows={snapshot.blocking.items.map((item) => [String(item.blockedSessionId), item.blockerSessionId === undefined ? item.blockerKind : `${String(item.blockerSessionId)}/${String(item.rootBlockerSessionId ?? "—")}`, item.waitType, `${item.waitingTaskCount}/${item.waitDurationMilliseconds}`, String(item.chainDepth), item.chainState])} />}
        {snapshot.history && <BlockingHistory key={`${instanceId}/${window?.fromUtc}/${window?.toUtc}/${snapshot.history.repositoryTimeUtc}`} instanceId={instanceId} initialPage={snapshot.history} />}
        {snapshot.waitHistory && <WaitHistory key={`${instanceId}/${window?.fromUtc}/${window?.toUtc}/${snapshot.waitHistory.repositoryTimeUtc}`} instanceId={instanceId} initialPage={snapshot.waitHistory} />}
      </>}
      </div></details>
    </section>
  );
}

function WaitCategoryChart({ page }: { readonly page: ActivityPage<ActivityWait> }) {
  const summary = useMemo(() => groupWaitDeltas(page.items), [page.items]);
  const max = Math.max(1, ...summary.categories.map(item => Number(item.waitMilliseconds)));
  return <section className="panel wait-chart" aria-label="Wait categories">
    <h4>Wait categories · loaded page</h4>
    <p>Comparable positive wait-time deltas, grouped by likely cause. Idle waits are omitted here; the raw wait table below retains every loaded type.{page.nextCursor ? " More wait rows are available." : ""}</p>
    {summary.categories.map(item => <label key={item.category}><span>{item.category} · {item.waitTypes} type{item.waitTypes === 1 ? "" : "s"}</span><meter min={0} max={max} value={Number(item.waitMilliseconds)} /><span>{item.waitMilliseconds} ms</span></label>)}
    {summary.categories.length === 0 && <p>No comparable positive wait deltas are available on this page.</p>}
    {(summary.idleTypesOmitted > 0 || summary.incomparableTypes > 0) && <p>{summary.idleTypesOmitted} idle type{summary.idleTypesOmitted === 1 ? "" : "s"} omitted; {summary.incomparableTypes} type{summary.incomparableTypes === 1 ? "" : "s"} without a comparable baseline or after a reset.</p>}
  </section>;
}

function BlockingHistory({ instanceId, initialPage }: { readonly instanceId: string; readonly initialPage: ActivityPage<BlockingHistoryItem> }) {
  const { mode } = useTimeDisplay();
  const [page, setPage] = useState(initialPage);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);
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
    <ActivityTable title="Blocking history" columns={[mode === "local" ? "Observed local" : "Observed UTC", "Blocked", "Blocker", "Wait type", "Tasks/duration", "Depth", "Root resolution/evidence"]} rows={page.items.map(item => [formatDisplayTime(item.edge.observedAtUtc, mode), String(item.edge.blockedSessionId), item.edge.blockerSessionId === undefined ? item.edge.blockerKind : String(item.edge.blockerSessionId), item.edge.waitType, `${item.edge.waitingTaskCount}/${item.edge.waitDurationMilliseconds}`, String(item.edge.chainDepth), `${item.edge.chainState} (${item.evidence.freshness}/${item.evidence.outcome})`])} />
    <p role="status">Page {pageIndex + 1} · {page.items.length} observations{loading ? " · Loading history…" : page.nextCursor === undefined ? " · End of this window" : ""}</p>
    {error && <p role="alert">{error} The displayed page is unchanged. Retry using the navigation buttons.</p>}
    <nav aria-label="Blocking history pages">
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(0)}>First history page</button>{" "}
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Newer history</button>{" "}
      <button type="button" disabled={loading || page.nextCursor === undefined || !window} onClick={() => void navigate(pageIndex + 1, page.nextCursor)}>Older history</button>
    </nav>
  </section>;
}

function WaitHistory({ instanceId, initialPage }: { readonly instanceId: string; readonly initialPage: ActivityPage<ServerWaitHistoryItem> }) {
  const { mode } = useTimeDisplay();
  const [page, setPage] = useState(initialPage);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);
  const window = initialPage.fromUtc && initialPage.toUtc ? { fromUtc: initialPage.fromUtc, toUtc: initialPage.toUtc } : undefined;
  async function navigate(index: number, cursor?: string) {
    if (!window || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setError(undefined);
    try {
      const next = await getServerWaitHistoryPage(instanceId, window, controller.signal, cursor);
      if (controller.signal.aborted) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (failure: unknown) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Wait history could not be loaded. Try again.");
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    }
  }
  return <section aria-label="Server wait history" aria-busy={loading}>
    <p className="activity-evidence">Wait history window: {window ? `${formatDisplayTime(window.fromUtc, mode)} to ${formatDisplayTime(window.toUtc, mode)}` : "bounded window unavailable"}. Rows use the preceding comparable collector run as a baseline; totals remain available when a baseline is missing or resets. {page.nextCursor ? "More bounded history is available." : ""}</p>
    <ActivityTable title="Server wait history" columns={[mode === "local" ? "Observed local" : "Observed UTC", "Wait type", "Tasks", "Total wait ms", "Wait delta ms", "Signal delta ms", "Baseline/evidence"]} rows={page.items.map(item => [formatDisplayTime(item.wait.observedAtUtc, mode), item.wait.waitType, item.wait.waitingTasksCount, item.wait.waitTimeMilliseconds, item.wait.waitTimeMillisecondsDelta ?? "—", item.wait.signalWaitTimeMillisecondsDelta ?? "—", `${item.wait.resetDetected ? "reset" : item.wait.baselineAvailable ? "comparable" : "no baseline"} · ${item.evidence.freshness}/${item.evidence.outcome}${item.evidence.isPartial ? " · partial" : ""}`])} />
    <p role="status">Page {pageIndex + 1} · {page.items.length} observations{loading ? " · Loading wait history…" : page.nextCursor === undefined ? " · End of this window" : ""}</p>
    {error && <p role="alert">{error} The displayed page is unchanged. Retry using the navigation buttons.</p>}
    <nav aria-label="Server wait history pages">
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(0)}>First wait history page</button>{" "}
      <button type="button" disabled={loading || pageIndex === 0 || !window} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Newer waits</button>{" "}
      <button type="button" disabled={loading || page.nextCursor === undefined || !window} onClick={() => void navigate(pageIndex + 1, page.nextCursor)}>Older waits</button>
    </nav>
  </section>;
}

function Evidence<T>({ page }: { readonly page: ActivityPage<T> }) {
  const { mode } = useTimeDisplay();
  const evidence = page.evidence;
  if (evidence === undefined) return <p className="activity-evidence">No current evidence. Repository time: {formatDisplayTime(page.repositoryTimeUtc, mode)}</p>;
  const loss = evidence.loss;
  const lossText = loss === undefined ? "" : ` — ${loss.kind}: at least ${String(loss.minimumLostItems)} item(s), ${String(loss.minimumLostBytes)} byte(s)${loss.countIsExact ? " (exact count)" : " (minimum)"}`;
  const graphLimit = page.maximumChainDepth === undefined ? "" : ` Graph bounds: depth ${String(page.maximumChainDepth)}, nodes ${String(page.maximumGraphNodes ?? "—")}.`;
  const baseline = page.baselineRunId === undefined ? "" : ` Baseline run: ${page.baselineRunId}.`;
  return <p className="activity-evidence">{evidence.collectorId}: {evidence.freshness}; {evidence.outcome} ({evidence.reason}), completed {formatDisplayTime(evidence.completedAtUtc, mode)}{evidence.isPartial || loss !== undefined ? lossText : ""}. Repository time: {formatDisplayTime(page.repositoryTimeUtc, mode)}{baseline}{graphLimit}{page.nextCursor === undefined ? "" : " More bounded rows available."}</p>;
}

function HistoryEvidence({ page }: { readonly page: ActivityPage<BlockingHistoryItem> }) {
  const { mode } = useTimeDisplay();
  const range = page.fromUtc === undefined || page.toUtc === undefined ? "bounded window unavailable" : `${formatDisplayTime(page.fromUtc, mode)} to ${formatDisplayTime(page.toUtc, mode)}`;
  const evidence = page.evidence ?? page.items[0]?.evidence;
  return <p className="activity-evidence">Blocking history window: {range}. {evidence === undefined ? "No historical evidence." : `${evidence.collectorId}: ${evidence.freshness}; ${evidence.outcome}${evidence.isPartial ? " — partial/loss evidence" : ""}.`}{page.nextCursor === undefined ? "" : " More bounded history is available."}</p>;
}

function ActivityTable({ title, columns, rows }: { readonly title: string; readonly columns: readonly string[]; readonly rows: readonly (readonly string[])[] }) {
  return <section className="activity-section"><h4>{title}</h4>{rows.length === 0 ? <p className="empty-state">No bounded rows reported.</p> : <table><thead><tr>{columns.map(column => <th scope="col" key={column}>{column}</th>)}</tr></thead><tbody>{rows.map((row, index) => <tr key={index}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell}</td>)}</tr>)}</tbody></table>}</section>;
}
