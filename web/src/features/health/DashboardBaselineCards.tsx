import { useId } from "react";
import { dashboardBaselineCards, type DashboardBaselineInput } from "./dashboardBaselineModel.ts";

export function DashboardBaselineCards({ formatTime, baselineStatus, ...input }: DashboardBaselineInput & {
  readonly formatTime: (value: string) => string; readonly baselineStatus?: string;
}) {
  const headingId = useId();
  const cards = dashboardBaselineCards(input);
  const number = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  return <section className="dashboard-baseline-summary" aria-labelledby={headingId} data-testid="dashboard-baseline-cards">
    <h4 id={headingId}>Latest SQL buckets and hourly baselines</h4>
    <p className="table-note">Each mean uses the latest observed bucket in the loaded window. Reference percentiles describe matching UTC-hour history; they are not a healthy range, an alert verdict, or proof of what was known at Rewind time.</p>
    <div className="dashboard-baseline-grid">{cards.map(card => <section key={card.metric} className="dashboard-baseline-card"
      aria-label={`${card.label} baseline comparison`} data-testid={`dashboard-baseline-${card.metric.slice(7)}`}
      data-observation-state={card.observationState} data-reference-state={card.referenceState}>
      <p className="dashboard-baseline-label"><strong>{card.label}</strong></p>
      <p className="dashboard-baseline-value">{card.observation?.value !== null && card.observation?.value !== undefined
        ? <><strong>{number(card.observation.value)}</strong> <span>{card.unit}</span></> : <span>Mean unavailable</span>}</p>
      <p className="dashboard-baseline-note">{card.observationNote}</p>
      {card.observation && <p className="dashboard-baseline-observation">Bucket: {formatTime(card.observation.timeUtc)} · {card.observation.samples} sample{card.observation.samples === 1 ? "" : "s"}</p>}
      {card.reference ? <dl className="dashboard-baseline-percentiles">
        <div><dt>p10</dt><dd>{number(card.reference.low)} {card.unit}</dd></div>
        <div><dt>Median</dt><dd>{number(card.reference.median)} {card.unit}</dd></div>
        <div><dt>p90</dt><dd>{number(card.reference.high)} {card.unit}</dd></div>
      </dl> : <p className="dashboard-baseline-reference">{card.referenceNote}</p>}
      {card.reference && <p className="dashboard-baseline-reference">Reference quality: {card.referenceQuality === "unknown" ? "quality unknown" : card.referenceQuality}.
        {card.reference.coverage !== undefined && ` Source coverage ${number(card.reference.coverage * 100)}%.`}
        {card.reference.confidence !== undefined && ` Confidence ${number(card.reference.confidence * 100)}%.`}</p>}
      <details><summary>{card.label} source details</summary>
        <p>{card.semantics} The displayed timestamp identifies the bucket, not a continuous reading.</p>
        <p>Loaded window: {formatTime(input.fromUtc)} to {formatTime(input.toUtc)} (end excluded). Dimension: server-wide. Target: {input.targetId}.</p>
        {card.observation && <p>SQL series state: {card.observation.sourceState.replaceAll("_", " ")}.</p>}
        {card.reference && <><p>{card.referenceNote}</p><p>{card.reference.label}</p>
          {card.reference.sourceFromUtc && card.reference.sourceToUtc && <p>Source history: {formatTime(card.reference.sourceFromUtc)} to {formatTime(card.reference.sourceToUtc)}.</p>}
          <p>Matching reference interval: {formatTime(card.reference.fromUtc)} to {formatTime(card.reference.toUtc)} (end excluded).</p></>}
      </details>
    </section>)}</div>
    {baselineStatus && <details className="dashboard-baseline-source-status"><summary>Baseline reference coverage</summary><p>{baselineStatus}</p></details>}
  </section>;
}
