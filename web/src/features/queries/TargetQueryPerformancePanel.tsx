import { metricsForObservation, queryPerformanceDatabaseOptions } from "./querySelection";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { ObservationChart } from "../../components/ObservationChart";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { useEffect, useRef, useState } from "react";
import { getQueryPerformanceHistory, getQueryPerformancePlan, getQueryPerformancePlanContent, getQueryPerformancePlanWaits, getQueryPerformanceStatus, getQueryPerformanceText, getQueryPerformanceTop } from "./queryPerformanceApi";
import type { QueryWindow } from "./queryWindowModel";
import type { QueryPerformanceHistoryPage, QueryPerformanceItem, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformanceStatus } from "./queryPerformanceTypes";

const waitCategoryNames: Record<number, string> = {
  0: "Unknown", 1: "CPU", 2: "Worker thread", 3: "Lock", 4: "Latch",
  5: "Buffer latch", 6: "Buffer I/O", 7: "Compilation", 8: "SQL CLR",
  9: "Mirroring", 10: "Transaction", 11: "Idle", 12: "Preemptive",
  13: "Service Broker", 14: "Transaction log I/O", 15: "Network I/O",
  16: "Parallelism", 17: "Memory", 18: "User wait", 19: "Tracing",
  20: "Full-text search",
};

export function TargetQueryPerformancePanel({ instanceId, displayName, onClose, timeWindow, onSelectWindow, refresh, canReadText }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void;
  readonly timeWindow: QueryWindow;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly refresh: number;
  readonly canReadText: boolean;
}) {
  const { mode } = useTimeDisplay();
  const timeLabel = (value?: string) => value ? formatDisplayTime(value, mode) : "Time unavailable";
  const selectionRequest = useRef<AbortController | null>(null);
  const historyRequest = useRef<AbortController | null>(null);
  const pagingRequest = useRef<AbortController | null>(null);
  const textRequest = useRef<AbortController | null>(null);
  const planContentRequest = useRef<AbortController | null>(null);
  const waitRequest = useRef<AbortController | null>(null);
  const activeQuery = useRef<{ readonly instanceId: string; readonly ranking: QueryPerformanceMetric; readonly fromUtc: string; readonly toUtc: string; readonly refresh: number } | undefined>(undefined);
  const [ranking, setRanking] = useState<QueryPerformanceMetric>("cpu");
  const [paging, setPaging] = useState(false);
  const [database, setDatabase] = useState("");
  // The live collector can legitimately fall back to plan cache when Query Store
  // is empty or unavailable. Show the bounded evidence from both sources by
  // default so an environment with that fallback does not look like it has no
  // data; users can still narrow the view with the source filter below.
  const [source, setSource] = useState("mixed");
  useEffect(() => () => { selectionRequest.current?.abort(); historyRequest.current?.abort(); pagingRequest.current?.abort(); textRequest.current?.abort(); planContentRequest.current?.abort(); waitRequest.current?.abort(); }, [instanceId, ranking, timeWindow.fromUtc, timeWindow.toUtc]);
  const [page, setPage] = useState<QueryPerformancePage>();
  const [status, setStatus] = useState<QueryPerformanceStatus>();
  const [history, setHistory] = useState<QueryPerformanceHistoryPage>();
  const [historySelection, setHistorySelection] = useState<{ databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }>();
  const [plan, setPlan] = useState<QueryPerformancePlan>();
  const [message, setMessage] = useState<string>();
  const [textResult, setTextResult] = useState<{ runId: string; state: "loading" | "available" | "unavailable" | "error"; text?: string; message?: string }>();
  const [planResult, setPlanResult] = useState<{ runId: string; planFingerprint: string; state: "loading" | "available" | "unavailable" | "error"; xml?: string; message?: string }>();
  const [waitResult, setWaitResult] = useState<{ runId: string; planFingerprint: string; state: "loading" | "available" | "unavailable" | "error"; capturedAtUtc?: string; categories?: readonly { readonly category: number; readonly waitMilliseconds: number }[]; message?: string }>();
  useEffect(() => { if (!canReadText) { textRequest.current?.abort(); planContentRequest.current?.abort(); setTextResult(undefined); setPlanResult(undefined); } }, [canReadText]);
  const clearContent = () => { textRequest.current?.abort(); planContentRequest.current?.abort(); waitRequest.current?.abort(); setTextResult(undefined); setPlanResult(undefined); setWaitResult(undefined); };
  const openText = (item: QueryPerformanceItem) => {
    if (!canReadText || item.source !== "query_store") return;
    planContentRequest.current?.abort(); setPlanResult(undefined);
    textRequest.current?.abort();
    const controller = new AbortController(); textRequest.current = controller;
    setTextResult({ runId: item.collectionRunId, state: "loading" });
    void getQueryPerformanceText(instanceId, item.query.databaseId, item.query.queryFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted) setTextResult({ runId: item.collectionRunId, state: result.status, text: result.text ?? undefined }); })
      .catch((error: unknown) => { if (!controller.signal.aborted) setTextResult({ runId: item.collectionRunId, state: "error", message: error instanceof Error ? error.message : "Query text could not be opened." }); });
  };
  const openPlan = (item: QueryPerformanceItem) => {
    if (!canReadText || item.source !== "query_store" || !item.plan) return;
    textRequest.current?.abort(); setTextResult(undefined);
    planContentRequest.current?.abort();
    const controller = new AbortController(); planContentRequest.current = controller;
    const planFingerprint = item.plan.planFingerprint;
    setPlanResult({ runId: item.collectionRunId, planFingerprint, state: "loading" });
    void getQueryPerformancePlanContent(instanceId, item.query.databaseId,
      item.query.queryFingerprint, planFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted) setPlanResult({ runId: item.collectionRunId, planFingerprint, state: result.status, xml: result.xml ?? undefined }); })
      .catch((error: unknown) => { if (!controller.signal.aborted) setPlanResult({ runId: item.collectionRunId, planFingerprint, state: "error", message: error instanceof Error ? error.message : "Query plan could not be opened." }); });
  };
  const openWaits = (item: QueryPerformanceItem) => {
    if (item.source !== "query_store" || !item.plan) return;
    waitRequest.current?.abort();
    const controller = new AbortController(); waitRequest.current = controller;
    const planFingerprint = item.plan.planFingerprint;
    setWaitResult({ runId: item.collectionRunId, planFingerprint, state: "loading" });
    void getQueryPerformancePlanWaits(instanceId, item.query.databaseId,
      item.query.queryFingerprint, planFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted) setWaitResult({ runId: item.collectionRunId,
        planFingerprint, state: result.status, capturedAtUtc: result.capturedAtUtc ?? undefined,
        categories: result.categories ?? undefined }); })
      .catch((error: unknown) => { if (!controller.signal.aborted) setWaitResult({ runId: item.collectionRunId,
        planFingerprint, state: "error", message: error instanceof Error ? error.message : "Query waits could not be opened." }); });
  };
  const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
  useEffect(() => {
    const previous = activeQuery.current;
    const queryChanged = previous === undefined || previous.instanceId !== instanceId || previous.ranking !== ranking ||
      (previous.refresh === refresh && (previous.fromUtc !== timeWindow.fromUtc || previous.toUtc !== timeWindow.toUtc));
    activeQuery.current = { instanceId, ranking, fromUtc: timeWindow.fromUtc, toUtc: timeWindow.toUtc, refresh };
    const c = new AbortController(); clearContent(); setPaging(false); setPage(undefined); setStatus(undefined); setMessage(undefined);
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
    clearContent(); setPlan(undefined); setMessage(undefined); setHistorySelection(selection); setHistory(undefined);
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
      clearContent(); setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); setPage(next);
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
    {status === undefined ? null : <div className="activity-evidence"><EvidenceStatus label={`${status.source ?? "unavailable"} evidence · ${status.coverage}`} detail={`${status.sourceState}${status.truncated ? " · truncated" : ""} · repository snapshot ${timeLabel(status.snapshotUtc)}`} tone={status.sourceState === "read_write" && !status.truncated ? "current" : status.sourceState === "unavailable" ? "unavailable" : "warning"} /><div>{status.databaseStatuses.length === 0 ? "No per-database attempts were recorded." : status.databaseStatuses.map(x => <span key={x.databaseId} className="badge">{displayDatabase(x.databaseId)}: {x.status} · {x.sourceState} · fallback {x.fallbackAttempted ? "used" : "not used"} · {x.reason} · {x.lossKind} ({x.minimumLostItems} items, {x.minimumLostBytes} bytes{ x.lossCountIsExact ? ", exact" : ", lower bound"}){x.truncated ? " · truncated" : ""}</span>)}</div></div>}
    {page === undefined && message === undefined ? <p>Loading bounded query evidence…</p> : null}
    {page === undefined ? null : <>
      <p className="activity-evidence">Query Store and plan-cache rows are explicitly labelled; interval and cumulative semantics are not mixed. Protected Query Store text and plan XML can be opened by a QueryTextReader. Repository time: {timeLabel(page.repositoryTimeUtc)}.</p><div className="toolbar"><label>Rank by <select value={ranking} onChange={event => setRanking(event.target.value as QueryPerformanceMetric)}>{metrics.map(metric => <option key={metric}>{metric}</option>)}</select></label><button disabled={!page.nextCursor || paging} onClick={loadMetricPage}>Next ranking page</button></div>
      <p className="activity-evidence">Ranking window: {timeLabel(page.fromUtc)} to {timeLabel(page.toUtc)} · each row is the latest observation for one query/plan/source identity in this window. History retains every observation. Only the selected ranking metric is loaded; other metrics remain unavailable.</p>
      <label>Database <select value={database} onChange={event => { selectionRequest.current?.abort(); historyRequest.current?.abort(); clearContent(); setDatabase(event.target.value); setPlan(undefined); setHistory(undefined); setHistorySelection(undefined); }}><option value="">All loaded databases</option>{databaseOptions.map(option => <option key={option.databaseId} value={option.databaseId}>{option.databaseName ?? `Database ID ${option.databaseId}`}</option>)}</select></label>
      <label>Source <select value={source} onChange={event => { selectionRequest.current?.abort(); historyRequest.current?.abort(); clearContent(); setSource(event.target.value); setPlan(undefined); setHistory(undefined); setHistorySelection(undefined); }}><option value="mixed">All sources · evidence</option><option value="query_store">Query Store · interval</option><option value="plan_cache">Plan cache · row semantics</option></select></label>
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
      </section><section className="panel"><h2>Selected query</h2><p className="fingerprint">{historySelection?.queryFingerprint ?? "No query selected"}</p><p>{displayDatabase(historySelection?.databaseId)} · {source.replaceAll("_", " ")}</p><p>{selectedItem?.semantics ?? "Semantics unavailable"} · {selectedItem?.coverage ?? "Coverage unavailable"}</p><p className="fingerprint">Plan: {plan?.planFingerprint ?? "Metadata unavailable"}</p>
        {selectedItem?.source === "query_store" && canReadText ? <button className="secondary-button" type="button" onClick={() => openText(selectedItem)}>Open query text for selected observation</button> : selectedItem ? <p>{selectedItem.source === "query_store" ? "Query text requires the QueryTextReader role on this server." : "Plan-cache query text is not collected."}</p> : null}
        {canReadText && textResult ? <div role="status" className="query-text-result"><p>{textResult.state === "loading" ? "Opening query text…" : textResult.state === "available" ? `Query text from run ${textResult.runId}` : textResult.state === "unavailable" ? "Query text was not captured for this observation. Check the collector key, Query Store permissions, or source text restrictions." : textResult.message}</p>{textResult.state === "available" && <pre>{textResult.text}</pre>}</div> : null}
        {selectedItem?.source === "query_store" && selectedItem.plan && canReadText ? <button className="secondary-button" type="button" onClick={() => openPlan(selectedItem)}>Open plan XML for selected observation</button> : selectedItem?.source === "query_store" && selectedItem.plan && !canReadText ? <p>Plan XML requires the QueryTextReader role on this server.</p> : null}
        {canReadText && planResult ? <div role="status" className="query-text-result"><p>{planResult.state === "loading" ? "Opening plan XML…" : planResult.state === "available" ? `Plan XML from run ${planResult.runId}` : planResult.state === "unavailable" ? "Plan XML was not captured for this observation. Check the collector key, Query Store permissions, source restrictions, or plan size limit." : planResult.message}</p>{planResult.state === "available" && <pre>{planResult.xml}</pre>}</div> : null}
        {selectedItem?.source === "query_store" && selectedItem.plan ? <button className="secondary-button" type="button" onClick={() => openWaits(selectedItem)}>Show plan waits for selected observation</button> : null}
        {waitResult ? <div role="status" className="activity-evidence"><p>{waitResult.state === "loading" ? "Loading plan waits…" : waitResult.state === "unavailable" ? "Query Store wait capture was unavailable for this plan and run." : waitResult.state === "error" ? waitResult.message : `Query Store wait totals captured ${timeLabel(waitResult.capturedAtUtc)} for run ${waitResult.runId}. Repeated interval snapshots can overlap.`}</p>{waitResult.state === "available" && (waitResult.categories?.length ? <ul>{waitResult.categories.map(row => <li key={row.category}>{waitCategoryNames[row.category] ?? `Category ${row.category}`}: {row.waitMilliseconds} ms</li>)}</ul> : <p>No nonzero wait categories were captured for this plan and run.</p>)}</div> : null}
      </section></div>
      <div className="table-scroll"><table><caption>Bounded ranked observations · CPU and duration in milliseconds · unavailable values are not zero</caption><thead><tr>{["Database / query", "Source", "Semantics", "CPU ms", "Duration ms", "Executions", "Reads / writes / rows", "Coverage"].map(label => <th scope="col" key={label}>{label}</th>)}</tr></thead><tbody>
        {rows.map(x => <tr key={`${x.collectionRunId}-${x.observationKey}`} className={historySelection?.queryFingerprint === x.query.queryFingerprint && historySelection.databaseId === x.query.databaseId ? "selected-row" : ""}><td><button className="fingerprint" type="button" onClick={() => select(x.query.databaseId, x.query.queryFingerprint, x.intervalStartUtc, x.intervalEndUtc, x.plan?.planFingerprint)}>{displayDatabase(x.query.databaseId)} / {x.query.queryFingerprint.slice(0, 12)}…</button></td><td>{x.source} · {x.sourceState}</td><td>{x.semantics}</td><td>{x.metrics.cpuMilliseconds ?? "—"}</td><td>{x.metrics.durationMilliseconds ?? "—"}</td><td>{x.metrics.executions ?? "—"}</td><td>{x.metrics.logicalReads ?? "—"} / {x.metrics.writes ?? "—"} / {x.metrics.rows ?? "—"}</td><td>{x.coverage} · {x.fresh ? "fresh at snapshot" : "stale"}{x.truncated ? " · truncated" : ""}</td></tr>)}
      </tbody></table>{rows.length === 0 && <p className="empty-state">No {source.replaceAll("_", " ")} observations in this bounded page.</p>}</div>

      {history === undefined ? null : <div className="activity-evidence"><details><summary>Bounded history observations</summary>{history.items.length === 0 ? <p>No activity in the selected interval.</p> : history.items.map(x => <p key={`${x.collectionRunId}-${x.observationKey}`}>{timeLabel(x.intervalEndUtc)}: CPU {String(x.metrics.cpuMilliseconds ?? "—")} · duration {String(x.metrics.durationMilliseconds ?? "—")} · executions {String(x.metrics.executions ?? "—")} · reads {String(x.metrics.logicalReads ?? "—")} · writes {String(x.metrics.writes ?? "—")} · rows {String(x.metrics.rows ?? "—")} · {x.source} · {x.semantics} {canReadText && x.source === "query_store" ? <button className="secondary-button" type="button" onClick={() => openText(x)}>Open text for this run</button> : null} {canReadText && x.source === "query_store" && x.plan ? <button className="secondary-button" type="button" onClick={() => openPlan(x)}>Open plan XML for this run</button> : null} {x.source === "query_store" && x.plan ? <button className="secondary-button" type="button" onClick={() => openWaits(x)}>Show waits for this run</button> : null}</p>)}{history.nextCursor === undefined || historySelection === undefined ? null : <button className="secondary-button" type="button" onClick={() => loadHistory(historySelection, history.nextCursor)}>Next history page</button>}</details></div>}
      {plan === undefined ? null : <p className="activity-evidence"><strong>Plan metadata:</strong> {plan.planFingerprint.slice(0, 12)}… · {plan.source} · {plan.coverage} · latest run XML {plan.contentAvailable ? "captured" : "unavailable"}</p>}
    </>}
  </section>;
}
