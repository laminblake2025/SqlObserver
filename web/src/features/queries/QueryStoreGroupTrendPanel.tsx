import { ObservationChart } from "../../components/ObservationChart";
import { queryStoreGroupTrendSeries, queryStoreGroupTrendSummary } from "./queryStoreGroupTrendModel";
import type { QueryStoreGroupTrend } from "./queryPerformanceTypes";

export type GroupTrendState =
  | { readonly status: "loading" }
  | { readonly status: "ready"; readonly trend: QueryStoreGroupTrend }
  | { readonly status: "error"; readonly message: string };

export function QueryStoreGroupTrendPanel({ state, onSelectWindow, timeLabel, waitCategoryNames }: {
  readonly state: GroupTrendState;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly timeLabel: (utc: string) => string;
  readonly waitCategoryNames: Readonly<Record<number, string>>;
}) {
  const trend = state.status === "ready" ? state.trend : undefined;
  const summary = trend ? queryStoreGroupTrendSummary(trend) : undefined;
  return <section className="panel" aria-label="Selected plan delta trend" aria-busy={state.status === "loading"}>
    <h2>Selected plan · hourly deltas</h2>
    <p>Comparable Query Store group changes for the current target revision. This opt-in source shows only observed groups. Missing hours and buckets without comparable rows are gaps, not zero activity.</p>
    {state.status === "loading" && <p role="status">Loading plan deltas…</p>}
    {state.status === "error" && <p role="status">{state.message}</p>}
    {trend && summary && <>
      <p className="activity-evidence">{summary.comparableRows} comparable rows · {summary.baselineRows} baselines · {summary.resetRows} resets · {summary.ambiguousRows} ambiguous epochs · {summary.missingBuckets} hours without group rows. Wait deltas: {summary.waitComparableRows} comparable · {summary.waitBaselineRows} baseline · {summary.waitResetRows} reset · {summary.waitUnavailableRows} unavailable · {summary.waitStaleRows} stale.</p>
      {trend.points.length === 0 ? <p>No group deltas were observed for this plan in the selected window. The collector may be disabled or have no matching Query Store activity.</p>
        : <ObservationChart label="Hourly comparable plan CPU and duration (ms)" fromUtc={trend.fromUtc} toUtc={trend.toUtc}
            series={queryStoreGroupTrendSeries(trend)} onSelectWindow={onSelectWindow} />}
      {summary.waits.length > 0 && <details className="supporting-evidence"><summary>Wait categories from comparable deltas</summary>
        <ul>{summary.waits.map(wait => <li key={wait.category}>{waitCategoryNames[wait.category] ?? `Category ${wait.category}`}: {wait.milliseconds} ms</li>)}</ul>
      </details>}
      {trend.points.length > 0 && <details className="supporting-evidence"><summary>Hourly evidence and coverage</summary>
        <div className="table-scroll"><table><caption>Exact additive values from comparable rows; unavailable values are not zero</caption>
          <thead><tr><th scope="col">Hour</th><th scope="col">CPU ms</th><th scope="col">Duration ms</th><th scope="col">Executions</th><th scope="col">Runtime coverage</th><th scope="col">Wait coverage</th></tr></thead>
          <tbody>{trend.points.map(point => <tr key={point.bucketStartUtc}><td>{timeLabel(point.bucketStartUtc)}</td>
            <td>{point.cpuMilliseconds ?? "—"}</td><td>{point.durationMilliseconds ?? "—"}</td>
            <td>{point.executions ?? "—"}</td>
            <td>{point.runtimeComparableRows} comparable / {point.runtimeBaselineRows} baseline / {point.runtimeResetRows} reset / {point.runtimeAmbiguousRows} ambiguous</td>
            <td>{point.waitComparableRows} comparable / {point.waitBaselineRows} baseline / {point.waitResetRows} reset / {point.waitUnavailableRows} unavailable / {point.waitStaleRows} stale</td>
          </tr>)}</tbody></table></div>
      </details>}
    </>}
  </section>;
}
