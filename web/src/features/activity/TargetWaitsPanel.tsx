import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { ObservationChart } from "../../components/ObservationChart";
import type { ObservationSeries } from "../../components/observationChartModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getCurrentServerWaitPage, getServerWaitHistoryPage } from "./activityApi";
import type { ActivityPage, ActivityWait, ServerWaitHistoryItem } from "./activityTypes";
import { resolveActivityWindow } from "./activityWindowModel";
import { ActivityTable, Evidence, WaitCategoryChart, WaitHistory } from "./TargetActivityPanel";
import { getServerWaitTrend, type WaitTrendResponse } from "./waitTrendApi";
import { waitTrendChartData } from "./waitTrendChartModel";
import { getQueryStoreTopWaits } from "../queries/queryPerformanceApi";
import type { QueryStoreWaitRanking } from "../queries/queryPerformanceTypes";
import { queryStoreWaitCategories } from "../queries/queryStoreWaitCategories";
import { queryJumpHref } from "../../components/commandPaletteModel";

export interface TargetWaitsPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}

export function TargetWaitsPanel({ instanceId, displayName, onClose, scope, refresh, onSelectWindow }: TargetWaitsPanelProps) {
  const { mode } = useTimeDisplay();
  const selection = useMemo(() => resolveActivityWindow(scope, Date.now()),
    [scope.range, scope.from, scope.to, refresh]);
  const window = selection.state === "available" ? selection.window : undefined;
  const [history, setHistory] = useState<ActivityPage<ServerWaitHistoryItem>>();
  const [historyError, setHistoryError] = useState<string>();
  const [historyLoading, setHistoryLoading] = useState(false);
  const [trend, setTrend] = useState<WaitTrendResponse>();
  const [trendError, setTrendError] = useState<string>();
  const [trendLoading, setTrendLoading] = useState(false);
  const [queryCategory, setQueryCategory] = useState(3);
  const [queryRanking, setQueryRanking] = useState<QueryStoreWaitRanking>();
  const [queryRankingError, setQueryRankingError] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    setHistory(undefined);
    setHistoryError(undefined);
    if (!window) { setHistoryLoading(false); return () => controller.abort(); }
    setHistoryLoading(true);
    void getServerWaitHistoryPage(instanceId, window, controller.signal)
      .then(page => { if (!controller.signal.aborted) setHistory(page); })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setHistoryError(error instanceof Error ? error.message : "Wait history is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) setHistoryLoading(false); });
    return () => controller.abort();
  }, [instanceId, window?.fromUtc, window?.toUtc, refresh]);

  useEffect(() => {
    const controller = new AbortController();
    setQueryRanking(undefined); setQueryRankingError(undefined);
    if (!window) return () => controller.abort();
    void getQueryStoreTopWaits(instanceId, window.fromUtc, window.toUtc,
      controller.signal, queryCategory)
      .then(value => { if (!controller.signal.aborted) setQueryRanking(value); })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setQueryRankingError(error instanceof Error
          ? error.message : "Query Store plan wait ranking is unavailable.");
      });
    return () => controller.abort();
  }, [instanceId, window?.fromUtc, window?.toUtc, queryCategory, refresh]);

  useEffect(() => {
    const controller = new AbortController();
    setTrend(undefined); setTrendError(undefined);
    if (!window) { setTrendLoading(false); return () => controller.abort(); }
    setTrendLoading(true);
    void getServerWaitTrend(instanceId, window, controller.signal)
      .then(value => { if (!controller.signal.aborted) setTrend(value); })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setTrendError(error instanceof Error ? error.message : "Wait trend is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) setTrendLoading(false); });
    return () => controller.abort();
  }, [instanceId, window?.fromUtc, window?.toUtc, refresh]);

  return <section className="activity-screen" aria-labelledby="waits-heading">
    <div className="screen-intro"><div><p className="eyebrow">Server · waits</p><h2 id="waits-heading">Waits for {displayName}</h2>
      <p>Wait totals are cumulative SQL Server counters. Historical rows show a delta only when the preceding collector run is comparable; a reset or missing baseline remains unknown.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {window && <section className="panel" aria-label="Wait categories over time">
      <h3>Wait categories over time</h3>
      <p>Five-minute deltas across the selected window. Empty, partial, legacy, and incomparable buckets remain gaps. Older history may need new collector runs before summaries become available.</p>
      {trendLoading && <p role="status">Loading wait trend…</p>}
      {trendError && <p role="alert">{trendError} Refresh to retry.</p>}
      {trend && <WaitTrendChart trend={trend} onSelectWindow={onSelectWindow} />}
    </section>}
    {window && <section className="panel" aria-label="Query Store plans by wait category">
      <h3>Queries with matching Query Store waits</h3>
      <p>Query Store plan wait deltas are a separate source from the server wait trend above. They can point to queries worth investigating, but do not account for all server waits or identify a current waiting session.</p>
      <label>Query Store category <select value={queryCategory} onChange={event => setQueryCategory(Number(event.target.value))}>
        {queryStoreWaitCategories.map((name, category) => category === 11 ? null :
          <option key={category} value={category}>{name}</option>)}
      </select></label>
      {queryRankingError && <p role="alert">{queryRankingError} Refresh to retry.</p>}
      {!queryRanking && !queryRankingError && <p role="status">Loading Query Store wait groups…</p>}
      {queryRanking && queryRanking.items.length === 0 && <p>No positive comparable Query Store wait delta was observed for this category and window.</p>}
      {queryRanking && queryRanking.items.length > 0 && <div className="table-scroll"><table>
        <caption>Top 25 Query Store plans by comparable category wait milliseconds</caption>
        <thead><tr><th scope="col">Database / query / plan</th><th scope="col">Wait ms</th>
          <th scope="col">Comparable groups</th><th scope="col">Latest group</th></tr></thead>
        <tbody>{queryRanking.items.map(item => <tr key={`${item.databaseId}:${item.queryFingerprint}:${item.planFingerprint}`}>
          <td><a className="fingerprint" href={queryJumpHref(
            { ...scope, range: "custom", from: window.fromUtc, to: window.toUtc },
            instanceId, item.databaseId, item.queryFingerprint, item.planFingerprint, "query_store")}>
            Database ID {item.databaseId} / {item.queryFingerprint.slice(0, 12)}… / {item.planFingerprint.slice(0, 12)}…</a></td>
          <td>{BigInt(item.waitMilliseconds).toLocaleString()}</td>
          <td>{item.comparableRows} / {item.sourceRows}</td>
          <td>{formatDisplayTime(item.lastObservedAtUtc, mode)}</td>
        </tr>)}</tbody>
      </table></div>}
    </section>}
    {scope.range !== "custom" && <CurrentWaits key={`${instanceId}:${refresh}`} instanceId={instanceId} refresh={refresh} />}
    <section className="panel" aria-label="Selected wait history">
      <h3>Selected-window history</h3>
      {window && <p className="activity-evidence">{formatDisplayTime(window.fromUtc, mode)} to {formatDisplayTime(window.toUtc, mode)}. Rows are paged; the table does not claim full-window coverage until its final page.</p>}
      {selection.state === "unavailable" && <p role="status">{selection.message}</p>}
      {historyLoading && <p role="status">Loading wait history…</p>}
      {historyError && <p role="alert">{historyError} Refresh to retry.</p>}
      {history && <WaitHistory key={`${instanceId}:${history.fromUtc}:${history.toUtc}:${history.repositoryTimeUtc}`} instanceId={instanceId} initialPage={history} />}
    </section>
  </section>;
}

function WaitTrendChart({ trend, onSelectWindow }: {
  readonly trend: WaitTrendResponse;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const chart = waitTrendChartData(trend);
  const series: ObservationSeries[] = chart.series.map(category => ({
    id: category.category, label: category.category,
    items: category.points.map(point => ({ time: point.timeUtc, value: point.value })),
  }));
  return <>
    <ObservationChart label="Server wait time by category (ms per five minutes)" series={series}
      fromUtc={trend.fromUtc} toUtc={trend.toUtc} onSelectWindow={onSelectWindow} />
    <p className="activity-evidence">{chart.comparableBuckets} of {chart.totalBuckets} buckets are comparable across all categories among committed runs; {chart.totalBuckets - chart.comparableBuckets} remain gaps.{chart.oversizedValues ? ` ${chart.oversizedValues} values exceed chart precision and are shown as gaps.` : ""} Repository time: {trend.repositoryTimeUtc}.</p>
  </>;
}

function CurrentWaits({ instanceId, refresh }: { readonly instanceId: string; readonly refresh: number }) {
  const [page, setPage] = useState<ActivityPage<ActivityWait>>();
  const [pageIndex, setPageIndex] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => {
    const controller = new AbortController();
    request.current = controller;
    setPage(undefined); setPageIndex(0); setCursors([undefined]); setError(undefined); setLoading(true);
    void getCurrentServerWaitPage(instanceId, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPage(value); })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Current waits are unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) { request.current = undefined; setLoading(false); } });
    return () => controller.abort();
  }, [instanceId, refresh]);

  async function navigate(index: number, cursor?: string) {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setError(undefined);
    try {
      const next = await getCurrentServerWaitPage(instanceId, controller.signal, cursor);
      if (controller.signal.aborted) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (failure: unknown) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Current waits could not be loaded.");
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    }
  }

  return <section className="panel" aria-label="Current server waits" aria-busy={loading}>
    <h3>Live wait snapshot</h3>
    {loading && !page && <p role="status">Loading current waits…</p>}
    {error && <p role="alert">{error} Refresh or retry navigation.</p>}
    {page && <>
      <Evidence page={page} />
      <WaitCategoryChart page={page} />
      <ActivityTable title="Wait types" columns={["Wait type", "Tasks", "Total wait ms", "Signal wait ms", "Comparable delta ms", "Baseline"]}
        rows={page.items.map(item => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds,
          item.signalWaitTimeMilliseconds, item.resetDetected ? "reset" : item.waitTimeMillisecondsDelta ?? "—",
          item.baselineAvailable ? "available" : "not available"])} />
      <p role="status">Page {pageIndex + 1} · {page.items.length} wait types{page.nextCursor ? " · More rows available" : " · End of snapshot"}</p>
      <nav aria-label="Current wait pages">
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First wait page</button>{" "}
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Previous wait page</button>{" "}
        <button type="button" disabled={loading || !page.nextCursor} onClick={() => void navigate(pageIndex + 1, page.nextCursor)}>Next wait page</button>
      </nav>
    </>}
  </section>;
}
