import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { ObservationChart } from "../../components/ObservationChart";
import { ChartAlignmentControls } from "../../components/ChartAlignmentControls.tsx";
import { chartInspectionHref, readChartInspection, retainChartInspectionHref } from "../../components/chartInspectionModel.ts";
import { useChartInspection } from "../../components/useChartInspection.ts";
import type { ObservationSeries } from "../../components/observationChartModel";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { ActivityReadBoundary, activityAccessDenied, useActivityBoundary, useActivitySource, useBoundarySnapshot, type ActivityReadOwner } from "./activityAccess.ts";
import { getCurrentServerWaitPage, getServerWaitHistoryPage } from "./activityApi";
import type { ActivityPage, ActivityWait } from "./activityTypes";
import { resolveActivityWindow } from "./activityWindowModel";
import { ActivityTable, Evidence, WaitCategoryChart, WaitHistory } from "./TargetActivityPanel";
import { getServerWaitTrend, type WaitTrendResponse } from "./waitTrendApi";
import { waitTrendChartData } from "./waitTrendChartModel";
import { getQueryStoreTopWaits } from "../queries/queryPerformanceApi";
import { queryStoreWaitCategories } from "../queries/queryStoreWaitCategories";
import { queryJumpHref } from "../../components/commandPaletteModel";
import { readWaitCategory, waitCategoryHref } from "./waitSelectionModel.ts";
import { WaitCapturedSessionsPanel } from "./WaitCapturedSessionsPanel";
import { retainSessionWaitCategoryHref, retainSessionWaitCaptureHref } from "./waitCapturedSessionsModel.ts";
import "./waitsEvidence.css";

export interface TargetWaitsPanelProps {
  readonly active?: boolean;
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly targetRevision?: number;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly selectionHash?: string;
}

export function TargetWaitsPanel({ active = true, instanceId, displayName, onClose, scope, refresh, onSelectWindow, selectionHash = location.hash, targetRevision }: TargetWaitsPanelProps) {
  const { mode } = useTimeDisplay();
  const selection = useMemo(() => resolveActivityWindow({ ...scope,
    from: scope.from?.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"),
    to: scope.to?.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") }, Date.now()),
    [scope.range, scope.from, scope.to, refresh]);
  const window = selection.state === "available" ? selection.window : undefined;
  const historyContext = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, scope.databaseId]);
  const access = useActivityBoundary(historyContext);
  const requestedCategory = readWaitCategory(selectionHash, instanceId);
  const queryCategory = requestedCategory === undefined ? 3 : requestedCategory;
  const historySource = useActivitySource(access, "history", historyContext, refresh,
    window ? signal => getServerWaitHistoryPage(instanceId, window, signal) : undefined, window);
  const trendSource = useActivitySource(access, "trend", historyContext, refresh,
    window ? signal => getServerWaitTrend(instanceId, window, signal) : undefined, window);
  const rankingSource = useActivitySource(access, "ranking", JSON.stringify([historyContext, queryCategory]), refresh,
    window && queryCategory !== null ? signal => getQueryStoreTopWaits(instanceId, window.fromUtc, window.toUtc, signal, queryCategory) : undefined, window);
  const history = historySource.value, historyError = historySource.error, historyLoading = historySource.loading;
  const trend = trendSource.value, trendError = trendSource.error, trendLoading = trendSource.loading;
  const queryRanking = rankingSource.value, queryRankingError = rankingSource.error;
  const requestedChartAt = readChartInspection(selectionHash);
  const chartTimeIdentity = (value: string | undefined) => value === undefined ? undefined
    : utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")).toString();
  const alignmentContext = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.databaseId,
    active, access.epoch, chartTimeIdentity(window?.fromUtc), chartTimeIdentity(window?.toUtc),
    chartTimeIdentity(trend?.fromUtc), chartTimeIdentity(trend?.toUtc)]);
  const inspectionContext = JSON.stringify([alignmentContext, selectionHash]);
  const inspection = useChartInspection(inspectionContext, trend?.fromUtc, trend?.toUtc, requestedChartAt);
  const interaction = useRef({ context: inspectionContext, access });
  if (interaction.current.context !== inspectionContext || interaction.current.access !== access)
    interaction.current = { context: inspectionContext, access };
  const owner = interaction.current;
  const mounted = useRef(false);
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const interactionCurrent = () => mounted.current && active && interaction.current === owner
    && access.allowed("trend") && location.hash === selectionHash;
  const hoverChart = (utc: string | null) => { if (interactionCurrent()) inspection.setCrosshairUtc(utc); };
  const selectChartWindow = onSelectWindow && active ? (next: { readonly fromUtc: string; readonly toUtc: string }) => {
    if (interactionCurrent()) onSelectWindow(next);
  } : undefined;
  return <section className="activity-screen" aria-labelledby="waits-heading">
    <div className="screen-intro"><div><p className="eyebrow">Server · waits</p><h2 id="waits-heading">Waits for {displayName}</h2>
      <p>Wait totals are cumulative SQL Server counters. Historical rows show a delta only when the preceding collector run is comparable; a reset or missing baseline remains unknown.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {access.denied && <p role="alert">Waits access was denied. Previously received protected evidence was removed. Automatic reads stay stopped for each source until its explicit retry succeeds.</p>}
    {scope.databaseId !== undefined ? <p className="table-note">Server wait totals and trends remain server-wide. Query Store contributors are filtered within the loaded ranking for database ID {scope.databaseId}.</p> : null}
    {window && <section className="panel waits-evidence-panel" aria-label="Wait categories over time">
      <h3>Wait categories over time</h3>
      <p>Five-minute deltas across the selected window. Empty, partial, legacy, and incomparable buckets remain gaps. Older history may need new collector runs before summaries become available.</p>
      {trendLoading && <p role="status">Loading wait trend…</p>}
      {trendError && <p role="alert">{trendError}{trend ? " Previously received trend remains displayed." : ""}</p>}
      {(trendSource.denied || trendError) && <button type="button" disabled={trendLoading} onClick={trendSource.retry}>Retry wait trend</button>}
      {trendSource.denied && <p role="status">Wait trend unavailable after access denial. No zero-wait trend has been substituted.</p>}
      {trend && <>
        {active && <ChartAlignmentControls key={alignmentContext} context={alignmentContext} label="Waits" headingLevel={4} clearFallbackNote=""
          fromUtc={trend.fromUtc} toUtc={trend.toUtc} requestedUtc={requestedChartAt} crosshairUtc={inspection.crosshairUtc}
          onAlign={utc => {
            if (!interactionCurrent()) return;
            inspection.setCrosshairUtc(null); location.hash = chartInspectionHref(selectionHash, utc);
          }}
          onClear={() => {
            if (!interactionCurrent()) return;
            inspection.setCrosshairUtc(null); location.hash = chartInspectionHref(selectionHash);
          }} />}
        <WaitTrendChart key={inspectionContext} trend={trend} crosshairUtc={active ? inspection.crosshairUtc : null}
          onCrosshairChange={hoverChart} onSelectWindow={selectChartWindow} />
      </>}
    </section>}
    {window && <section className="panel waits-evidence-panel" aria-label="Query Store plans by wait category">
      <h3>Queries with matching Query Store waits</h3>
      <p>Query Store plan wait deltas are a separate source from the server wait trend above. They can point to queries worth investigating, but do not account for all server waits or identify a current waiting session.</p>
      {queryCategory === null && <p role="status">The requested Query Store wait category is invalid. Choose a supported category; no other category has been selected.</p>}
      <label>Query Store category <select value={queryCategory ?? ""} onChange={event => { if (location.hash === selectionHash) location.hash = retainChartInspectionHref(retainSessionWaitCaptureHref(retainSessionWaitCategoryHref(waitCategoryHref({ ...scope, target: instanceId }, Number(event.target.value)), selectionHash), selectionHash), selectionHash); }}>
        {queryCategory === null && <option value="" disabled>Choose a supported category</option>}
        {queryStoreWaitCategories.map((name, category) => category === 11 ? null :
          <option key={category} value={category}>{name}</option>)}
      </select></label>
      {queryRankingError && <p role="alert">{queryRankingError}{queryRanking ? " Previously received ranking remains displayed." : ""}</p>}
      {queryCategory !== null && rankingSource.loading && !queryRanking && !queryRankingError && <p role="status">Loading Query Store wait groups…</p>}
      {queryCategory !== null && (rankingSource.denied || queryRankingError) && <button type="button" disabled={rankingSource.loading} onClick={rankingSource.retry}>Retry Query Store wait groups</button>}
      {rankingSource.denied && <p role="status">Query Store contributors unavailable after access denial. Previously received rows have been removed.</p>}
      {queryRanking && rankingSource.receipt?.window && <p className="activity-evidence">Received Query Store window: {formatDisplayTime(rankingSource.receipt.window.fromUtc, mode)} to {formatDisplayTime(rankingSource.receipt.window.toUtc, mode)}.</p>}
      {queryRanking && !queryRanking.items.some(item => scope.databaseId === undefined || item.databaseId === scope.databaseId) && <p>No positive comparable Query Store wait delta for this database context is present in the loaded category ranking.</p>}
      {queryRanking && queryRanking.items.some(item => scope.databaseId === undefined || item.databaseId === scope.databaseId) && <div className="table-scroll" tabIndex={0} role="region" aria-label="Query Store wait contributors"><table>
        <caption>Top 25 Query Store plans by comparable category wait milliseconds</caption>
        <thead><tr><th scope="col">Database / query / plan</th><th scope="col">Wait ms</th>
          <th scope="col">Comparable groups</th><th scope="col">Latest group</th></tr></thead>
        <tbody>{queryRanking.items.filter(item => scope.databaseId === undefined || item.databaseId === scope.databaseId).map(item => <tr key={`${item.databaseId}:${item.queryFingerprint}:${item.planFingerprint}`}>
          <td><a className="fingerprint" href={queryJumpHref(
            { ...scope, range: "custom", from: queryRanking.fromUtc, to: queryRanking.toUtc },
            instanceId, item.databaseId, item.queryFingerprint, item.planFingerprint, "query_store")}>
            Database ID {item.databaseId} / {item.queryFingerprint.slice(0, 12)}… / {item.planFingerprint.slice(0, 12)}…</a></td>
          <td>{BigInt(item.waitMilliseconds).toLocaleString()}</td>
          <td>{item.comparableRows} / {item.sourceRows}</td>
          <td>{formatDisplayTime(item.lastObservedAtUtc, mode)}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>}
    <WaitCapturedSessionsPanel access={access} instanceId={instanceId} scope={scope} timeWindow={window} refresh={refresh} selectionHash={selectionHash} />
    {scope.range !== "custom" && <CurrentWaits key={historyContext} access={access} instanceId={instanceId} refresh={refresh} />}
    <section className="panel waits-evidence-panel" aria-label="Selected wait history">
      <h3>Selected-window history</h3>
      {window && <p className="activity-evidence">{formatDisplayTime(window.fromUtc, mode)} to {formatDisplayTime(window.toUtc, mode)}. Rows are paged; the table does not claim full-window coverage until its final page.</p>}
      {selection.state === "unavailable" && <p role="status">{selection.message}</p>}
      {historyLoading && <p role="status">Loading wait history…</p>}
      {historyError && <p role="alert">{historyError}{history ? " Previously received history remains displayed." : ""}</p>}
      {(historySource.denied || historyError) && <button type="button" disabled={historyLoading || !window} onClick={historySource.retry}>Retry wait history</button>}
      {historySource.denied && <p role="status">Wait history unavailable after access denial. Previously received rows and page navigation were removed.</p>}
      {history && <WaitHistory key={historyContext} instanceId={instanceId} initialPage={history} latestWindow={window} access={access} accessSource="history" initialGeneration={historySource.receipt?.refresh} latestGeneration={refresh} />}
    </section>
  </section>;
}

function WaitTrendChart({ trend, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly trend: WaitTrendResponse;
  readonly crosshairUtc: string | null;
  readonly onCrosshairChange: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const chart = waitTrendChartData(trend);
  const series: ObservationSeries[] = chart.series.map(category => ({
    id: category.category, label: category.category,
    items: category.points.map(point => ({ time: point.timeUtc, value: point.value })),
  }));
  return <>
    <ObservationChart label="Server wait time by category (ms per five minutes)" series={series}
      fromUtc={trend.fromUtc} toUtc={trend.toUtc} crosshairUtc={crosshairUtc}
      onCrosshairChange={onCrosshairChange} onSelectWindow={onSelectWindow} />
    <p className="activity-evidence">{chart.comparableBuckets} of {chart.totalBuckets} buckets are comparable across all categories among committed runs; {chart.totalBuckets - chart.comparableBuckets} remain gaps.{chart.oversizedValues ? ` ${chart.oversizedValues} values exceed chart precision and are shown as gaps.` : ""} Repository time: {trend.repositoryTimeUtc}.</p>
  </>;
}

function CurrentWaits({ instanceId, refresh, access }: { readonly instanceId: string; readonly refresh: number; readonly access: ActivityReadBoundary }) {
  useBoundarySnapshot(access);
  const [page, setPage] = useState<ActivityPage<ActivityWait>>();
  const [pageIndex, setPageIndex] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<{ ticket: ActivityReadOwner; automatic: boolean } | undefined>(undefined);
  const firstPage = useRef<ActivityPage<ActivityWait> | undefined>(undefined);
  const browsing = useRef(false);
  const [retained, setRetained] = useState(false);
  const latestRefresh = useRef(refresh); latestRefresh.current = refresh;
  useLayoutEffect(() => {
    const unsubscribe = access.subscribeInvalidation(() => {
      request.current = undefined; firstPage.current = undefined; browsing.current = true;
      setPage(undefined); setPageIndex(0); setCursors([undefined]); setLoading(false); setError(undefined); setRetained(true);
    });
    return () => { access.cancel(request.current?.ticket); request.current = undefined; firstPage.current = undefined; unsubscribe(); };
  }, [access]);
  async function load(index: number, cursor: string | undefined, explicit: boolean, latest: boolean) {
    if (request.current) return;
    const ticket = access.begin("current", "current-waits", explicit); if (!ticket) return;
    const owned = { ticket, automatic: !explicit }; request.current = owned;
    const generation = latestRefresh.current;
    const current = () => access.current(ticket) && request.current === owned && (!owned.automatic || latestRefresh.current === generation);
    setLoading(true); setError(undefined);
    try {
      const next = await getCurrentServerWaitPage(instanceId, ticket.controller.signal, cursor);
      if (!current() || !access.recover(ticket)) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => latest ? [undefined] : [...previous.slice(0, index), cursor]);
      if (latest) { firstPage.current = next; browsing.current = false; setRetained(false); }
    } catch (failure) {
      if (!current()) return;
      if (activityAccessDenied(failure)) { access.deny(); return; }
      setError(failure instanceof Error ? failure.message : "Current waits are unavailable.");
    } finally {
      access.finish(ticket);
      if (request.current === owned) { request.current = undefined; setLoading(false); }
    }
  }
  useEffect(() => {
    // Explicit paging/recovery owns its read; shared polling can supersede only an automatic latest read.
    if (request.current?.automatic) { access.cancel(request.current.ticket); request.current = undefined; }
    if (!browsing.current && !request.current && access.allowed("current")) void load(0, undefined, false, true);
  }, [access, instanceId, refresh]);
  function returnToLatest() {
    access.cancel(request.current?.ticket); request.current = undefined;
    browsing.current = true;
    void load(0, undefined, true, true);
  }
  async function navigate(index: number, cursor?: string) {
    if (request.current || !access.allowed("current")) return;
    browsing.current = true; setRetained(true);
    if (index === 0) {
      if (firstPage.current) { setPage(firstPage.current); setPageIndex(0); setError(undefined); }
      return;
    }
    if (cursor !== undefined) await load(index, cursor, true, false);
  }
  const visiblePage = access.allowed("current") ? page : undefined;
  return <section className="panel waits-evidence-panel" aria-label="Current server waits" aria-busy={loading}>
    <h3>Live wait snapshot</h3>
    {!access.allowed("current") && <p role="status">Current wait snapshot unavailable after access denial. Received rows and the retained first page were removed.</p>}
    {loading && !visiblePage && <p role="status">Loading current waits...</p>}
    {loading && visiblePage && <p role="status">{retained ? "Loading another page of the retained wait snapshot..." : "Refreshing the latest wait snapshot; previously loaded evidence remains displayed..."}</p>}
    {error && <p role="alert">{error} {retained ? "Retry page navigation or return to latest; expired snapshots require a new snapshot." : "Refresh to retry the latest snapshot."} {visiblePage ? "Previously loaded evidence remains displayed." : ""}</p>}
    {retained && visiblePage && <p role="status">Browsing retained snapshot · automatic wait snapshot refresh paused. First and Previous keep this snapshot.</p>}
    {(retained || error || !access.allowed("current")) && <button type="button" onClick={returnToLatest}>Return to latest wait snapshot</button>}
    {visiblePage && <>
      <Evidence page={visiblePage} />
      <WaitCategoryChart page={visiblePage} />
      <ActivityTable title="Wait types" columns={["Wait type", "Tasks", "Total wait ms", "Signal wait ms", "Comparable delta ms", "Baseline"]}
        rows={visiblePage.items.map(item => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds,
          item.signalWaitTimeMilliseconds, item.resetDetected ? "reset" : item.waitTimeMillisecondsDelta ?? "—",
          item.baselineAvailable ? "available" : "not available"])} />
      <p role="status">Page {pageIndex + 1} · {visiblePage.items.length} wait types{visiblePage.nextCursor ? " · More rows available" : " · End of snapshot"}</p>
      <nav aria-label="Current wait pages">
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First wait page</button>{" "}
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Previous wait page</button>{" "}
        <button type="button" disabled={loading || !visiblePage.nextCursor} onClick={() => void navigate(pageIndex + 1, visiblePage.nextCursor)}>Next wait page</button>
      </nav>
    </>}
  </section>;
}
