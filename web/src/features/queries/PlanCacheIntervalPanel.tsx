import { PlanCacheIntervalHistoryPanel } from "./PlanCacheIntervalHistoryPanel";
import { useEffect, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { getPlanCacheIntervalRanking } from "./queryPerformanceApi";
import type { PlanCacheHistorySelection, PlanCacheIntervalRanking, QueryPerformanceMetric } from "./queryPerformanceTypes";
import type { QueryWindow } from "./queryWindowModel";

const metrics: readonly [QueryPerformanceMetric, string][] = [
  ["cpu", "CPU"], ["duration", "Duration"], ["executions", "Executions"],
  ["logical_reads", "Logical reads"], ["writes", "Writes"], ["rows", "Rows"],
];
const decimalLabel = (value: string) => value.includes(".") ? value.replace(/0+$/u, "").replace(/\.$/u, "") : value;

export function PlanCacheIntervalPanel({ instanceId, timeWindow, refresh, databaseNames, metric, onMetricChange, selection, onSelect }: {
  readonly instanceId: string; readonly timeWindow: QueryWindow; readonly refresh: number;
  readonly metric: QueryPerformanceMetric; readonly onMetricChange: (metric: QueryPerformanceMetric) => void;
  readonly selection?: PlanCacheHistorySelection; readonly onSelect: (selection?: PlanCacheHistorySelection) => void;
  readonly databaseNames: ReadonlyMap<number, string | undefined>;
}) {
  const timeLabel = useTimeFormatter();
  const [state, setState] = useState<{ readonly key: string; readonly result?: PlanCacheIntervalRanking; readonly error?: string }>();
  const key = JSON.stringify([instanceId, timeWindow.fromUtc, timeWindow.toUtc, metric, refresh]);
  const current = state?.key === key ? state : undefined;
  useEffect(() => {
    const controller = new AbortController();
    void getPlanCacheIntervalRanking(instanceId, timeWindow.fromUtc, timeWindow.toUtc, metric, controller.signal)
      .then(result => { if (!controller.signal.aborted) setState({ key, result }); })
      .catch(() => { if (!controller.signal.aborted) setState({ key, error: "Cache interval evidence is unavailable. Retry when collection and repository access are available." }); });
    return () => controller.abort();
  }, [instanceId, timeWindow.fromUtc, timeWindow.toUtc, metric, refresh, key]);
  const result = current?.result;
  return <section className="panel" aria-label="Cache interval deltas">
    <h4>Cache interval deltas</h4>
    <p>Activity measured between qualified cache captures in the selected window. First samples, resets and comparisons crossing the window boundary contribute no total. Database recreations are ranked separately. Cache eviction and incomplete captures can hide activity.</p>
    <label>Rank cache activity by <select value={metric} onChange={event => onMetricChange(event.target.value as QueryPerformanceMetric)}>
      {metrics.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
    </select></label>
    {!current ? <p role="status">Loading cache interval evidence…</p> : null}
    {current?.error ? <p role="alert">{current.error}</p> : null}
    {result && <>
      <p className="activity-evidence">{result.comparableRows} comparable samples · {result.unavailableRows} unavailable comparisons · {result.boundaryRows} boundary crossings excluded · {result.incompleteFrames} of {result.sourceFrames} captures incomplete</p>
      {result.items.length === 0 ? <p>No comparable cache activity was recorded in this window. At least two qualified captures are needed; an empty result does not prove there was no activity.</p> : <div className="table-scroll"><table>
        <caption>Top 25 observed cache query plans by {metrics.find(([value]) => value === metric)?.[1].toLowerCase()}</caption>
        <thead><tr><th scope="col">Database / query</th><th scope="col">Plan</th><th scope="col">CPU ms</th><th scope="col">Duration ms</th><th scope="col">Executions</th><th scope="col">Reads / writes / rows</th><th scope="col">Samples / latest</th></tr></thead>
        <tbody>{result.items.map(row => <tr key={`${row.databaseId}:${row.databaseIncarnation}:${row.queryFingerprint}:${row.planFingerprint}`}>
          <td>{databaseNames.get(row.databaseId) ?? `Database ID ${row.databaseId}`}<span className="fingerprint"> / {row.queryFingerprint.slice(0, 12)}…</span></td>
          <td className="fingerprint"><button type="button" onClick={() => onSelect({ targetId: instanceId, databaseId: row.databaseId, databaseIncarnation: row.databaseIncarnation, queryFingerprint: row.queryFingerprint, planFingerprint: row.planFingerprint })}>Inspect {row.planFingerprint.slice(0, 12)}…</button></td>
          <td>{decimalLabel(row.cpuMilliseconds)}</td><td>{decimalLabel(row.durationMilliseconds)}</td><td>{row.executions}</td>
          <td>{row.logicalReads} / {row.writes} / {row.rows}</td><td>{row.comparableRows} / {timeLabel(row.lastObservedAtUtc)}</td>
        </tr>)}</tbody>
      </table></div>}
      {result.hasMore ? <p>More query plans were observed; this ranking shows the first 25.</p> : null}
    </>}
    {selection?.targetId === instanceId ? <PlanCacheIntervalHistoryPanel selection={selection} timeWindow={timeWindow} refresh={refresh} onClose={() => onSelect(undefined)} /> : null}
  </section>;
}
