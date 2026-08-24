import { useEffect, useState } from "react";
import { getQueryPerformance, getQueryPerformanceHistory, getQueryPerformancePlan, getQueryPerformanceStatus, getQueryPerformanceTop } from "./queryPerformanceApi";
import type { QueryPerformanceHistoryPage, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformanceStatus } from "./queryPerformanceTypes";

export function TargetQueryPerformancePanel({ instanceId, displayName, onClose }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void }) {
  const [page, setPage] = useState<QueryPerformancePage>();
  const [status, setStatus] = useState<QueryPerformanceStatus>();
  const [history, setHistory] = useState<QueryPerformanceHistoryPage>();
  const [historySelection, setHistorySelection] = useState<{ databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }>();
  const [plan, setPlan] = useState<QueryPerformancePlan>();
  const [message, setMessage] = useState<string>();
  const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
  useEffect(() => {
    const c = new AbortController(); setPage(undefined); setStatus(undefined); setHistory(undefined); setPlan(undefined); setMessage(undefined);
    void getQueryPerformance(instanceId, c.signal).then(setPage).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance evidence is unavailable."); });
    void getQueryPerformanceStatus(instanceId, undefined, undefined, c.signal).then(setStatus).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance status is unavailable."); });
    return () => c.abort();
  }, [instanceId]);
  const loadHistory = (selection: { databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }, cursor?: string) => {
    const c = new AbortController();
    void getQueryPerformanceHistory(instanceId, selection.databaseId, selection.queryFingerprint, selection.fromUtc, selection.toUtc, c.signal, cursor).then(setHistory).catch((e: unknown) => setMessage(e instanceof Error ? e.message : "Query history is unavailable."));
  };
  const select = (databaseId: number, queryFingerprint: string, from: string, to: string, planFingerprint: string | undefined) => {
    const selection = { databaseId, queryFingerprint, fromUtc: from, toUtc: to };
    const c = new AbortController();
    setHistorySelection(selection); setHistory(undefined);
    loadHistory(selection);
    if (planFingerprint !== undefined) void getQueryPerformancePlan(instanceId, databaseId, queryFingerprint, planFingerprint, c.signal).then(setPlan).catch((e: unknown) => setMessage(e instanceof Error ? e.message : "Plan metadata is unavailable."));
  };
  const loadMetricPage = (metric: QueryPerformanceMetric) => {
    if (page?.fromUtc === undefined || page.toUtc === undefined || page.cursorsByMetric?.[metric] === undefined) return;
    const c = new AbortController();
    void getQueryPerformanceTop(instanceId, metric, page.cursorsByMetric[metric], page.fromUtc, page.toUtc, c.signal).then(next => {
      const merged = new Map(page.items.map(item => [`${item.collectionRunId}:${item.observationKey}`, item]));
      next.items.forEach(item => { const key = `${item.collectionRunId}:${item.observationKey}`; const prior = merged.get(key); merged.set(key, prior === undefined ? item : { ...prior, metrics: { ...prior.metrics, ...item.metrics } }); });
      setPage({ ...page, items: [...merged.values()], cursorsByMetric: { ...page.cursorsByMetric, [metric]: next.nextCursor } });
    }).catch((e: unknown) => setMessage(e instanceof Error ? e.message : "Query performance page is unavailable."));
  };
  return <section className="activity-panel" aria-labelledby="query-performance-heading">
    <div className="health-heading-row"><div><p className="eyebrow">Milestone 7 · read-only</p><h3 id="query-performance-heading">Top queries for {displayName}</h3></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {message === undefined ? null : <p className="status-message">{message}</p>}
    {status === undefined ? null : <div className="activity-evidence"><strong>Status:</strong> {status.source ?? "unavailable"} · {status.sourceState} · {status.coverage}{status.truncated ? " · truncated" : ""} · repository snapshot {status.snapshotUtc}{status.reason === undefined ? null : ` · ${status.reason}`}{status.targetStatus === undefined ? null : ` · target ${status.targetStatus}: ${status.targetReason ?? "unavailable"}`}<div>{status.databaseStatuses.length === 0 ? "No per-database attempts were recorded." : status.databaseStatuses.map(x => <span key={x.databaseId} className="badge">DB {x.databaseId}: {x.status} · {x.sourceState} · fallback {x.fallbackAttempted ? "used" : "not used"} · {x.reason} · {x.lossKind} ({x.minimumLostItems} items, {x.minimumLostBytes} bytes{ x.lossCountIsExact ? ", exact" : ", lower bound"}){x.truncated ? " · truncated" : ""}</span>)}</div></div>}
    {page === undefined && message === undefined ? <p>Loading bounded query evidence…</p> : null}
    {page === undefined ? null : <>
      <p className="activity-evidence">Query Store and plan-cache rows are explicitly labelled; interval and cumulative semantics are not mixed. Content is unavailable until an approved protector exists. Repository time: {page.repositoryTimeUtc}.</p><div className="activity-evidence">{metrics.map(metric => <button className="secondary-button" type="button" key={metric} disabled={page.cursorsByMetric?.[metric] === undefined} onClick={() => loadMetricPage(metric)}>Next {metric} page</button>)}</div>
      <div className="activity-table" role="table"><div className="activity-row activity-header"><span>Database/query</span><span>Source</span><span>Semantics</span><span>CPU</span><span>Duration</span><span>Executions</span><span>Reads/writes/rows</span><span>Coverage</span></div>
        {page.items.map(x => <button className="activity-row" type="button" key={`${x.collectionRunId}-${x.observationKey}`} onClick={() => select(x.query.databaseId, x.query.queryFingerprint, x.intervalStartUtc, x.intervalEndUtc, x.plan?.planFingerprint)}><span>{String(x.query.databaseId)} / {x.query.queryFingerprint.slice(0, 12)}…</span><span>{x.source} · {x.sourceState}</span><span>{x.semantics}</span><span>{String(x.metrics.cpuMilliseconds ?? "—")}</span><span>{String(x.metrics.durationMilliseconds ?? "—")}</span><span>{String(x.metrics.executions ?? "—")}</span><span>{String(x.metrics.logicalReads ?? "—")} / {String(x.metrics.writes ?? "—")} / {String(x.metrics.rows ?? "—")}</span><span>{x.coverage}{x.truncated ? " · truncated" : ""}</span></button>)}
      </div>
      {history === undefined ? null : <div className="activity-evidence"><strong>Bounded history</strong>{history.items.length === 0 ? <p>No activity in the selected interval.</p> : history.items.map(x => <p key={`${x.collectionRunId}-${x.observationKey}`}>{x.intervalEndUtc}: CPU {String(x.metrics.cpuMilliseconds ?? "—")} · duration {String(x.metrics.durationMilliseconds ?? "—")} · executions {String(x.metrics.executions ?? "—")} · reads {String(x.metrics.logicalReads ?? "—")} · writes {String(x.metrics.writes ?? "—")} · rows {String(x.metrics.rows ?? "—")} · {x.source} · {x.semantics}</p>)}{history.nextCursor === undefined || historySelection === undefined ? null : <button className="secondary-button" type="button" onClick={() => loadHistory(historySelection, history.nextCursor)}>Next history page</button>}</div>}
      {plan === undefined ? null : <p className="activity-evidence"><strong>Plan metadata:</strong> {plan.planFingerprint.slice(0, 12)}… · {plan.source} · {plan.coverage} · content unavailable</p>}
    </>}
  </section>;
}
