import { useEffect, useState } from "react";

import { getActivitySnapshot } from "./activityApi";
import type { ActivityPage, BlockingHistoryItem } from "./activityTypes";

export interface TargetActivityPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
}

export function TargetActivityPanel({ instanceId, displayName, onClose }: TargetActivityPanelProps) {
  const [snapshot, setSnapshot] = useState<Awaited<ReturnType<typeof getActivitySnapshot>>>();
  const [message, setMessage] = useState<string>();
  useEffect(() => {
    const controller = new AbortController();
    setSnapshot(undefined);
    void getActivitySnapshot(instanceId, controller.signal)
      .then((next) => { if (!controller.signal.aborted) { setSnapshot(next); setMessage(undefined); } })
      .catch((error: unknown) => { if (!controller.signal.aborted) { setMessage(error instanceof Error ? error.message : "Activity evidence is unavailable."); } });
    return () => controller.abort();
  }, [instanceId]);

  return (
    <section className="activity-panel" aria-labelledby="activity-heading" aria-live="polite">
      <div className="health-heading-row"><div><p className="eyebrow">Milestone 5</p><h3 id="activity-heading">Activity for {displayName}</h3></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
      {message === undefined ? null : <p className="status-message">{message}</p>}
      {snapshot === undefined && message === undefined ? <p>Loading bounded activity evidence…</p> : null}
      {snapshot === undefined ? null : <>
        <Evidence page={snapshot.sessions} />
        <Evidence page={snapshot.requests} />
        <ActivityTable title="Sessions" columns={["Session", "Status", "Database", "CPU ms", "Memory pages", "Reads/writes", "Elapsed ms"]} rows={snapshot.sessions.items.map((item) => [String(item.sessionId), item.status, String(item.databaseId ?? "—"), item.cpuMilliseconds, item.memoryUsagePages, `${item.reads}/${item.writes}`, item.totalElapsedMilliseconds])} />
        <ActivityTable title="Active requests" columns={["Session/request", "Status", "Command", "CPU ms", "Reads/writes", "Rows", "% complete"]} rows={snapshot.requests.items.map((item) => [`${String(item.sessionId)}/${String(item.requestId)}`, item.status, item.command, item.cpuMilliseconds, `${item.reads}/${item.writes}`, item.rowCount, String(item.percentComplete)])} />
        <Evidence page={snapshot.waits} />
        <ActivityTable title="Server waits" columns={["Wait type", "Tasks", "Wait ms", "Max/signal", "Deltas", "Baseline"]} rows={snapshot.waits.items.map((item) => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds, `${item.maximumWaitTimeMilliseconds}/${item.signalWaitTimeMilliseconds}`, item.resetDetected ? "reset" : `${item.waitingTasksDelta ?? "—"}/${item.waitTimeMillisecondsDelta ?? "—"}/${item.signalWaitTimeMillisecondsDelta ?? "—"}`, item.baselineAvailable ? "available" : "not available"])} />
        <Evidence page={snapshot.blocking} />
        <ActivityTable title="Current blocking" columns={["Blocked", "Blocker/root", "Wait type", "Tasks/duration", "Depth", "State"]} rows={snapshot.blocking.items.map((item) => [String(item.blockedSessionId), item.blockerSessionId === undefined ? item.blockerKind : `${String(item.blockerSessionId)}/${String(item.rootBlockerSessionId ?? "—")}`, item.waitType, `${item.waitingTaskCount}/${item.waitDurationMilliseconds}`, String(item.chainDepth), item.chainState])} />
        <HistoryEvidence page={snapshot.history} />
        <ActivityTable title="Blocking history (last hour)" columns={["Blocked", "Blocker", "Wait type", "Tasks/duration", "Depth", "State/evidence"]} rows={snapshot.history.items.map((item) => [String(item.edge.blockedSessionId), item.edge.blockerSessionId === undefined ? item.edge.blockerKind : String(item.edge.blockerSessionId), item.edge.waitType, `${item.edge.waitingTaskCount}/${item.edge.waitDurationMilliseconds}`, String(item.edge.chainDepth), `${item.edge.chainState} (${item.evidence.freshness}/${item.evidence.outcome})`])} />
      </>}
    </section>
  );
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
  return <section className="activity-section"><h4>{title}</h4>{rows.length === 0 ? <p className="empty-state">No bounded rows reported.</p> : <div className="activity-table" role="table"><div className="activity-row activity-header" role="row">{columns.map((column) => <span key={column}>{column}</span>)}</div>{rows.map((row, index) => <div className="activity-row" key={`${title}-${String(index)}`} role="row">{row.map((cell, cellIndex) => <span key={`${String(index)}-${String(cellIndex)}`}>{cell}</span>)}</div>)}</div>}</section>;
}
