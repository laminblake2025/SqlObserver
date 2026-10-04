import { ApiRequestError } from "../../apiClient.ts";
import { QueryReadAccessContext, useQueryReadBoundary } from "./useQueryReadAccess.tsx";
import { PlanCacheIntervalPanel } from "./PlanCacheIntervalPanel";
import { invalidQuerySelectionMessage } from "../../dashboardModel";
import { metricsForObservation, queryPerformanceDatabaseOptions } from "./querySelection";
import { EvidenceStatus, Tabs } from "../../components/DiagnosticUi";
import { ObservationChart } from "../../components/ObservationChart";
import { QueryStoreGroupTrendPanel, type GroupTrendState } from "./QueryStoreGroupTrendPanel";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { useEffect, useRef, useState } from "react";
import { getQueryPerformanceHistory, getQueryPerformancePlan, getQueryPerformancePlanContent, getQueryPerformancePlanWaits, getQueryPerformanceStatus, getQueryPerformanceText, getQueryPerformanceTop, getQueryStoreGroupPlanTrend, getQueryStoreTopWaits, getQueryStorePlanRegressions } from "./queryPerformanceApi";
import type { QueryRankingSource, QueryView, QueryViewSelection } from "./queryViewModel";
import { queryPlanTrendContext } from "./queryViewModel";
import type { QueryWindow } from "./queryWindowModel";
import type { QueryPerformanceHistoryPage, QueryPerformanceItem, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformanceStatus, QueryStoreWaitRanking, QueryStorePlanRegressionList } from "./queryPerformanceTypes";
import { queryStoreWaitCategoryNames as waitCategoryNames } from "./queryStoreWaitCategories";
import { RankingTrend } from "./RankingTrend.tsx";
import { RankingTrendProvider } from "./useRankingTrends.tsx";
import { queryStoreRankingTrendRequest, rankingTrendIdentityCounts, rankingTrendRequestKey } from "./rankingTrendModel.ts";

const metricLabels: Readonly<Record<QueryPerformanceMetric, string>> = {
  cpu: "CPU time (ms)", duration: "Duration (ms)", executions: "Executions",
  logical_reads: "Logical reads", writes: "Writes", rows: "Rows",
};
interface ContentBinding { readonly context: string; readonly observation: string; readonly runId: string; }
const contentObservationKey = (item: QueryPerformanceItem) => JSON.stringify([
  item.query.databaseId, item.query.queryFingerprint, item.plan?.planFingerprint,
  item.source, item.semantics, item.collectionRunId, item.observationKey,
]);
// APIs validate these UTC timestamps; canonicalize precision without rounding.
const contentTimeKey = (value: string) => value.replace(/(?:Z|\+00:00)$/u, "")
  .split(".").map((part, index) => index === 1 ? part.padEnd(7, "0") : part).join(".")
  + (value.includes(".") ? "Z" : ".0000000Z");

// A receipt belongs to the render context that dispatched it. Hide old evidence
// immediately when that context changes, before passive effects cancel reads.
function useBoundState<T>(context: string): [T | undefined, (value: T | undefined) => void] {
  const [received, setReceived] = useState<{ context: string; value: T }>();
  return [received?.context === context ? received.value : undefined,
    value => setReceived(value === undefined ? undefined : { context, value })];
}

export function TargetQueryPerformancePanel({ instanceId, displayName, onClose, timeWindow, onSelectWindow, onSelectQuery, refresh, canReadText, initialQuerySelection, selectionInvalid = false, viewSelection, onViewSelectionChange, selectedDatabaseId, onSelectDatabase }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void;
  readonly selectedDatabaseId?: number;
  readonly onSelectDatabase: (databaseId: number | undefined) => void;
  readonly timeWindow: QueryWindow;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly onSelectQuery: (databaseId: number, queryFingerprint: string,
    planFingerprint?: string, sourceKind?: "query_store" | "plan_cache") => void;
  readonly viewSelection: QueryViewSelection;
  readonly onViewSelectionChange: (selection: QueryViewSelection) => void;
  readonly refresh: number;
  readonly canReadText: boolean;
  readonly selectionInvalid?: boolean;
  readonly initialQuerySelection?: { readonly databaseId: number; readonly queryFingerprint: string;
    readonly planFingerprint?: string; readonly sourceKind?: "query_store" | "plan_cache" };
}) {
  const { mode } = useTimeDisplay();
  const renderedPlanHash = typeof location === "undefined" ? "" : location.hash;
  const timeLabel = (value?: string) => value ? formatDisplayTime(value, mode) : "Time unavailable";
  const selectionRequest = useRef<AbortController | null>(null);
  const historyRequest = useRef<AbortController | null>(null);
  const pagingRequest = useRef<AbortController | null>(null);
  const textRequest = useRef<AbortController | null>(null);
  const planContentRequest = useRef<AbortController | null>(null);
  const waitRequest = useRef<AbortController | null>(null);
  const trendRequest = useRef<AbortController | null>(null);
  const activeQuery = useRef<{ readonly instanceId: string; readonly ranking: QueryPerformanceMetric; readonly fromUtc: string; readonly toUtc: string; readonly refresh: number } | undefined>(undefined);
  const ranking = viewSelection.metric;
  const setRanking = (metric: QueryPerformanceMetric) => onViewSelectionChange({ ...viewSelection, metric });
  const view = viewSelection.view;
  const setView = (next: QueryView) => onViewSelectionChange({ ...viewSelection, view: next });
  const [paging, setPaging] = useState(false);
  const database = selectedDatabaseId === undefined ? "" : String(selectedDatabaseId);
  const rankingSource = viewSelection.sourceFilterInvalid ? "invalid" : viewSelection.sourceFilter ?? "mixed";
  const evidenceContext = JSON.stringify([instanceId.toLowerCase(), contentTimeKey(timeWindow.fromUtc), contentTimeKey(timeWindow.toUtc), refresh]);
  const rankingContext = JSON.stringify([evidenceContext, ranking]);
  const inspectionContext = JSON.stringify([evidenceContext, selectedDatabaseId, selectionInvalid,
    initialQuerySelection?.databaseId, initialQuerySelection?.queryFingerprint,
    initialQuerySelection?.planFingerprint, initialQuerySelection?.sourceKind]);
  const pagingContext = JSON.stringify([instanceId.toLowerCase(), ranking, contentTimeKey(timeWindow.fromUtc),
    contentTimeKey(timeWindow.toUtc), refresh, selectedDatabaseId, rankingSource, view]);
  const currentPagingContext = useRef(pagingContext); currentPagingContext.current = pagingContext;
  useEffect(() => {
    pagingRequest.current?.abort(); pagingRequest.current = null; setPaging(false);
    return () => { pagingRequest.current?.abort(); pagingRequest.current = null; };
  }, [pagingContext]);
  // The inspected history's source is independent of the loaded ranking filter.
  // A history response must never rewrite a deliberate filter or its URL.
  const [selectedSource, setSelectedSource] = useState("mixed");
  const source = initialQuerySelection?.planFingerprint ? "query_store" : initialQuerySelection?.sourceKind ?? selectedSource;
  useEffect(() => () => { selectionRequest.current?.abort(); historyRequest.current?.abort(); pagingRequest.current?.abort(); textRequest.current?.abort(); planContentRequest.current?.abort(); waitRequest.current?.abort(); trendRequest.current?.abort(); }, [instanceId, timeWindow.fromUtc, timeWindow.toUtc]);
  const [page, setPage] = useBoundState<QueryPerformancePage>(rankingContext);
  const [status, setStatus] = useBoundState<QueryPerformanceStatus>(evidenceContext);
  const [waitRanking, setWaitRanking] = useBoundState<QueryStoreWaitRanking>(evidenceContext);
  const [waitRankingError, setWaitRankingError] = useBoundState<string>(evidenceContext);
  const [regressions, setRegressions] = useBoundState<QueryStorePlanRegressionList>(evidenceContext);
  const [regressionError, setRegressionError] = useBoundState<string>(evidenceContext);
  const [receivedHistory, setHistory] = useBoundState<QueryPerformanceHistoryPage>(inspectionContext);
  const [receivedHistorySelection, setHistorySelection] = useBoundState<{ databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string; planFingerprint?: string }>(inspectionContext);
  const historySelection = !selectionInvalid && (selectedDatabaseId === undefined || receivedHistorySelection?.databaseId === selectedDatabaseId)
    && (!initialQuerySelection || receivedHistorySelection?.databaseId === initialQuerySelection.databaseId
      && receivedHistorySelection.queryFingerprint === initialQuerySelection.queryFingerprint
      && receivedHistorySelection.planFingerprint === initialQuerySelection.planFingerprint) ? receivedHistorySelection : undefined;
  const history = historySelection ? receivedHistory : undefined;
  const expectedQuery = initialQuerySelection ?? historySelection;
  const contentContext = JSON.stringify([instanceId.toLowerCase(), contentTimeKey(timeWindow.fromUtc), contentTimeKey(timeWindow.toUtc), refresh,
    selectedDatabaseId, selectionInvalid, initialQuerySelection !== undefined, expectedQuery?.databaseId,
    expectedQuery?.queryFingerprint, expectedQuery?.planFingerprint, source]);
  const currentContentContext = useRef(contentContext);
  currentContentContext.current = contentContext;
  const currentTextAccess = useRef(canReadText);
  currentTextAccess.current = canReadText;
  const contentObservations = useRef(new Set<string>());
  const currentPageItems = page && typeof page.fromUtc === "string" && typeof page.toUtc === "string"
    && contentTimeKey(page.fromUtc) === contentTimeKey(timeWindow.fromUtc)
    && contentTimeKey(page.toUtc) === contentTimeKey(timeWindow.toUtc) ? page.items : [];
  const currentHistoryItems = history && historySelection && contentTimeKey(historySelection.fromUtc) === contentTimeKey(timeWindow.fromUtc)
    && contentTimeKey(historySelection.toUtc) === contentTimeKey(timeWindow.toUtc) ? history.items : [];
  contentObservations.current = new Set([...currentPageItems, ...currentHistoryItems]
    .filter(item => expectedQuery !== undefined && item.query.databaseId === expectedQuery.databaseId
      && item.query.queryFingerprint === expectedQuery.queryFingerprint
      && (expectedQuery.planFingerprint === undefined || item.plan?.planFingerprint === expectedQuery.planFingerprint)
      && item.source === source && (selectedDatabaseId === undefined || item.query.databaseId === selectedDatabaseId))
    .map(contentObservationKey));
  const currentBinding = (binding: ContentBinding, protectedContent = false) => binding.context === currentContentContext.current
    && contentObservations.current.has(binding.observation) && (!protectedContent || currentTextAccess.current);
  const bindContent = (item: QueryPerformanceItem): ContentBinding => ({ context: contentContext,
    observation: contentObservationKey(item), runId: item.collectionRunId });
  const groupSelection = historySelection && expectedQuery?.databaseId === historySelection.databaseId
    && expectedQuery.queryFingerprint === historySelection.queryFingerprint
    && expectedQuery.planFingerprint === historySelection.planFingerprint && source === "query_store"
    && contentTimeKey(historySelection.fromUtc) === contentTimeKey(timeWindow.fromUtc)
    && contentTimeKey(historySelection.toUtc) === contentTimeKey(timeWindow.toUtc) ? historySelection : undefined;
  const groupTrendKey = JSON.stringify([instanceId.toLowerCase(), selectedDatabaseId, expectedQuery?.databaseId,
    expectedQuery?.queryFingerprint, expectedQuery?.planFingerprint, source,
    contentTimeKey(timeWindow.fromUtc), contentTimeKey(timeWindow.toUtc), refresh]);
  const currentGroupTrendKey = useRef(groupTrendKey); currentGroupTrendKey.current = groupTrendKey;
  const [receivedGroupTrend, setGroupTrend] = useState<{ key: string; state: GroupTrendState }>();
  const groupTrend = groupSelection && receivedGroupTrend?.key === groupTrendKey ? receivedGroupTrend.state : undefined;
  const [receivedPlan, setPlan] = useBoundState<QueryPerformancePlan>(inspectionContext);
  const plan = historySelection ? receivedPlan : undefined;
  const [message, setMessage] = useBoundState<string>(JSON.stringify([rankingContext, inspectionContext]));
  const [receivedTextResult, setTextResult] = useState<ContentBinding & { state: "loading" | "available" | "unavailable" | "error"; text?: string; message?: string }>();
  const [receivedPlanResult, setPlanResult] = useState<ContentBinding & { planFingerprint: string; state: "loading" | "available" | "unavailable" | "error"; xml?: string; message?: string }>();
  const [receivedWaitResult, setWaitResult] = useState<ContentBinding & { planFingerprint: string; state: "loading" | "available" | "unavailable" | "error"; capturedAtUtc?: string; categories?: readonly { readonly category: number; readonly waitMilliseconds: number }[]; message?: string }>();
  const textResult = receivedTextResult && currentBinding(receivedTextResult, true) ? receivedTextResult : undefined;
  const planResult = receivedPlanResult && currentBinding(receivedPlanResult, true) ? receivedPlanResult : undefined;
  const waitResult = receivedWaitResult && currentBinding(receivedWaitResult) ? receivedWaitResult : undefined;
  const readAccess = useQueryReadBoundary(instanceId, () => {
    setPage(undefined); setStatus(undefined); setWaitRanking(undefined); setWaitRankingError(undefined); setRegressions(undefined); setRegressionError(undefined);
    setHistory(undefined); setHistorySelection(undefined); setPlan(undefined); setGroupTrend(undefined); setMessage(undefined); clearContent();
    selectionRequest.current?.abort(); historyRequest.current?.abort(); pagingRequest.current?.abort(); waitRequest.current?.abort(); trendRequest.current?.abort(); setPaging(false);
  }, evidenceContext);
  useEffect(() => { if (!canReadText) { textRequest.current?.abort(); planContentRequest.current?.abort(); setTextResult(undefined); setPlanResult(undefined); } }, [canReadText]);
  const clearContent = () => { textRequest.current?.abort(); planContentRequest.current?.abort(); waitRequest.current?.abort(); setTextResult(undefined); setPlanResult(undefined); setWaitResult(undefined); };
  useEffect(() => { clearContent(); }, [contentContext]);
  useEffect(() => {
    selectionRequest.current?.abort(); historyRequest.current?.abort();
    clearContent(); setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); setGroupTrend(undefined);
  }, [selectedDatabaseId]);
  const openText = (item: QueryPerformanceItem) => {
    const binding = bindContent(item);
    if (!currentBinding(binding, true) || item.source !== "query_store") return;
    planContentRequest.current?.abort(); setPlanResult(undefined);
    textRequest.current?.abort();
    const controller = readAccess.begin(); if (!controller) return; textRequest.current = controller;
    setTextResult({ ...binding, state: "loading" });
    void getQueryPerformanceText(instanceId, item.query.databaseId, item.query.queryFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted && textRequest.current === controller && currentBinding(binding, true)) setTextResult({ ...binding, state: result.status, text: result.text ?? undefined }); })
      .catch((error: unknown) => { if (textRequest.current !== controller || !currentBinding(binding, true)) return; if (error instanceof ApiRequestError && error.status === 401 && readAccess.failed(error, controller)) return; if (!controller.signal.aborted && textRequest.current === controller && currentBinding(binding, true)) setTextResult({ ...binding, state: "error", message: error instanceof Error ? error.message : "Query text could not be opened." }); });
  };
  const openPlan = (item: QueryPerformanceItem) => {
    const binding = bindContent(item);
    if (!currentBinding(binding, true) || item.source !== "query_store" || !item.plan) return;
    textRequest.current?.abort(); setTextResult(undefined);
    planContentRequest.current?.abort();
    const controller = readAccess.begin(); if (!controller) return; planContentRequest.current = controller;
    const planFingerprint = item.plan.planFingerprint;
    setPlanResult({ ...binding, planFingerprint, state: "loading" });
    void getQueryPerformancePlanContent(instanceId, item.query.databaseId,
      item.query.queryFingerprint, planFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted && planContentRequest.current === controller && currentBinding(binding, true)) setPlanResult({ ...binding, planFingerprint, state: result.status, xml: result.xml ?? undefined }); })
      .catch((error: unknown) => { if (planContentRequest.current !== controller || !currentBinding(binding, true)) return; if (error instanceof ApiRequestError && error.status === 401 && readAccess.failed(error, controller)) return; if (!controller.signal.aborted && planContentRequest.current === controller && currentBinding(binding, true)) setPlanResult({ ...binding, planFingerprint, state: "error", message: error instanceof Error ? error.message : "Query plan could not be opened." }); });
  };
  const openWaits = (item: QueryPerformanceItem) => {
    const binding = bindContent(item);
    if (!currentBinding(binding) || item.source !== "query_store" || !item.plan) return;
    waitRequest.current?.abort();
    const controller = readAccess.begin(); if (!controller) return; waitRequest.current = controller;
    const planFingerprint = item.plan.planFingerprint;
    setWaitResult({ ...binding, planFingerprint, state: "loading" });
    void getQueryPerformancePlanWaits(instanceId, item.query.databaseId,
      item.query.queryFingerprint, planFingerprint, item.collectionRunId, controller.signal)
      .then(result => { if (!controller.signal.aborted && waitRequest.current === controller && currentBinding(binding)) setWaitResult({ ...binding,
        planFingerprint, state: result.status, capturedAtUtc: result.capturedAtUtc ?? undefined,
        categories: result.categories ?? undefined }); })
      .catch((error: unknown) => { if (waitRequest.current !== controller || !currentBinding(binding)) return; if (readAccess.failed(error, controller)) return; if (!controller.signal.aborted && waitRequest.current === controller && currentBinding(binding)) setWaitResult({ ...binding,
        planFingerprint, state: "error", message: error instanceof Error ? error.message : "Query waits could not be opened." }); });
  };
  const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"];
  useEffect(() => {
    const controller = readAccess.begin(); if (!controller) return;
    setWaitRanking(undefined);
    setWaitRankingError(undefined);
    void getQueryStoreTopWaits(instanceId, timeWindow.fromUtc, timeWindow.toUtc, controller.signal)
      .then(value => { if (!controller.signal.aborted) setWaitRanking(value); })
      .catch((error: unknown) => { if (readAccess.failed(error, controller)) return;
        if (!controller.signal.aborted) setWaitRankingError(error instanceof Error
          ? error.message : "Query wait ranking is unavailable.");
      });
    return () => controller.abort();
  }, [instanceId, timeWindow.fromUtc, timeWindow.toUtc, refresh, readAccess.revision]);
  useEffect(() => {
    const controller = readAccess.begin(); if (!controller) return;
    setRegressions(undefined);
    setRegressionError(undefined);
    void getQueryStorePlanRegressions(instanceId, timeWindow.fromUtc, timeWindow.toUtc, controller.signal)
      .then(value => { if (!controller.signal.aborted) setRegressions(value); })
      .catch((error: unknown) => { if (readAccess.failed(error, controller)) return;
        if (!controller.signal.aborted) setRegressionError(error instanceof Error
          ? error.message : "Plan regression comparison is unavailable.");
      });
    return () => controller.abort();
  }, [instanceId, timeWindow.fromUtc, timeWindow.toUtc, refresh, readAccess.revision]);
  useEffect(() => {
    trendRequest.current?.abort();
    const selection = groupSelection;
    if (!selection?.planFingerprint || selectedDatabaseId !== undefined && selection.databaseId !== selectedDatabaseId) { setGroupTrend(undefined); return; }
    const controller = readAccess.begin(); if (!controller) return; trendRequest.current = controller;
    setGroupTrend({ key: groupTrendKey, state: { status: "loading" } });
    void getQueryStoreGroupPlanTrend(instanceId, selection.databaseId,
      selection.queryFingerprint, selection.planFingerprint,
      selection.fromUtc, selection.toUtc, controller.signal)
      .then(trend => { if (!controller.signal.aborted && trendRequest.current === controller && currentGroupTrendKey.current === groupTrendKey) setGroupTrend({ key: groupTrendKey, state: { status: "ready", trend } }); })
      .catch((error: unknown) => { if (readAccess.failed(error, controller)) return; if (!controller.signal.aborted && trendRequest.current === controller && currentGroupTrendKey.current === groupTrendKey) setGroupTrend({ key: groupTrendKey, state: { status: "error",
        message: error instanceof Error ? error.message : "Plan delta trend could not be loaded." } }); });
    return () => controller.abort();
  }, [groupTrendKey, groupSelection, readAccess.revision]);
  useEffect(() => {
    const previous = activeQuery.current;
    const queryChanged = previous === undefined || previous.instanceId !== instanceId || previous.ranking !== ranking ||
      (previous.refresh === refresh && (previous.fromUtc !== timeWindow.fromUtc || previous.toUtc !== timeWindow.toUtc));
    activeQuery.current = { instanceId, ranking, fromUtc: timeWindow.fromUtc, toUtc: timeWindow.toUtc, refresh };
    pagingRequest.current?.abort(); pagingRequest.current = null; setPaging(false);
    const c = readAccess.begin(); if (!c) return; setPage(undefined); setStatus(undefined); setMessage(undefined);
    if (queryChanged && !initialQuerySelection) { setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); }
    void getQueryPerformanceTop(instanceId, ranking, undefined, timeWindow.fromUtc, timeWindow.toUtc, c.signal).then(value => { if (!c.signal.aborted) setPage(value); }).catch((e: unknown) => { if (readAccess.failed(e, c)) return; if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance evidence is unavailable."); });
    void getQueryPerformanceStatus(instanceId, timeWindow.fromUtc, timeWindow.toUtc, c.signal).then(value => { if (!c.signal.aborted) setStatus(value); }).catch((e: unknown) => { if (readAccess.failed(e, c)) return; if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query performance status is unavailable."); });
    return () => c.abort();
  }, [instanceId, ranking, timeWindow.fromUtc, timeWindow.toUtc, refresh, readAccess.revision]);
  useEffect(() => {
    if (!page || !historySelection || page.items.some(item => item.query.databaseId === historySelection.databaseId && item.query.queryFingerprint === historySelection.queryFingerprint)
      || waitRanking?.items.some(item => item.databaseId === historySelection.databaseId && item.queryFingerprint === historySelection.queryFingerprint)
      || regressions?.items.some(item => item.databaseId === historySelection.databaseId && item.queryFingerprint === historySelection.queryFingerprint)
      || initialQuerySelection?.databaseId === historySelection.databaseId && initialQuerySelection.queryFingerprint === historySelection.queryFingerprint) return;
    setHistorySelection(undefined);
    setHistory(undefined);
    setPlan(undefined);
  }, [page, historySelection, waitRanking, regressions, initialQuerySelection?.databaseId, initialQuerySelection?.queryFingerprint]);
  useEffect(() => {
    clearContent();
    historyRequest.current?.abort(); selectionRequest.current?.abort();
    if (selectionInvalid || !initialQuerySelection || selectedDatabaseId !== undefined && initialQuerySelection.databaseId !== selectedDatabaseId) {
      setHistorySelection(undefined); setHistory(undefined); setPlan(undefined); setGroupTrend(undefined); setSelectedSource("mixed");
      return;
    }
    const controller = readAccess.begin(); if (!controller) return;
    const selection = { ...initialQuerySelection, fromUtc: timeWindow.fromUtc, toUtc: timeWindow.toUtc };
    historyRequest.current?.abort();
    historyRequest.current = controller;
    selectionRequest.current?.abort();
    selectionRequest.current = controller;
    if (selection.planFingerprint || selection.sourceKind === "query_store") setSelectedSource("query_store");
    else if (selection.sourceKind === "plan_cache") setSelectedSource("plan_cache");
    else setSelectedSource("mixed");
    setHistorySelection(selection);
    setHistory(undefined);
    setPlan(undefined);
    setMessage(undefined);
    void getQueryPerformanceHistory(instanceId, selection.databaseId, selection.queryFingerprint, selection.fromUtc, selection.toUtc, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return;
        setHistory(value);
        if (value.items[0] && !selection.planFingerprint && !selection.sourceKind)
          setSelectedSource(value.items[0].source);
        if (value.items.length === 0) setMessage("No history was observed for this query hash in the selected time window.");
      })
      .catch((error: unknown) => { if (readAccess.failed(error, controller)) return; if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Query history is unavailable."); });
    if (selection.planFingerprint) void getQueryPerformancePlan(instanceId, selection.databaseId,
      selection.queryFingerprint, selection.planFingerprint, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPlan(value); })
      .catch((error: unknown) => { if (readAccess.failed(error, controller)) return; if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Plan metadata is unavailable."); });
    return () => controller.abort();
  }, [instanceId, initialQuerySelection?.databaseId, initialQuerySelection?.queryFingerprint,
      initialQuerySelection?.planFingerprint, initialQuerySelection?.sourceKind,
      timeWindow.fromUtc, timeWindow.toUtc, refresh, selectedDatabaseId, selectionInvalid, readAccess.revision]);
  const loadHistory = (selection: { databaseId: number; queryFingerprint: string; fromUtc: string; toUtc: string }, cursor?: string) => {
    historyRequest.current?.abort();
    const c = readAccess.begin(); if (!c) return; historyRequest.current = c;
    void getQueryPerformanceHistory(instanceId, selection.databaseId, selection.queryFingerprint, selection.fromUtc, selection.toUtc, c.signal, cursor).then(value => { if (!c.signal.aborted) setHistory(value); }).catch((e: unknown) => { if (readAccess.failed(e, c)) return; if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Query history is unavailable."); });
  };
  const select = (databaseId: number, queryFingerprint: string, from: string, to: string, planFingerprint: string | undefined, sourceKind: string) => {
    if (!readAccess.allowed()) return;
    const selectedPlan = sourceKind === "query_store" ? planFingerprint : undefined;
    const selectedSource = sourceKind === "query_store" ? "query_store" : "plan_cache";
    if (initialQuerySelection?.databaseId !== databaseId ||
        initialQuerySelection.queryFingerprint !== queryFingerprint ||
        initialQuerySelection.planFingerprint !== selectedPlan ||
        initialQuerySelection.sourceKind !== selectedSource) {
      clearContent();
      onSelectQuery(databaseId, queryFingerprint, selectedPlan, selectedSource);
      return;
    }
    const selection = { databaseId, queryFingerprint, fromUtc: page?.fromUtc ?? from,
      toUtc: page?.toUtc ?? to, planFingerprint: selectedPlan };
    const c = readAccess.begin(); if (!c) return;
    setSelectedSource(selectedSource);
    selectionRequest.current?.abort(); selectionRequest.current = c;
    clearContent(); setPlan(undefined); setMessage(undefined); setHistorySelection(selection); setHistory(undefined);
    loadHistory(selection);
    if (planFingerprint !== undefined) void getQueryPerformancePlan(instanceId, databaseId, queryFingerprint, planFingerprint, c.signal).then(value => { if (!c.signal.aborted) setPlan(value); }).catch((e: unknown) => { if (readAccess.failed(e, c)) return; if (!c.signal.aborted) setMessage(e instanceof Error ? e.message : "Plan metadata is unavailable."); });
  };
  const loadMetricPage = () => {
    if (!page?.nextCursor || pagingRequest.current) return;
    const context = pagingContext;
    const c = readAccess.begin(); if (!c) return; pagingRequest.current = c;
    const current = () => !c.signal.aborted && pagingRequest.current === c && currentPagingContext.current === context;
    setPaging(true); setMessage(undefined);
    void getQueryPerformanceTop(instanceId, ranking, page.nextCursor, page.fromUtc, page.toUtc, c.signal).then(next => {
      if (current()) setPage(next);
    }).catch((e: unknown) => { if (readAccess.failed(e, c)) return; if (current()) setMessage(e instanceof Error ? e.message : "Query performance page is unavailable."); }).finally(() => {
      if (current()) { pagingRequest.current = null; setPaging(false); }
    });
  };
  const selectedItem = page?.items.find(item => (selectedDatabaseId === undefined || item.query.databaseId === selectedDatabaseId) && item.query.databaseId === historySelection?.databaseId && item.query.queryFingerprint === historySelection.queryFingerprint && (historySelection.planFingerprint === undefined || item.plan?.planFingerprint === historySelection.planFingerprint) && (source === "mixed" || item.source === source));
  const selectedMetrics = metricsForObservation(selectedItem, history);
  const selectedHistoryItem = selectedItem ?? history?.items.find(item => item.source === source
    && (historySelection?.planFingerprint === undefined || item.plan?.planFingerprint === historySelection.planFingerprint));
  const databaseOptions = queryPerformanceDatabaseOptions(page, status);
  if (initialQuerySelection && !databaseOptions.some(option => option.databaseId === initialQuerySelection.databaseId)) {
    databaseOptions.push({ databaseId: initialQuerySelection.databaseId, databaseName: undefined });
  }
  if (selectedDatabaseId !== undefined && !databaseOptions.some(option => option.databaseId === selectedDatabaseId)) {
    databaseOptions.push({ databaseId: selectedDatabaseId, databaseName: undefined });
  }
  const databaseNames = new Map(databaseOptions.map(option => [option.databaseId, option.databaseName]));
  const visibleWaitRanking = waitRanking?.items.filter(item => selectedDatabaseId === undefined || item.databaseId === selectedDatabaseId) ?? [];
  const visibleRegressions = regressions?.items.filter(item => selectedDatabaseId === undefined || item.databaseId === selectedDatabaseId) ?? [];
  const displayDatabase = (databaseId: number | undefined) => databaseId === undefined ? "—" : databaseNames.get(databaseId) ?? `Database ID ${databaseId}`;
  const rows = page?.items.filter(item => (rankingSource === "mixed" || item.source === rankingSource) && (!database || String(item.query.databaseId) === database)) ?? [];
  const runtimeRequests = rows.map(item => item.source === "query_store" ? queryStoreRankingTrendRequest(instanceId, item.query.databaseId, item.query.queryFingerprint, item.plan?.planFingerprint, timeWindow) : undefined);
  const runtimeCounts = rankingTrendIdentityCounts(runtimeRequests);
  const runtimeRequest = (index: number) => {
    const request = runtimeRequests[index]; return request && runtimeCounts.get(rankingTrendRequestKey(request)) === 1 ? request : undefined;
  };
  const waitRequests = visibleWaitRanking.map(item => queryStoreRankingTrendRequest(instanceId, item.databaseId, item.queryFingerprint, item.planFingerprint, timeWindow));
  const waitCounts = rankingTrendIdentityCounts(waitRequests);
  const waitRequestFor = (index: number) => {
    const request = waitRequests[index]; return request && waitCounts.get(rankingTrendRequestKey(request)) === 1 ? request : undefined;
  };
  const rowTrendContext = JSON.stringify([instanceId.toLowerCase(), timeWindow.fromUtc, timeWindow.toUtc, view, ranking, rankingSource, database, refresh,
    page?.items.map(item => `${item.collectionRunId}:${item.observationKey}`)]);
  if (readAccess.denied) return <section className="query-performance-screen" aria-label="Query evidence access"><h2>Queries for {displayName}</h2><p role="alert">Query evidence access was denied. Retained rankings, history, waits, trends, and protected content were removed. Restore read access for this server, then explicitly retry.</p><button type="button" onClick={readAccess.retry}>Retry query access</button><button type="button" className="secondary-button" onClick={onClose}>Close</button></section>;
  return <QueryReadAccessContext.Provider value={readAccess}><RankingTrendProvider contextKey={rowTrendContext}><section className="query-performance-screen" aria-labelledby="query-performance-heading">
    <div className="health-heading-row"><div><p className="eyebrow">Server · queries</p><h2 id="query-performance-heading">Queries for {displayName}</h2></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {selectionInvalid ? <p role="alert" className="status-message">{invalidQuerySelectionMessage}</p> : null}
    {message === undefined || view === "cache" ? null : <p role="alert" className="status-message">{message}</p>}
    {status === undefined || view === "cache" ? null : <div className="activity-evidence"><EvidenceStatus label={`${status.source ?? "unavailable"} evidence · ${status.coverage}`} detail={`${status.sourceState}${status.truncated ? " · truncated" : ""} · repository snapshot ${timeLabel(status.snapshotUtc)}`} tone={status.sourceState === "read_write" && !status.truncated ? "current" : status.sourceState === "unavailable" ? "unavailable" : "warning"} /><details className="supporting-evidence"><summary>Database collection attempts ({status.databaseStatuses.length})</summary><div>{status.databaseStatuses.length === 0 ? "No per-database attempts were recorded." : status.databaseStatuses.map(x => <span key={x.databaseId} className="badge">{displayDatabase(x.databaseId)}: {x.status} · {x.sourceState} · fallback {x.fallbackAttempted ? "used" : "not used"} · {x.reason} · {x.lossKind} ({x.minimumLostItems} items, {x.minimumLostBytes} bytes{ x.lossCountIsExact ? ", exact" : ", lower bound"}){x.truncated ? " · truncated" : ""}</span>)}</div></details></div>}
    <Tabs label="Query views" revealFocusedTab tabs={[{ value: "top", label: "Top queries" }, { value: "regressed", label: `Regressed${visibleRegressions.length ? ` (${visibleRegressions.length})` : ""}` }, { value: "cache", label: "Cache interval deltas" }]}
      value={view} onChange={value => setView(value as QueryView)} />
    <label>Source <select value={rankingSource} onChange={event => onViewSelectionChange({ ...viewSelection,
      sourceFilter: event.target.value as QueryRankingSource, sourceFilterInvalid: undefined })}>
      {viewSelection.sourceFilterInvalid ? <option value="invalid" disabled>Invalid source filter</option> : null}
      <option value="mixed">All sources · evidence</option><option value="query_store">Query Store · interval</option><option value="plan_cache">Plan cache · row semantics</option>
    </select></label>
    {viewSelection.sourceFilterInvalid ? <p role="alert">The shared URL contains an invalid or duplicate query source filter. Choose Source to show runtime ranking observations.</p> : null}
    <p className="table-note">Source filters the loaded runtime ranking page only; other matching observations may be outside this bounded page. Query Store waits and plan comparisons use Query Store evidence; Cache interval deltas uses qualified plan-cache evidence. Inspected query history retains its own source.</p>
    {selectedDatabaseId !== undefined ? <p className="table-note">Database ID {selectedDatabaseId} · query rankings, wait contributors, and regression leads are filtered within their loaded responses. Other matching observations may be outside the bounded ranking.</p> : null}
    {view === "cache" && <PlanCacheIntervalPanel canReadText={canReadText} instanceId={instanceId} selectedDatabaseId={selectedDatabaseId} timeWindow={timeWindow} refresh={refresh} databaseNames={databaseNames} metric={ranking} onMetricChange={setRanking} selection={viewSelection.cacheSelection} onSelectWindow={onSelectWindow} onSelect={cacheSelection => onViewSelectionChange({ ...viewSelection, cacheSelection })} />}
    {view === "regressed" && <section className="panel" aria-label="Possible plan regressions">
      <h3>Possible plan regressions</h3>
      <p>Compare this window with the immediately preceding window of the same length. A lead appears when the dominant Query Store plan changes and comparable duration per execution is at least twice as high and 50 ms slower, with at least two groups and ten executions per plan. This is a correlation, not proof that the plan caused the slowdown.</p>
      {regressionError ? <p role="alert">{regressionError}</p> : null}
      {!regressions && !regressionError ? <p role="status">Comparing Query Store plans…</p> : null}
      {regressions && <p className="activity-evidence">Earlier: {timeLabel(regressions.comparisonFromUtc)} to {timeLabel(regressions.fromUtc)} · Current: {timeLabel(regressions.fromUtc)} to {timeLabel(regressions.toUtc)}. Only observed comparable groups contribute; missing or reset groups can hide changes.</p>}
      {regressions && visibleRegressions.length === 0 ? <p>No qualifying plan change for this database context is present in the loaded comparison.</p> : null}
      {regressions && visibleRegressions.length > 0 ? <div className="table-scroll" tabIndex={0} role="region" aria-label="Possible plan regression comparisons"><table>
        <caption>Possible Query Store plan regressions, ranked by increase in duration per execution</caption>
        <thead><tr><th scope="col">Database / query</th><th scope="col">Plan change</th>
          <th scope="col">Current plan duration trend</th><th scope="col">Average duration / execution</th><th scope="col">Comparable groups</th><th scope="col">Latest group</th></tr></thead>
        <tbody>{visibleRegressions.map(item => <tr key={`${item.databaseId}:${item.queryFingerprint}`}>
          <td><button className="fingerprint" type="button" onClick={() => {
            setSelectedSource("query_store");
            select(item.databaseId, item.queryFingerprint, timeWindow.fromUtc,
              timeWindow.toUtc, item.currentPlanFingerprint, "query_store");
          }}>{displayDatabase(item.databaseId)} / {item.queryFingerprint.slice(0, 12)}…</button></td>
          <td className="fingerprint">{item.previousPlanFingerprint.slice(0, 12)}… → {item.currentPlanFingerprint.slice(0, 12)}…</td>
          <td><RankingTrend request={queryStoreRankingTrendRequest(instanceId, item.databaseId, item.queryFingerprint, item.currentPlanFingerprint, timeWindow)} metric="duration"
            label={`${displayDatabase(item.databaseId)} / ${item.queryFingerprint.slice(0, 12)}… / plan ${item.currentPlanFingerprint.slice(0, 12)}… / current plan duration`} formatTime={timeLabel}
            onInspect={() => select(item.databaseId, item.queryFingerprint, timeWindow.fromUtc, timeWindow.toUtc, item.currentPlanFingerprint, "query_store")} /></td>
          <td>{item.previousAverageMilliseconds} → {item.currentAverageMilliseconds} ms · {item.slowdownRatio}×</td>
          <td>{item.previousComparableRows} / {item.previousSourceRows} → {item.currentComparableRows} / {item.currentSourceRows}</td>
          <td>{timeLabel(item.lastObservedAtUtc)}</td>
        </tr>)}</tbody>
      </table></div> : null}
    </section>}
    {view === "top" && page === undefined && message === undefined ? <p role="status">Loading bounded query evidence…</p> : null}
    {page === undefined || view === "cache" ? null : <>
      {view === "top" && <>
        <p className="activity-evidence">Query Store and plan-cache rows are explicitly labelled; interval and cumulative semantics are not mixed. Protected Query Store text and plan XML can be opened by a QueryTextReader. Repository time: {timeLabel(page.repositoryTimeUtc)}.</p><div className="toolbar"><label>Rank by <select value={ranking} onChange={event => setRanking(event.target.value as QueryPerformanceMetric)}>{metrics.map(metric => <option key={metric} value={metric}>{metricLabels[metric]}</option>)}</select></label><button disabled={!page.nextCursor || paging} onClick={loadMetricPage}>Next ranking page</button></div>
        <p className="activity-evidence">Ranking window: {timeLabel(page.fromUtc)} to {timeLabel(page.toUtc)} · each row is the latest observation for one query/plan/source identity in this window. History retains every observation. Only the selected ranking metric is loaded; other metrics remain unavailable.</p>
        <label>Database <select value={database} onChange={event => { selectionRequest.current?.abort(); historyRequest.current?.abort(); clearContent(); onSelectDatabase(event.target.value === "" ? undefined : Number(event.target.value)); setPlan(undefined); setHistory(undefined); setHistorySelection(undefined); }}><option value="">All loaded databases</option>{databaseOptions.map(option => <option key={option.databaseId} value={option.databaseId}>{option.databaseName ?? `Database ID ${option.databaseId}`}</option>)}</select></label>
      </>}
      {view === "top" && <p className="ranking-trend-coverage">Row trends load as they become visible, two reads at a time, for this bounded ranking page (up to 200 runtime rows and 25 wait plans). Query Store previews show qualified hourly deltas for the selected metric; they do not connect resets, ambiguous epochs or missing hours. Exact values and coverage remain available per row. Legacy cache latest rows do not supply a qualified database incarnation.</p>}
      {view === "top" && <div className="table-scroll" tabIndex={0} role="region" aria-label="Ranked query observations"><table><caption>Bounded ranked observations · CPU and duration in milliseconds · unavailable values are not zero</caption><thead><tr>{["Database / query", "Selected metric trend", "Source", "Semantics", "CPU ms", "Duration ms", "Executions", "Reads / writes / rows", "Interval end", "Coverage"].map(label => <th scope="col" key={label}>{label}</th>)}</tr></thead><tbody>
        {rows.map((x, index) => <tr key={`${x.collectionRunId}-${x.observationKey}`} className={selectedItem?.observationKey === x.observationKey && selectedItem.collectionRunId === x.collectionRunId ? "selected-row" : ""}><td><button className="fingerprint" type="button" aria-pressed={selectedItem?.observationKey === x.observationKey && selectedItem.collectionRunId === x.collectionRunId} onClick={() => select(x.query.databaseId, x.query.queryFingerprint, x.intervalStartUtc, x.intervalEndUtc, x.plan?.planFingerprint, x.source)}>{displayDatabase(x.query.databaseId)} / {x.query.queryFingerprint.slice(0, 12)}…</button></td>
          <td><RankingTrend request={runtimeRequest(index)}
            unavailableReason={x.source === "plan_cache" ? "Trend unavailable: this latest cache row lacks an exact database incarnation. Use Cache interval deltas for qualified history." : "Trend unavailable: exact plan identity is missing or duplicated in this ranking."}
            metric={ranking} label={`${displayDatabase(x.query.databaseId)} / ${x.query.queryFingerprint.slice(0, 12)}… / plan ${x.plan?.planFingerprint.slice(0, 12) ?? "unavailable"}… / ${ranking}`} formatTime={timeLabel}
            onInspect={() => select(x.query.databaseId, x.query.queryFingerprint, timeWindow.fromUtc, timeWindow.toUtc, x.plan?.planFingerprint, x.source)} /></td>
          <td>{x.source} · {x.sourceState}</td><td>{x.semantics}</td><td>{x.metrics.cpuMilliseconds ?? "—"}</td><td>{x.metrics.durationMilliseconds ?? "—"}</td><td>{x.metrics.executions ?? "—"}</td><td>{x.metrics.logicalReads ?? "—"} / {x.metrics.writes ?? "—"} / {x.metrics.rows ?? "—"}</td><td>{timeLabel(x.intervalEndUtc)}</td><td>{x.coverage} · {x.fresh ? "fresh at snapshot" : "stale"}{x.truncated ? " · truncated" : ""}</td></tr>)}
      </tbody></table>{rows.length === 0 && !viewSelection.sourceFilterInvalid && <p className="empty-state">No {rankingSource === "mixed" ? "matching" : rankingSource.replaceAll("_", " ")} observations in this bounded page.</p>}</div>}
      {selectedItem && <div className="kpi-grid">{[["CPU ms", selectedMetrics?.cpuMilliseconds], ["Duration ms", selectedMetrics?.durationMilliseconds], ["Executions", selectedMetrics?.executions], ["Logical reads", selectedMetrics?.logicalReads]].map(([label,value]) => <section className="kpi" key={label}><p>{label}</p><strong>{value ?? "—"}</strong><small>Selected observation · {selectedItem.semantics}</small></section>)}</div>}
    </>}
    {view === "cache" ? null : <>
      {historySelection && history && !selectedItem ? <p className="table-note">The selected query is outside this loaded ranking page; its exact history remains selected.</p> : null}
      <div className="query-investigation"><section className="panel"><h3>Query CPU and duration history</h3>
        {history ? (source === "mixed" ? <p>Select a query observation to chart one source with consistent semantics.</p> :
          <ObservationChart
            label={`Query activity (ms) · ${selectedHistoryItem?.semantics ?? "semantics unavailable"}`}
            fromUtc={historySelection?.fromUtc ?? timeWindow.fromUtc}
            toUtc={historySelection?.toUtc ?? timeWindow.toUtc}
            series={[
              { id: "duration", label: "Duration (ms)", items: history.items.filter(x => x.source === source && x.semantics === selectedHistoryItem?.semantics).map(x => ({ time: x.intervalEndUtc, value: x.metrics.durationMilliseconds ?? null })) },
              { id: "cpu", label: "CPU (ms)", items: history.items.filter(x => x.source === source && x.semantics === selectedHistoryItem?.semantics).map(x => ({ time: x.intervalEndUtc, value: x.metrics.cpuMilliseconds ?? null })) },
            ]}
            onSelectWindow={onSelectWindow}
          />) : <p>Select a query to load its bounded history.</p>}
      </section><section className="panel"><h3>Selected query</h3><p className="fingerprint">{historySelection?.queryFingerprint ?? "No query selected"}</p><p>{displayDatabase(historySelection?.databaseId)} · {source.replaceAll("_", " ")}</p><p>{selectedHistoryItem?.semantics ?? "Semantics unavailable"} · {selectedHistoryItem?.coverage ?? "Coverage unavailable"}</p>{selectedItem && !rows.includes(selectedItem) && view === "top" ? <p className="table-note">The inspected observation is hidden by the ranking source filter; its history remains selected.</p> : null}<p className="fingerprint">Plan: {plan?.planFingerprint ?? "Metadata unavailable"}</p>
        {selectedItem?.source === "query_store" && canReadText ? <button className="secondary-button" type="button" onClick={() => openText(selectedItem)}>Open query text for selected observation</button> : selectedItem ? <p>{selectedItem.source === "query_store" ? "Query text requires the QueryTextReader role on this server." : "Inspect captured cache text and plans in Cache interval deltas history."}</p> : null}
        {canReadText && historySelection && textResult ? <div role="status" className="query-text-result"><p>{textResult.state === "loading" ? "Opening query text…" : textResult.state === "available" ? `Query text from run ${textResult.runId}` : textResult.state === "unavailable" ? "Query text was not captured for this observation. Check the collector key, Query Store permissions, or source text restrictions." : textResult.message}</p>{textResult.state === "available" && <pre tabIndex={0} aria-label="Captured Query Store text">{textResult.text}</pre>}</div> : null}
        {selectedItem?.source === "query_store" && selectedItem.plan && canReadText ? <button className="secondary-button" type="button" onClick={() => openPlan(selectedItem)}>Open plan XML for selected observation</button> : selectedItem?.source === "query_store" && selectedItem.plan && !canReadText ? <p>Plan XML requires the QueryTextReader role on this server.</p> : null}
        {canReadText && historySelection && planResult ? <div role="status" className="query-text-result"><p>{planResult.state === "loading" ? "Opening plan XML…" : planResult.state === "available" ? `Plan XML from run ${planResult.runId}` : planResult.state === "unavailable" ? "Plan XML was not captured for this observation. Check the collector key, Query Store permissions, source restrictions, or plan size limit." : planResult.message}</p>{planResult.state === "available" && <pre tabIndex={0} aria-label="Captured Query Store plan XML">{planResult.xml}</pre>}</div> : null}
        {selectedItem?.source === "query_store" && selectedItem.plan ? <button className="secondary-button" type="button" onClick={() => openWaits(selectedItem)}>Show plan waits for selected observation</button> : null}
        {historySelection && waitResult ? <div role="status" className="activity-evidence"><p>{waitResult.state === "loading" ? "Loading plan waits…" : waitResult.state === "unavailable" ? "Query Store wait capture was unavailable for this plan and run." : waitResult.state === "error" ? waitResult.message : `Query Store wait totals captured ${timeLabel(waitResult.capturedAtUtc)} for run ${waitResult.runId}. Repeated interval snapshots can overlap.`}</p>{waitResult.state === "available" && (waitResult.categories?.length ? <ul>{waitResult.categories.map(row => <li key={row.category}>{waitCategoryNames[row.category] ?? `Category ${row.category}`}: {row.waitMilliseconds} ms</li>)}</ul> : <p>No nonzero wait categories were captured for this plan and run.</p>)}</div> : null}
      </section></div>
      {groupTrend && groupSelection?.planFingerprint && <QueryStoreGroupTrendPanel key={groupTrendKey} state={groupTrend}
        onSelectWindow={onSelectWindow} timeLabel={timeLabel} waitCategoryNames={waitCategoryNames}
        selection={viewSelection.planTrendSelection} onSelectionChange={selection => {
          const context = queryPlanTrendContext(renderedPlanHash);
          if (!context || typeof location !== "undefined" && location.hash !== renderedPlanHash) return;
          onViewSelectionChange({ ...viewSelection, planTrendSelection: { ...selection, context } });
        }} />}


      {history === undefined ? null : <div className="activity-evidence"><details><summary>Bounded history observations</summary>{history.items.length === 0 ? <p>No activity in the selected interval.</p> : history.items.map(x => <p key={`${x.collectionRunId}-${x.observationKey}`}>{timeLabel(x.intervalEndUtc)}: CPU {String(x.metrics.cpuMilliseconds ?? "—")} · duration {String(x.metrics.durationMilliseconds ?? "—")} · executions {String(x.metrics.executions ?? "—")} · reads {String(x.metrics.logicalReads ?? "—")} · writes {String(x.metrics.writes ?? "—")} · rows {String(x.metrics.rows ?? "—")} · {x.source} · {x.semantics} {canReadText && x.source === "query_store" ? <button className="secondary-button" type="button" onClick={() => openText(x)}>Open text for this run</button> : null} {canReadText && x.source === "query_store" && x.plan ? <button className="secondary-button" type="button" onClick={() => openPlan(x)}>Open plan XML for this run</button> : null} {x.source === "query_store" && x.plan ? <button className="secondary-button" type="button" onClick={() => openWaits(x)}>Show waits for this run</button> : null}</p>)}{history.nextCursor === undefined || historySelection === undefined ? null : <button className="secondary-button" type="button" onClick={() => loadHistory(historySelection, history.nextCursor)}>Next history page</button>}</details></div>}
      {plan === undefined ? null : <p className="activity-evidence"><strong>Plan metadata:</strong> {plan.planFingerprint.slice(0, 12)}… · {plan.source} · {plan.coverage} · latest run XML {plan.contentAvailable ? "captured" : "unavailable"}</p>}
    </>}
    {view === "top" && <section className="panel" aria-label="Top Query Store waits">
      <h3>Top by waits</h3>
      <p>Measured Query Store wait deltas across comparable plan groups in this window. Missing baselines, resets, and unavailable waits are excluded from totals; coverage shows how many observed groups contributed.</p>
      {waitRankingError ? <p role="alert">{waitRankingError}</p> : null}
      {!waitRanking && !waitRankingError ? <p role="status">Loading query wait ranking…</p> : null}
      {waitRanking && visibleWaitRanking.length === 0 ? <p>No comparable Query Store wait groups for this database context are present in the loaded ranking. This does not prove that queries had no waits.</p> : null}
      {waitRanking && visibleWaitRanking.length > 0 ? <div className="table-scroll" tabIndex={0} role="region" aria-label="Query Store wait ranking"><table>
        <caption>Top 25 plans by comparable Query Store wait milliseconds</caption>
        <thead><tr><th scope="col">Database / query / plan</th><th scope="col">Wait trend</th><th scope="col">Wait ms</th>
          <th scope="col">Comparable groups</th><th scope="col">Latest group</th></tr></thead>
        <tbody>{visibleWaitRanking.map((item, index) => <tr key={`${item.databaseId}:${item.queryFingerprint}:${item.planFingerprint}`}>
          <td><button className="fingerprint" type="button" onClick={() => {
            setSelectedSource("query_store");
            select(item.databaseId, item.queryFingerprint, timeWindow.fromUtc,
              timeWindow.toUtc, item.planFingerprint, "query_store");
          }}>{displayDatabase(item.databaseId)} / {item.queryFingerprint.slice(0, 12)}… / {item.planFingerprint.slice(0, 12)}…</button></td>
          <td><RankingTrend request={waitRequestFor(index)} unavailableReason="Trend unavailable: exact plan identity is missing or duplicated in this wait ranking." metric="waits" category={waitRanking.category}
            label={`${displayDatabase(item.databaseId)} / ${item.queryFingerprint.slice(0, 12)}… / plan ${item.planFingerprint.slice(0, 12)}… / waits`} formatTime={timeLabel}
            onInspect={() => select(item.databaseId, item.queryFingerprint, timeWindow.fromUtc, timeWindow.toUtc, item.planFingerprint, "query_store")} /></td>
          <td>{BigInt(item.waitMilliseconds).toLocaleString()}</td>
          <td>{item.comparableRows} / {item.sourceRows}</td>
          <td>{timeLabel(item.lastObservedAtUtc)}</td>
        </tr>)}</tbody>
      </table></div> : null}
    </section>}

  </section></RankingTrendProvider></QueryReadAccessContext.Provider>;
}
