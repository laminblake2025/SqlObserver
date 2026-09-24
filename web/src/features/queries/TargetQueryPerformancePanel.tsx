import { metricsForObservation, queryPerformanceDatabaseOptions } from "./querySelection";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { ObservationChart } from "../../components/ObservationChart";
import { useEffect, useRef, useState } from "react";
import { getQueryPerformanceHistory, getQueryPerformancePlan, getQueryPerformanceStatus, getQueryPerformanceTop } from "./queryPerformanceApi";
import type { QueryWindow } from "./queryWindowModel";
import type { QueryPerformanceHistoryPage, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformanceStatus } from "./queryPerformanceTypes";

export function TargetQueryPerformancePanel({ instanceId, displayName, onClose, timeWindow, onSelectWindow, refresh }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void;
  readonly timeWindow: QueryWindow;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly refresh: number;
}) {
  const selectionRequest = useRef<AbortController | null>(null);
  const historyRequest = useRef<AbortController | null>(null);
  const pagingRequest = useRef<AbortController | null>(null);
  const activeQuery = useRef<{ readonly instanceId: string; readonly ranking: QueryPerformanceMetric; readonly fromUtc: string; readonly toUtc: string; readonly refresh: number } | undefined>(undefined);
  const [ranking, setRanking] = useState<QueryPerformanceMetric>("cpu");
  const [paging, setPaging] = useState(false);
  const [database, setDatabase] = useState("");
  // The live collector can legitimately fall back to plan cache when Query Store
  // is empty or unavailable. Show the bounded evidence from both sources by
  // default so an environment with that fallback does not look like it has no
  // data; users can still narrow the view with the source filter below.
  const [source, setSource] = useState("mixed");
  useEffect(() => () => { selectionRequest.current?.abort(); historyRequest.current?.abort(); pagingRequest.current?.abort(); }, [instanceId, ranking, timeWindow.fromUtc, timeWindow.toUtc]);
  const [page, setPage] = useState<QueryPerformancePage>();
  const [status, setStatus] = useState<QueryPerformanceStatus>();
  const [history, setHistory] = useState<QueryPerformanceHistoryPage>();
  const [historySelection, setHistorySelection] = useState<{ databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }>();
  const [plan, setPlan] = useState<QueryPerformancePlan>();
  const [message, setMessage] = useState<string>();
  const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
  useEffect(() => {
    const previous = activeQuery.current;
    const queryChanged = previous === undefined || previous.instanceId !== instanceId || previous.ranking !== ranking ||
      (previous.refresh === refresh && (previous.fromUtc !== timeWindow.fromUtc || previous.toUtc !== timeWindow.toUtc));
    activeQuery.current = { instanceId, ranking, fromUtc: timeWindow.fromUtc, toUtc: timeWindow.toUtc, refresh };
    const c = new AbortController(); setPaging(false); setPage(undefined); setStatus(undefined); setMessage(undefined);
    if (queryChanged) { setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); }
    void getQueryPerformanceTop(instanceId, ranking, undefined, timeWindow.fromUtc, timeWindow.toUtc, c.signal).then(value => { if (!c.signal.aborted) setPage(value); }).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance evidence is unavailable."); });
    void getQueryPerformanceStatus(instanceId, timeWindow.fromUtc, timeWindow.toUtc, c.signal).then(value => { if (!c.signal.aborted) setStatus(value); }).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance status is unavailable."); });
    return () => c.abort();
  }, [instanceId, ranking, timeWindow.fromUtc, timeWindow.toUtc, refresh]);
  useEffect(() => {
    if (!page || !historySelection || page.items.some(item => item.query.databaseId === historySelection.databaseId && item.query.queryFingerprint === historySelection.queryFingerprint)) return;
    setHistorySelection(undefined);
    setHistory(undefined);
    setPlan(undefined);
  }, [page, historySelection]);
  const loadHistory = (selection: { databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }, cursor?: string) => {
    historyRequest.current?.abort();
    const c = new AbortController(); historyRequest.current = c;
    void getQueryPerformanceHistory(instanceId, selection.databaseId, selection.queryFingerprint, selection.fromUtc, selection.toUtc, c.signal, cursor).then(value => { if (!c.signal.aborted) setHistory(value); }).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query history is unavailable."); });
  };
  const select = (databaseId: number, queryFingerprint: string, from: string, to: string, planFingerprint: string | undefined) => {
    const selection = { databaseId, queryFingerprint, fromUtc: page?.fromUtc ?? from, toUtc: page?.toUtc ?? to };
    const c = new AbortController();
    selectionRequest.current?.abort(); selectionRequest.current = c;
    setPlan(undefined); setMessage(undefined); setHistorySelection(selection); setHistory(undefined);
    loadHistory(selection);
    if (planFingerprint !== undefined) void getQueryPerformancePlan(instanceId, databaseId, queryFingerprint, planFingerprint, c.signal).then(value => { if (!c.signal.aborted) setPlan(value); }).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Plan metadata is unavailable."); });
  };
  const loadMetricPage = () => {
    if (!page?.nextCursor || paging) return;
    const c = new AbortController(); pagingRequest.current?.abort(); pagingRequest.current = c;
    setPaging(true); setMessage(undefined);
    void getQueryPerformanceTop(instanceId, ranking, page.nextCursor, page.fromUtc, page.toUtc, c.signal).then(next => {
      if (c.signal.aborted) return;
      selectionRequest.current?.abort(); historyRequest.current?.abort();
      setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); setPage(next);
    }).catch((e: unknown) => { if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance page is unavailable."); }).finally(() => { if (!c.signal.aborted) setPaging(false); });
  };
  const selectedItem = page?.items.find(item => item.query.databaseId === historySelection?.databaseId && item.query.queryFingerprint === historySelection.queryFingerprint && (source === "mixed" || item.source === source));
  const selectedMetrics = metricsForObservation(selectedItem, history);
  const databaseOptions = queryPerformanceDatabaseOptions(page, status);
  const databaseNames = new Map(databaseOptions.map(option => [option.databaseId, option.databaseName]));
  const displayDatabase = (databaseId: number | undefined) => databaseId === undefined ? "—" : databaseNames.get(databaseId) ?? `Database ID ${databaseId}`;
  const rows = page?.items.filter(item => (source === "mixed" || item.source === source) && (!database || String(item.query.databaseId) === database)) ?? [];
  return <section className="query-performance-screen" aria-labelledby="query-performance-heading">
    <div className="health-heading-row"><div><p className="eyebrow">Milestone 7 · read-only</p><h3 id="query-performance-heading">Top queries for {displayName}</h3></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {message === undefined ? null : <p className="status-message">{message}</p>}
    {status === undefined ? null : <div className="activity-evidence"><EvidenceStatus label={`${status.source ?? "unavailable"} evidence · ${status.coverage}`} detail={`${status.sourceState}${status.truncated ? " · truncated" : ""} · repository snapshot ${status.snapshotUtc}`} tone={status.sourceState === "read_write" && !status.truncated ? "current" : status.sourceState === "unavailable" ? "unavailable" : "warning"} /><div>{status.databaseStatuses.length === 0 ? "No per-database attempts were recorded." : status.databaseStatuses.map(x => <span key={x.databaseId} className="badge">{displayDatabase(x.databaseId)}: {x.status} · {x.sourceState} · fallback {x.fallbackAttempted ? "used" : "not used"} · {x.reason} · {x.lossKind} ({x.minimumLostItems} items, {x.minimumLostBytes} bytes{ x.lossCountIsExact ? ", exact" : ", lower bound"}){x.truncated ? " · truncated" : ""}</span>)}</div></div>}
    {page === undefined && message === undefined ? <p>Loading bounded query evidence…</p> : null}
    {page === undefined ? null : <>
      <p className="activity-evidence">Query Store and plan-cache rows are explicitly labelled; interval and cumulative semantics are not mixed. Query text and plan content are unavailable. Repository time: {page.repositoryTimeUtc}.</p><div className="toolbar"><label>Rank by <select value={ranking} onChange={event => setRanking(event.target.value as QueryPerformanceMetric)}>{metrics.map(metric => <option key={metric}>{metric}</option>)}</select></label><button disabled={!page.nextCursor || paging} onClick={loadMetricPage}>Next ranking page</button></div>
      <p className="activity-evidence">Ranking window: {page.fromUtc} to {page.toUtc} · UTC · each row is the latest observation for one query/plan/source identity in this window. History retains every observation. Only the selected ranking metric is loaded; other metrics remain unavailable.</p>
      <label>Database <select value={database} onChange={event => { selectionRequest.current?.abort(); historyRequest.current?.abort(); setDatabase(event.target.value); setPlan(undefined); setHistory(undefined); setHistorySelection(undefined); }}><option value="">All loaded databases</option>{databaseOptions.map(option => <option key={option.databaseId} value={option.databaseId}>{option.databaseName ?? `Database ID ${option.databaseId}`}</option>)}</select></label>
      <label>Source <select value={source} onChange={event => { selectionRequest.current?.abort(); historyRequest.current?.abort(); setSource(event.target.value); setPlan(undefined); setHistory(undefined); setHistorySelection(undefined); }}><option value="mixed">All sources · evidence</option><option value="query_store">Query Store · interval</option><option value="plan_cache">Plan cache · row semantics</option></select></label>
      {selectedItem && <div className="kpi-grid">{[["CPU ms", selectedMetrics?.cpuMilliseconds], ["Duration ms", selectedMetrics?.durationMilliseconds], ["Executions", selectedMetrics?.executions], ["Logical reads", selectedMetrics?.logicalReads]].map(([label,value]) => <section className="kpi" key={label}><p>{label}</p><strong>{value ?? "—"}</strong><small>Selected observation · {selectedItem.semantics}</small></section>)}</div>}
      <div className="query-investigation"><section className="panel"><h2>Query CPU and duration history</h2>
        {history ? (source === "mixed" ? <p>Select Query Store or Plan cache to chart one source with consistent semantics.</p> :
          <ObservationChart
            label={`Query activity (ms) · ${selectedItem?.semantics ?? "semantics unavailable"}`}
            fromUtc={historySelection?.fromUtc ?? timeWindow.fromUtc}
            toUtc={historySelection?.toUtc ?? timeWindow.toUtc}
            series={[
              { id: "duration", label: "Duration (ms)", items: history.items.filter(x => x.source === source && x.semantics === selectedItem?.semantics).map(x => ({ time: x.intervalEndUtc, value: x.metrics.durationMilliseconds ?? null })) },
              { id: "cpu", label: "CPU (ms)", items: history.items.filter(x => x.source === source && x.semantics === selectedItem?.semantics).map(x => ({ time: x.intervalEndUtc, value: x.metrics.cpuMilliseconds ?? null })) },
            ]}
            onSelectWindow={onSelectWindow}
          />) : <p>Select a query to load its bounded history.</p>}
      </section><section className="panel"><h2>Selected query</h2><p className="fingerprint">{historySelection?.queryFingerprint ?? "No query selected"}</p><p>{displayDatabase(historySelection?.databaseId)} · {source.replaceAll("_", " ")}</p><p>{selectedItem?.semantics ?? "Semantics unavailable"} · {selectedItem?.coverage ?? "Coverage unavailable"}</p><p className="fingerprint">Plan: {plan?.planFingerprint ?? "Metadata unavailable"}</p><p>Query text unavailable</p><p>Plan content unavailable</p></section></div>
      <div className="table-scroll"><table><caption>Bounded ranked observations · CPU and duration in milliseconds · unavailable values are not zero</caption><thead><tr>{["Database / query", "Source", "Semantics", "CPU ms", "Duration ms", "Executions", "Reads / writes / rows", "Coverage"].map(label => <th scope="col" key={label}>{label}</th>)}</tr></thead><tbody>
        {rows.map(x => <tr key={`${x.collectionRunId}-${x.observationKey}`} className={historySelection?.queryFingerprint === x.query.queryFingerprint && historySelection.databaseId === x.query.databaseId ? "selected-row" : ""}><td><button className="fingerprint" type="button" onClick={() => select(x.query.databaseId, x.query.queryFingerprint, x.intervalStartUtc, x.intervalEndUtc, x.plan?.planFingerprint)}>{displayDatabase(x.query.databaseId)} / {x.query.queryFingerprint.slice(0, 12)}…</button></td><td>{x.source} · {x.sourceState}</td><td>{x.semantics}</td><td>{x.metrics.cpuMilliseconds ?? "—"}</td><td>{x.metrics.durationMilliseconds ?? "—"}</td><td>{x.metrics.executions ?? "—"}</td><td>{x.metrics.logicalReads ?? "—"} / {x.metrics.writes ?? "—"} / {x.metrics.rows ?? "—"}</td><td>{x.coverage} · {x.fresh ? "fresh at snapshot" : "stale"}{x.truncated ? " · truncated" : ""}</td></tr>)}
      </tbody></table>{rows.length === 0 && <p className="empty-state">No {source.replaceAll("_", " ")} observations in this bounded page.</p>}</div>

      {history === undefined ? null : <div className="activity-evidence"><details><summary>Bounded history observations</summary>{history.items.length === 0 ? <p>No activity in the selected interval.</p> : history.items.map(x => <p key={`${x.collectionRunId}-${x.observationKey}`}>{x.intervalEndUtc}: CPU {String(x.metrics.cpuMilliseconds ?? "—")} · duration {String(x.metrics.durationMilliseconds ?? "—")} · executions {String(x.metrics.executions ?? "—")} · reads {String(x.metrics.logicalReads ?? "—")} · writes {String(x.metrics.writes ?? "—")} · rows {String(x.metrics.rows ?? "—")} · {x.source} · {x.semantics}</p>)}{history.nextCursor === undefined || historySelection === undefined ? null : <button className="secondary-button" type="button" onClick={() => loadHistory(historySelection, history.nextCursor)}>Next history page</button>}</details></div>}
      {plan === undefined ? null : <p className="activity-evidence"><strong>Plan metadata:</strong> {plan.planFingerprint.slice(0, 12)}… · {plan.source} · {plan.coverage} · content unavailable</p>}
    </>}
  </section>;
}
