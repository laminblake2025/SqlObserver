import { QueryObservationChart as ObservationChart, useSharedQueryChartInspection } from "./QueryChartInspection.tsx";
import { useEffect, useState, type FormEvent } from "react";
import { queryStoreGroupTrendSeries, queryStoreGroupTrendSummary, queryStoreGroupWaitCategories, queryStoreGroupWaitTrend, queryPlanInspectionUtc } from "./queryStoreGroupTrendModel";
import type { QueryStoreGroupTrend } from "./queryPerformanceTypes";
import type { PlanTrendSelection } from "./queryViewModel";
import { customUtcInputValue } from "../overview/overviewModel.ts";

export type GroupTrendState =
  | { readonly status: "loading" }
  | { readonly status: "ready"; readonly trend: QueryStoreGroupTrend }
  | { readonly status: "error"; readonly message: string };

export function QueryStoreGroupTrendPanel({ state, onSelectWindow, timeLabel, waitCategoryNames, selection, onSelectionChange }: {
  readonly state: GroupTrendState;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly timeLabel: (utc: string) => string;
  readonly waitCategoryNames: Readonly<Record<number, string>>;
  readonly selection?: PlanTrendSelection;
  readonly onSelectionChange: (selection: Omit<PlanTrendSelection, "context">) => void;
}) {
  const trend = state.status === "ready" ? state.trend : undefined;
  const summary = trend ? queryStoreGroupTrendSummary(trend) : undefined;
  return <section className="panel server-dashboard-chart" aria-label="Selected plan delta trend" aria-busy={state.status === "loading"}>
    <h3>Selected plan · hourly deltas</h3>
    <p>Comparable Query Store group changes for the current target revision. This opt-in source shows only observed groups. Missing hours and buckets without comparable rows are gaps, not zero activity.</p>
    {state.status === "loading" && <p role="status">Loading plan deltas…</p>}
    {state.status === "error" && <p role="status">{state.message}</p>}
    {trend && summary && <>
      <p className="activity-evidence">{summary.comparableRows} comparable rows · {summary.baselineRows} baselines · {summary.resetRows} resets · {summary.ambiguousRows} ambiguous epochs · {summary.missingBuckets} hours without group rows. Wait deltas: {summary.waitComparableRows} comparable · {summary.waitBaselineRows} baseline · {summary.waitResetRows} reset · {summary.waitUnavailableRows} unavailable · {summary.waitStaleRows} stale.</p>
      {trend.points.length === 0 ? <p>No group deltas were observed for this plan in the selected window. The collector may be disabled or have no matching Query Store activity.</p>
        : <PlanTrendCharts key={`${trend.fromUtc}:${trend.toUtc}`} trend={trend} onSelectWindow={onSelectWindow} timeLabel={timeLabel} waitCategoryNames={waitCategoryNames} selection={selection} onSelectionChange={onSelectionChange} />}
      {summary.waits.length > 0 && <details className="supporting-evidence"><summary>Wait categories from comparable deltas</summary>
        <ul>{summary.waits.map(wait => <li key={wait.category}>{waitCategoryNames[wait.category] ?? `Category ${wait.category}`}: {wait.milliseconds} ms</li>)}</ul>
      </details>}
      {trend.points.length > 0 && <details className="supporting-evidence"><summary>Hourly evidence and coverage</summary>
        <div className="table-scroll" tabIndex={0} role="region" aria-label="Query Store comparable group values"><table className="evidence-table"><caption>Exact additive values from comparable rows; unavailable values are not zero</caption>
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

function PlanTrendCharts({ trend, onSelectWindow, timeLabel, waitCategoryNames, selection, onSelectionChange }: {
  readonly trend: QueryStoreGroupTrend;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly timeLabel: (utc: string) => string;
  readonly waitCategoryNames: Readonly<Record<number, string>>;
  readonly selection?: PlanTrendSelection;
  readonly onSelectionChange: (selection: Omit<PlanTrendSelection, "context">) => void;
}) {
  const sharedInspection = useSharedQueryChartInspection();
  const category = selection?.category ?? null;
  const [input, setInput] = useState("");
  const [edited, setEdited] = useState(false);
  const [hover, setHover] = useState<string | null>(null);
  const [error, setError] = useState<string>();
  const waits = selection?.categoryInvalid ? undefined : queryStoreGroupWaitTrend(trend, category);
  const categories = queryStoreGroupWaitCategories(trend);
  const name = (value: number) => waitCategoryNames[value] ?? `Category ${value} (unknown label)`;
  let pinned: string | null = null;
  let invalidInspection = Boolean(selection?.inspectionInvalid);
  if (selection?.inspectionUtc) try { pinned = queryPlanInspectionUtc(selection.inspectionUtc.replace(/Z$/u, ""), trend.fromUtc, trend.toUtc); }
    catch { invalidInspection = true; }
  const crosshair = hover ?? pinned;
  useEffect(() => { if (selection?.inspectionUtc) setInput(customUtcInputValue(selection.inspectionUtc)); setEdited(false); setHover(null); }, [selection?.inspectionUtc, selection?.inspectionInvalid]);
  function inspectPointer(utc: string | null) {
    try { setHover(utc === null ? null : queryPlanInspectionUtc(utc.replace(/Z$/u, ""), trend.fromUtc, trend.toUtc)); }
    catch { setHover(null); }
  }
  function align(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    try { const inspectionUtc = queryPlanInspectionUtc(input, trend.fromUtc, trend.toUtc, selection?.inspectionUtc, edited); onSelectionChange({ ...selection, inspectionUtc, inspectionInvalid: undefined }); setHover(null); setError(undefined); }
    catch (failure) { onSelectionChange({ ...selection, inspectionUtc: undefined, inspectionInvalid: undefined }); setHover(null); setError(failure instanceof Error ? failure.message : "Choose a valid plan inspection time."); }
  }
  return <>
    {sharedInspection && <p className="table-note">Shared Queries alignment governs chart markers when selected. Plan-only alignment is retained separately; clear shared Queries alignment to use the saved plan-only marker.</p>}
    <form className="toolbar" aria-label="Plan chart inspection" onSubmit={align}>
      <label>Plan inspection time (UTC)<input type="datetime-local" step="0.001" value={input} onChange={event => { setInput(event.target.value); setEdited(true); }} /></label>
      <button type="submit" className="secondary-button">Align plan charts</button>
      <button type="button" className="secondary-button" disabled={crosshair === null && !error && !invalidInspection} onClick={() => { onSelectionChange({ ...selection, inspectionUtc: undefined, inspectionInvalid: undefined }); setHover(null); setError(undefined); }}>Clear plan alignment</button>
    </form>
    <p className="table-note">UTC input edits use milliseconds. An unchanged restored inspection retains its exact saved precision. Pointer inspection is temporary and does not change the shared URL.</p>
    {error && pinned === null && <p role="alert">{error}</p>}
    {invalidInspection && <p role="status">The requested plan inspection time is invalid or outside the loaded window. No alignment has been substituted; align another time or clear plan alignment.</p>}
    {crosshair && <p role="status">Plan inspection time: <time dateTime={crosshair}>{timeLabel(crosshair)}</time></p>}
    <ObservationChart label="Hourly comparable plan CPU and duration (ms)" fromUtc={trend.fromUtc} toUtc={trend.toUtc}
      series={queryStoreGroupTrendSeries(trend)} crosshairUtc={crosshair} onCrosshairChange={inspectPointer} onSelectWindow={onSelectWindow} />
    <section aria-label="Selected plan wait category trend">
      <h4>Hourly Query Store wait categories</h4>
      <label>Plan wait category <select value={selection?.categoryInvalid ? "invalid" : category === null ? "all" : String(category)} onChange={event => onSelectionChange({ ...selection, category: event.target.value === "all" ? null : Number(event.target.value), categoryInvalid: undefined })}>
        {selection?.categoryInvalid && <option disabled value="invalid">Invalid plan wait category</option>}
        {category !== null && !categories.includes(category) && <option value={category}>{name(category)} (not reported)</option>}
        <option value="all">All reported categories</option>{categories.map(value => <option key={value} value={value}>{name(value)}</option>)}
      </select></label>
      {selection?.categoryInvalid && <p role="status">The requested plan wait category is invalid or duplicated. Choose a supported category; no other wait category has been substituted.</p>}
      {!selection?.categoryInvalid && category !== null && !categories.includes(category) && <p role="status">The requested category has no reported comparable value in this plan window. Missing values have not been substituted with zero.</p>}
      <p>Comparable hourly wait sums from this exact plan and window. Missing category values and hours remain gaps. A bucket containing excluded groups supplies only a partial sum and does not connect to adjacent buckets. These Query Store categories are independent of server wait types.</p>
      {waits && <ObservationChart label="Hourly comparable plan waits (ms)" fromUtc={trend.fromUtc} toUtc={trend.toUtc}
        series={[{ ...waits.series, label: category === null ? "All reported category waits (ms)" : `${name(category)} waits (ms)` }]}
        crosshairUtc={crosshair} onCrosshairChange={inspectPointer} onSelectWindow={onSelectWindow} />}
      {waits && waits.precisionOmissions > 0 && <p className="table-note">{waits.precisionOmissions} hourly values exceed chart precision and are omitted from the plot. Exact counters remain below.</p>}
      {waits && <details className="supporting-evidence"><summary>Hourly wait values and coverage</summary>
        <div className="table-scroll" tabIndex={0} role="region" aria-label="Selected plan hourly wait values"><table className="evidence-table">
          <caption>Exact {category === null ? "reported category" : name(category)} wait milliseconds from comparable groups; gaps are not zero</caption>
          <thead><tr><th scope="col">Hour</th><th scope="col">Wait ms</th><th scope="col">Coverage</th></tr></thead>
          <tbody>{waits.values.map(value => <tr key={value.timeUtc}><td className="evidence-scalar">{timeLabel(value.timeUtc)}</td><td className="evidence-scalar">{value.exact ?? "—"}</td><td>{value.note}</td></tr>)}</tbody>
        </table></div>
      </details>}
    </section>
  </>;
}
