import { ObservationChart } from "../../components/ObservationChart";
import { PlanCacheContentPanel } from "./PlanCacheContentPanel";
import type { PlanCacheContentKind } from "./queryPerformanceTypes";
import { cacheIntervalRateLabel, formatCacheIntervalRate, maximumCacheTrendSamples, planCacheIntervalTrend } from "./planCacheIntervalTrendModel";
import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { getPlanCacheIntervalHistory } from "./queryPerformanceApi";
import type { PlanCacheHistorySelection, PlanCacheIntervalHistory, PlanCacheIntervalHistoryItem, QueryPerformanceMetric } from "./queryPerformanceTypes";
import type { QueryWindow } from "./queryWindowModel";

const states = { comparable: "Measured", window_boundary: "Crosses window boundary", baseline_unavailable: "First qualified sample",
  reset: "Counters reset", stale: "Out-of-order capture", comparison_window_expired: "Comparison gap exceeds seven days" };
const label = (value: string | null) => value === null ? "Unavailable" : value.includes(".") ? value.replace(/0+$/u, "").replace(/\.$/u, "") : value;
const canonical = (value: string) => value.replace(/(?:\.(\d{1,7}))?Z$/u, (_match, fraction: string | undefined) => `.${(fraction ?? "").padEnd(7, "0")}Z`);
const keyOf = (row: PlanCacheIntervalHistoryItem) => `${canonical(row.observedAtUtc)}:${canonical(row.committedAtUtc)}:${row.collectionRunId}:${row.cacheEntryFingerprint}`;

export function PlanCacheIntervalHistoryPanel({ selection, timeWindow, refresh, onClose, metric, onSelectWindow, canReadText }: {
  readonly canReadText: boolean;
  readonly metric: QueryPerformanceMetric; readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly selection: PlanCacheHistorySelection; readonly timeWindow: QueryWindow; readonly refresh: number; readonly onClose: () => void;
}) {
  const timeLabel = useTimeFormatter(); const request = useRef<AbortController | undefined>(undefined);
  const [state, setState] = useState<{ readonly key: string; readonly result?: PlanCacheIntervalHistory; readonly loading?: boolean; readonly error?: string }>();
  const [contentSelection, setContentSelection] = useState<{ readonly key: string; readonly row: PlanCacheIntervalHistoryItem; readonly kind: PlanCacheContentKind }>();
  const { targetId, databaseId, databaseIncarnation, queryFingerprint, planFingerprint } = selection;
  const key = JSON.stringify([selection, timeWindow.fromUtc, timeWindow.toUtc, refresh]);
  const current = state?.key === key ? state : undefined;
  useEffect(() => { setContentSelection(undefined); }, [key, canReadText]);
  useEffect(() => {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    void getPlanCacheIntervalHistory({ targetId, databaseId, databaseIncarnation, queryFingerprint, planFingerprint }, timeWindow.fromUtc, timeWindow.toUtc, controller.signal)
      .then(result => { if (!controller.signal.aborted) setState({ key, result }); })
      .catch(() => { if (!controller.signal.aborted) setState({ key, error: "Cache history is unavailable. Retry when collection and repository access are available." }); });
    return () => controller.abort();
  }, [targetId, databaseId, databaseIncarnation, queryFingerprint, planFingerprint, timeWindow.fromUtc, timeWindow.toUtc, refresh, key]);
  const result = current?.result;
  const trend = result ? planCacheIntervalTrend(result, metric) : undefined;
  const more = () => {
    if (!result?.nextCursor || current?.loading || result.items.length >= 1000) return;
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    setState({ key, result, loading: true });
    void getPlanCacheIntervalHistory(selection, timeWindow.fromUtc, timeWindow.toUtc, controller.signal, result.nextCursor)
      .then(page => {
        if (controller.signal.aborted) return;
        const seen = new Set(result.items.map(row => `${row.collectionRunId}:${row.cacheEntryFingerprint}`));
        const last = result.items.at(-1), first = page.items[0];
        if (page.snapshotUtc !== result.snapshotUtc || page.revision !== result.revision || page.totalRows !== result.totalRows
            || first && last && keyOf(first) >= keyOf(last) || page.items.some(row => seen.has(`${row.collectionRunId}:${row.cacheEntryFingerprint}`)))
          throw new Error("Cache history snapshot changed.");
        setState({ key, result: { ...page, items: [...result.items, ...page.items] } });
      }).catch(() => { if (!controller.signal.aborted) setState({ key, result, error: "More history could not be loaded. Refresh to restart if the snapshot expired, the server configuration changed or evidence was retired." }); });
  };
  return <section className="panel" aria-label="Cache query interval history">
    <div className="toolbar"><h4>Cache query interval history</h4><button type="button" onClick={onClose}>Close cache history</button></div>
    <p>Database ID {selection.databaseId} · query {selection.queryFingerprint.slice(0, 12)}… · plan {selection.planFingerprint.slice(0, 12)}… · database incarnation {selection.databaseIncarnation.slice(0, 12)}…</p>
    <p>Each row belongs to one cache entry. Measured counters cover the full interval shown. Unavailable comparisons and boundary crossings supply no counters; an absent row does not prove inactivity.</p>
    {!canReadText ? <p>Opening captured query text and plan XML requires the QueryTextReader role and a read role on this server.</p> : null}
    {canReadText && contentSelection?.key === key ? <PlanCacheContentPanel
      key={`${key}:${contentSelection.row.collectionRunId}:${contentSelection.row.cacheEntryFingerprint}:${contentSelection.kind}`}
      selection={selection} row={contentSelection.row} kind={contentSelection.kind} onClose={() => setContentSelection(undefined)} /> : null}
    {!current ? <p role="status">Loading cache query history…</p> : null}
    {current?.error ? <p role="alert">{current.error}</p> : null}
    {result && <>
      <p className="activity-evidence">{result.items.length} of {result.totalRows} observed samples · snapshot {timeLabel(result.snapshotUtc)}</p>
      {trend && result.items.length > 0 && <section aria-label="Cache interval rate trend">
        <h5>Measured interval rates · {cacheIntervalRateLabel(metric)}</h5>
        <p>Average rates over each full measured interval, rounded to six significant digits. Separate cache entries and epochs remain separate series; comparisons without counters are blank. Exact totals stay in the table.</p>
        <p>{trend.plottedIntervals} measured intervals plotted · {trend.unavailableComparisons} unavailable comparisons</p>
        {trend.hiddenSamples > 0 || trend.hiddenEntries > 0 ? <p>The chart uses the newest {maximumCacheTrendSamples} loaded samples and up to 10 cache entries. {trend.hiddenSamples} older samples and {trend.hiddenEntries} additional entries remain in the table.</p> : null}
        {trend.precisionOmissions > 0 ? <p>{trend.precisionOmissions} intervals exceed chart precision. Inspect their exact totals in the table.</p> : null}
        {trend.invalidIntervals > 0 ? <p>{trend.invalidIntervals} intervals could not be plotted. Inspect the evidence table.</p> : null}
        <ObservationChart label={`Measured cache interval rates (${cacheIntervalRateLabel(metric)})`} fromUtc={result.fromUtc} toUtc={result.toUtc}
          series={trend.series} valueFormatter={formatCacheIntervalRate} onSelectWindow={onSelectWindow} />
        {result.nextCursor && result.items.length < 1000 ? <p>Load earlier cache samples to extend the loaded evidence.</p> : null}
      </section>}
      {result.items.length === 0 ? <p>No samples for this query plan and database incarnation are available in this window.</p> : <div className="table-scroll"><table>
        <caption>Measured intervals and comparison gaps, newest first</caption>
        <thead><tr><th scope="col">Capture / cache entry</th><th scope="col">Comparison / interval</th><th scope="col">CPU ms</th><th scope="col">Duration ms</th><th scope="col">Executions</th><th scope="col">Reads / writes / rows</th>{canReadText ? <th scope="col">Captured content</th> : null}</tr></thead>
        <tbody>{result.items.map(row => <tr key={`${row.collectionRunId}:${row.cacheEntryFingerprint}`}>
          <td>{timeLabel(row.observedAtUtc)}<div className="fingerprint" title={`Cache entry ${row.cacheEntryFingerprint}; source epoch ${row.sourceEpoch}`}>{row.cacheEntryFingerprint.slice(0, 12)}…</div>{!row.captureComplete ? <div>Incomplete capture</div> : null}</td>
          <td>{states[row.transition]}{row.intervalFromUtc && row.intervalToUtc ? <div>{timeLabel(row.intervalFromUtc)} – {timeLabel(row.intervalToUtc)}</div> : null}</td>
          <td>{label(row.cpuMilliseconds)}</td><td>{label(row.durationMilliseconds)}</td><td>{label(row.executions)}</td>
          <td>{label(row.logicalReads)} / {label(row.writes)} / {label(row.rows)}</td>
          {canReadText ? <td><button type="button" onClick={() => setContentSelection({ key, row, kind: "text" })}>Open text</button>
            <button type="button" onClick={() => setContentSelection({ key, row, kind: "plan" })}>Open plan XML</button></td> : null}
        </tr>)}</tbody>
      </table></div>}
      {result.nextCursor && result.items.length < 1000 ? <button type="button" disabled={current?.loading} onClick={more}>{current?.loading ? "Loading earlier samples…" : "Load earlier cache samples"}</button> : null}
      {result.nextCursor && result.items.length >= 1000 ? <p>The view is limited to 1,000 samples. Select a smaller time window to inspect earlier activity.</p> : null}
    </>}
  </section>;
}
