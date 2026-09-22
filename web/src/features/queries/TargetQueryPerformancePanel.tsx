import { historyForObservation, metricsForObservation, queryPerformanceDatabaseOptions, sameQueryObservation } from "./querySelection";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { RequestStatus } from "../../components/RequestStatus";
import { ObservationChart } from "../../components/ObservationChart";
import { useEffect, useState } from "react";
import { getQueryPerformanceHistory, getQueryPerformancePlan, getQueryPerformanceStatus, getQueryPerformanceTop } from "./queryPerformanceApi";
import type { QueryWindow } from "./queryWindowModel";
import type { QueryPerformanceFilters, QueryPerformanceHistoryPage, QueryPerformanceItem, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformanceStatus } from "./queryPerformanceTypes";

type Props = { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly timeWindow: QueryWindow; readonly refresh?: number };
const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
const errorMessage = (error: unknown, fallback: string) => error instanceof Error ? error.message : fallback;
const evidenceLabels: Readonly<Record<string, string>> = {
  query_store: "Query Store", plan_cache: "Plan cache", mixed: "Mixed",
  query_store_interval: "Query Store interval", plan_cache_cumulative: "Plan cache cumulative", plan_cache_delta: "Plan cache delta",
  read_write: "Read/write", read_only: "Read only", read_failure: "Read failed",
  query_store_rows: "Query Store rows", plan_cache_rows: "Plan cache rows", query_store_read: "Query Store read",
  cpu: "CPU", duration: "Duration", executions: "Executions", logical_reads: "Logical reads", writes: "Writes", rows: "Rows",
};
const evidenceLabel = (value: string): string => evidenceLabels[value] ?? value.replaceAll("_", " ").replace(/^./, letter => letter.toUpperCase());

// Remount only when the investigation scope changes. Same-scope refresh preserves state.
export function TargetQueryPerformancePanel(props: Props) {
  return <QueryInvestigation key={`${props.instanceId}:${props.timeWindow.fromUtc}:${props.timeWindow.toUtc}`} {...props} />;
}

function QueryInvestigation({ instanceId, displayName, onClose, timeWindow, refresh = 0 }: Props) {
  const [ranking, setRanking] = useState<QueryPerformanceMetric>("cpu");
  const [database, setDatabase] = useState("");
  const [source, setSource] = useState<"mixed" | "query_store" | "plan_cache">("mixed");
  const [page, setPage] = useState<QueryPerformancePage>();
  const [pageCursors, setPageCursors] = useState<(string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loadedPageIndex, setLoadedPageIndex] = useState(0);
  const [pageLoading, setPageLoading] = useState(true);
  const [pageError, setPageError] = useState<string>();
  const [pageRetry, setPageRetry] = useState(0);
  const [status, setStatus] = useState<QueryPerformanceStatus>();
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState<string>();
  const [statusRetry, setStatusRetry] = useState(0);
  const [selectedItem, setSelectedItem] = useState<QueryPerformanceItem>();
  const [history, setHistory] = useState<QueryPerformanceHistoryPage>();
  const [historyCursors, setHistoryCursors] = useState<(string | undefined)[]>([undefined]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [loadedHistoryIndex, setLoadedHistoryIndex] = useState(0);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string>();
  const [historyRetry, setHistoryRetry] = useState(0);
  const [plan, setPlan] = useState<QueryPerformancePlan>();
  const [planError, setPlanError] = useState<string>();
  const [planRetry, setPlanRetry] = useState(0);
  const pageCursor = pageCursors[pageIndex];
  const historyCursor = historyCursors[historyIndex];
  const selectedKey = selectedItem === undefined ? undefined : `${selectedItem.collectionRunId}:${selectedItem.observationKey}`;

  useEffect(() => {
    const controller = new AbortController();
    setPageLoading(true); setPageError(undefined);
    const filters: QueryPerformanceFilters = { databaseId: database ? Number(database) : undefined, source: source === "mixed" ? undefined : source };
    void getQueryPerformanceTop(instanceId, ranking, pageCursor, timeWindow.fromUtc, timeWindow.toUtc, controller.signal, filters)
      .then(value => { if (!controller.signal.aborted) { setPage(value); setLoadedPageIndex(pageIndex); } })
      .catch((error: unknown) => { if (!controller.signal.aborted) setPageError(errorMessage(error, "Query evidence is unavailable.")); })
      .finally(() => { if (!controller.signal.aborted) setPageLoading(false); });
    return () => controller.abort();
  }, [instanceId, ranking, database, source, pageCursor, timeWindow.fromUtc, timeWindow.toUtc, refresh, pageRetry]);

  useEffect(() => {
    const controller = new AbortController();
    setStatusLoading(true); setStatusError(undefined);
    void getQueryPerformanceStatus(instanceId, timeWindow.fromUtc, timeWindow.toUtc, controller.signal)
      .then(value => { if (!controller.signal.aborted) setStatus(value); })
      .catch((error: unknown) => { if (!controller.signal.aborted) setStatusError(errorMessage(error, "Query collection status is unavailable.")); })
      .finally(() => { if (!controller.signal.aborted) setStatusLoading(false); });
    return () => controller.abort();
  }, [instanceId, timeWindow.fromUtc, timeWindow.toUtc, refresh, statusRetry]);

  useEffect(() => {
    if (selectedItem === undefined) return;
    const controller = new AbortController();
    setHistoryLoading(true); setHistoryError(undefined);
    void getQueryPerformanceHistory(instanceId, selectedItem.query.databaseId, selectedItem.query.queryFingerprint, timeWindow.fromUtc, timeWindow.toUtc, controller.signal, historyCursor)
      .then(value => {
        if (controller.signal.aborted) return;
        setHistory(value); setLoadedHistoryIndex(historyIndex);
        // Preserve any full metrics for this exact observation while paging history.
        setSelectedItem(current => {
          const exact = current === undefined ? undefined : value.items.find(row => sameQueryObservation(row, current));
          return current === undefined || exact === undefined ? current : { ...current, metrics: exact.metrics };
        });
      })
      .catch((error: unknown) => { if (!controller.signal.aborted) setHistoryError(errorMessage(error, "Query history is unavailable.")); })
      .finally(() => { if (!controller.signal.aborted) setHistoryLoading(false); });
    return () => controller.abort();
  }, [instanceId, selectedKey, historyCursor, timeWindow.fromUtc, timeWindow.toUtc, refresh, historyRetry]);

  useEffect(() => {
    if (selectedItem?.plan === undefined) return;
    const controller = new AbortController();
    setPlanError(undefined);
    void getQueryPerformancePlan(instanceId, selectedItem.query.databaseId, selectedItem.query.queryFingerprint, selectedItem.plan.planFingerprint, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return;
        if (value.source !== selectedItem.source) { setPlan(undefined); setPlanError("Plan metadata for the selected source is unavailable."); }
        else setPlan(value);
      })
      .catch((error: unknown) => { if (!controller.signal.aborted) setPlanError(errorMessage(error, "Plan metadata is unavailable.")); });
    return () => controller.abort();
  }, [instanceId, selectedKey, refresh, planRetry]);

  const clearSelection = () => { setSelectedItem(undefined); setHistory(undefined); setHistoryError(undefined); setHistoryLoading(false); setPlan(undefined); setPlanError(undefined); setHistoryCursors([undefined]); setHistoryIndex(0); setLoadedHistoryIndex(0); };
  const resetRanking = () => { clearSelection(); setPage(undefined); setPageCursors([undefined]); setPageIndex(0); setLoadedPageIndex(0); };
  const select = (item: QueryPerformanceItem) => { if (selectedItem !== undefined && sameQueryObservation(item, selectedItem)) return; clearSelection(); setSelectedItem(item); };
  const nextRanking = () => { if (page?.nextCursor && !pageLoading) { setPageCursors(previous => [...previous.slice(0, loadedPageIndex + 1), page.nextCursor]); setPageIndex(loadedPageIndex + 1); setPageRetry(value => value + 1); } };
  const nextHistory = () => { if (history?.nextCursor && !historyLoading) { setHistoryCursors(previous => [...previous.slice(0, loadedHistoryIndex + 1), history.nextCursor]); setHistoryIndex(loadedHistoryIndex + 1); setHistoryRetry(value => value + 1); } };
  const selectedMetrics = metricsForObservation(selectedItem, history);
  const series = historyForObservation(selectedItem, history);
  const databaseOptions = queryPerformanceDatabaseOptions(page, status);
  const databaseNames = new Map(databaseOptions.map(option => [option.databaseId, option.databaseName]));
  const displayDatabase = (databaseId: number | undefined) => databaseId === undefined ? "—" : databaseNames.get(databaseId) ?? `Database ID ${databaseId}`;
  const selectedInPage = selectedItem !== undefined && page?.items.some(row => sameQueryObservation(row, selectedItem));

  return <section className="query-performance-screen" aria-labelledby="query-performance-heading">
    <div className="health-heading-row"><div><p className="eyebrow">Query performance · read-only evidence</p><h3 id="query-performance-heading">Top queries for {displayName}</h3></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    <p className="activity-evidence">Investigation window: {timeWindow.fromUtc} to {timeWindow.toUtc} · UTC.</p>
    <RequestStatus loading={pageLoading} error={pageError} hasData={page !== undefined} updatedAt={page?.repositoryTimeUtc} label="query evidence" onRetry={() => setPageRetry(value => value + 1)} />
    <RequestStatus loading={statusLoading} error={statusError} hasData={status !== undefined} updatedAt={status?.snapshotUtc} label="query collection status" onRetry={() => setStatusRetry(value => value + 1)} />
    {status === undefined ? null : <div className="activity-evidence"><EvidenceStatus label={`${evidenceLabel(status.source ?? "unavailable")} evidence · ${evidenceLabel(status.coverage)}`} detail={`${evidenceLabel(status.sourceState)}${status.truncated ? " · truncated" : ""} · repository snapshot ${status.snapshotUtc}`} tone={status.sourceState === "read_write" && !status.truncated ? "current" : status.sourceState === "unavailable" ? "unavailable" : "warning"} /><details><summary>Per-database collection status</summary>{status.databaseStatuses.length === 0 ? <p>No per-database attempts were recorded.</p> : status.databaseStatuses.map(x => <p key={x.databaseId}>{displayDatabase(x.databaseId)}: {evidenceLabel(x.status)} · {evidenceLabel(x.sourceState)} · fallback {x.fallbackAttempted ? "used" : "not used"} · {evidenceLabel(x.reason)} · {evidenceLabel(x.lossKind)} ({x.minimumLostItems} items, {x.minimumLostBytes} bytes{x.lossCountIsExact ? ", exact" : ", lower bound"}){x.truncated ? " · truncated" : ""}</p>)}</details></div>}
    <div className="toolbar">
      <label>Rank by <select value={ranking} onChange={event => { resetRanking(); setRanking(event.target.value as QueryPerformanceMetric); }}>{metrics.map(metric => <option key={metric} value={metric}>{evidenceLabel(metric)}</option>)}</select></label>
      <label>Database <select value={database} onChange={event => { resetRanking(); setDatabase(event.target.value); }}><option value="">All databases</option>{databaseOptions.map(option => <option key={option.databaseId} value={option.databaseId}>{option.databaseName ?? `Database ID ${option.databaseId}`}</option>)}</select></label>
      <label>Source <select value={source} onChange={event => { resetRanking(); setSource(event.target.value as typeof source); }}><option value="mixed">All sources · evidence</option><option value="query_store">Query Store · interval</option><option value="plan_cache">Plan cache · row semantics</option></select></label>
    </div>
    {page === undefined ? null : <>
      <p className="activity-evidence">Each row is the latest observation for one query, plan, source, and semantics in this window. Database and source filters apply before ranking. Other metrics load only when the exact observation is available in history; unavailable values are not zero.</p>
      <div className="toolbar" aria-label="Ranking pagination"><button type="button" disabled={loadedPageIndex === 0 || pageLoading} onClick={() => { setPageIndex(0); setPageRetry(value => value + 1); }}>First ranking page</button><button type="button" disabled={loadedPageIndex === 0 || pageLoading} onClick={() => { setPageIndex(loadedPageIndex - 1); setPageRetry(value => value + 1); }}>Previous ranking page</button><span>Ranking page {loadedPageIndex + 1}</span><button type="button" disabled={!page.nextCursor || pageLoading} onClick={nextRanking}>Next ranking page</button></div>
      <div className="table-scroll"><table aria-busy={pageLoading}><caption>Bounded ranked observations · CPU and duration in milliseconds</caption><thead><tr>{["Database / query", "Source", "Semantics", "CPU ms", "Duration ms", "Executions", "Reads / writes / rows", "Coverage"].map(label => <th scope="col" key={label}>{label}</th>)}</tr></thead><tbody>
        {page.items.map(item => { const selected = selectedItem !== undefined && sameQueryObservation(item, selectedItem); return <tr key={`${item.collectionRunId}-${item.observationKey}`} className={selected ? "selected-row" : ""}><td><button className="fingerprint" type="button" aria-pressed={selected} onClick={() => select(item)}>{displayDatabase(item.query.databaseId)} / {item.query.queryFingerprint.slice(0, 12)}…</button></td><td>{evidenceLabel(item.source)} · {evidenceLabel(item.sourceState)}</td><td>{evidenceLabel(item.semantics)}</td><td>{item.metrics.cpuMilliseconds ?? "—"}</td><td>{item.metrics.durationMilliseconds ?? "—"}</td><td>{item.metrics.executions ?? "—"}</td><td>{item.metrics.logicalReads ?? "—"} / {item.metrics.writes ?? "—"} / {item.metrics.rows ?? "—"}</td><td>{evidenceLabel(item.coverage)} · {item.fresh ? "fresh at snapshot" : "stale"}{item.truncated ? " · truncated" : ""}</td></tr>; })}
      </tbody></table>{page.items.length === 0 && <p className="empty-state">No observations match this investigation window and filters.</p>}</div>
    </>}
    {selectedItem === undefined ? <p>Select a ranked observation to inspect its metrics, plan, and history.</p> : <>
      {!selectedInPage && <p className="activity-evidence">The selected observation is retained from an earlier ranking page or snapshot.</p>}
      <div className="kpi-grid" aria-label="Selected observation metrics">{[["CPU ms", selectedMetrics?.cpuMilliseconds], ["Duration ms", selectedMetrics?.durationMilliseconds], ["Executions", selectedMetrics?.executions], ["Logical reads", selectedMetrics?.logicalReads]].map(([label, value]) => <section className="kpi" key={label}><p>{label}</p><strong>{value ?? "—"}</strong><small>Selected observation · {evidenceLabel(selectedItem.semantics)}</small></section>)}</div>
      <RequestStatus loading={historyLoading} error={historyError} hasData={history !== undefined} updatedAt={history?.repositoryTimeUtc} label="selected query history" onRetry={() => setHistoryRetry(value => value + 1)} />
      <div className="query-investigation"><section className="panel"><h2>Query duration history</h2><p>{evidenceLabel(selectedItem.source)} · {evidenceLabel(selectedItem.semantics)} · selected plan only</p>{history === undefined ? <p>Loading the selected query's bounded history…</p> : series.length === 0 ? <p>No observations for the selected plan, source, and semantics on this history page.</p> : <ObservationChart items={series.map(item => ({ time: item.intervalEndUtc, value: item.metrics.durationMilliseconds ?? null }))} label={`Duration (ms) · ${evidenceLabel(selectedItem.semantics)}`} />}</section><section className="panel"><h2>Selected query</h2><p>{displayDatabase(selectedItem.query.databaseId)} · {evidenceLabel(selectedItem.source)}</p><p>{evidenceLabel(selectedItem.semantics)} · {evidenceLabel(selectedItem.coverage)}</p><p>Observed interval: {selectedItem.intervalStartUtc} to {selectedItem.intervalEndUtc}</p><details><summary>Query and plan identifiers</summary><p className="fingerprint">Query: {selectedItem.query.queryFingerprint}</p><p className="fingerprint">Plan: {selectedItem.plan?.planFingerprint ?? "Unavailable"}</p><p className="fingerprint">Observation: {selectedItem.collectionRunId} / {selectedItem.observationKey}</p></details><p>Query text and plan content unavailable</p>{plan === undefined ? null : <p>Plan metadata: {evidenceLabel(plan.source)} · {evidenceLabel(plan.coverage)} · {plan.observedAtUtc}</p>}<RequestStatus loading={false} error={planError} hasData={plan !== undefined} label="plan metadata" onRetry={() => setPlanRetry(value => value + 1)} /></section></div>
      {history === undefined ? null : <div className="activity-evidence"><div className="toolbar" aria-label="History pagination"><button type="button" disabled={loadedHistoryIndex === 0 || historyLoading} onClick={() => { setHistoryIndex(0); setHistoryRetry(value => value + 1); }}>First history page</button><button type="button" disabled={loadedHistoryIndex === 0 || historyLoading} onClick={() => { setHistoryIndex(loadedHistoryIndex - 1); setHistoryRetry(value => value + 1); }}>Previous history page</button><span>History page {loadedHistoryIndex + 1}</span><button type="button" disabled={!history.nextCursor || historyLoading} onClick={nextHistory}>Next history page</button></div><p>Pages traverse all observations for this query; the chart and details show only the selected plan, source, and semantics.</p><details><summary>Selected series history observations</summary>{series.length === 0 ? <p>No matching observations on this page.</p> : series.map(item => <p key={`${item.collectionRunId}-${item.observationKey}`}>{item.intervalEndUtc}: CPU {item.metrics.cpuMilliseconds ?? "—"} · duration {item.metrics.durationMilliseconds ?? "—"} · executions {item.metrics.executions ?? "—"} · reads {item.metrics.logicalReads ?? "—"} · writes {item.metrics.writes ?? "—"} · rows {item.metrics.rows ?? "—"} · {evidenceLabel(item.source)} · {evidenceLabel(item.semantics)}</p>)}</details></div>}
    </>}
  </section>;
}
