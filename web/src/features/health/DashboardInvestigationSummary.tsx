import { useId } from "react";
import { buildInvestigationSummary } from "./investigationSummaryModel.ts";
import type { InvestigationSummaryInput, InvestigationSummary } from "./investigationSummaryModel.ts";
import { HeadBlockerAnswer } from "./HeadBlockerAnswer.tsx";
import type { CurrentBlockingEvidence } from "./useCurrentBlockingEvidence.ts";

export function DashboardInvestigationSummary({ targetId, scope, fromUtc, toUtc, issues, events, formatTime, loading = false, sourceNotes = [], blocking }:
  InvestigationSummaryInput & { readonly formatTime: (value: string) => string; readonly loading?: boolean; readonly sourceNotes?: readonly string[];
    readonly blocking?: { evidence: CurrentBlockingEvidence; targetRevision: number; onInspect: (sessionId: number) => void } }) {
  const heading = useId();
  let summary: InvestigationSummary;
  try { summary = buildInvestigationSummary({ targetId, scope, fromUtc, toUtc, issues, events }); }
  catch { return <section className="panel dashboard-investigation" aria-labelledby={heading}>
    <p className="eyebrow">Investigate first</p><h4 id={heading}>What needs attention</h4>
    <p role="status">Investigation evidence could not be bound to this server and selected window. Refresh to load comparable evidence.</p>
  </section>; }
  return <section className="panel dashboard-investigation" aria-labelledby={heading} aria-busy={loading}>
    <p className="eyebrow">Investigate first</p><h4 id={heading}>What needs attention</h4>
    {blocking && <HeadBlockerAnswer {...blocking} formatTime={formatTime} />}
    <p className="dashboard-investigation-note">Loaded exceptions and observed plan comparisons. Temporal proximity does not establish causation or the actual change time.</p>
    {loading && <p className="dashboard-investigation-note" role="status">Refreshing investigation evidence; any retained findings and context belong to the displayed snapshot window.</p>}
    {summary.items.length ? <ol className="dashboard-investigation-findings">{summary.items.map(finding => <li key={finding.id}>
      <a className="dashboard-investigation-answer" href={finding.href}><strong>{finding.title}</strong><span>{finding.detail}</span><small>
        {finding.databaseId === undefined ? "Server-wide evidence" : `Database ${finding.databaseId}`}
        {finding.timeUtc ? ` · ${finding.timeBasis === "snapshot" ? "Evidence snapshot" : "Last observed query group"} ${formatTime(finding.timeUtc)}` : " · Observation time unavailable"}
        {finding.windowRelation === "outside_window" ? " · Latest overview evidence outside the selected history window" : finding.windowRelation === "unknown" ? " · Cannot place this finding in the selected history window" : ""}
        {" · Open evidence →"}</small></a>
      {finding.contexts.length > 0 && <details className="dashboard-investigation-context"><summary>Observed temporal context ({finding.contexts.length})</summary>
        <p>Observations within 15 minutes of this finding timestamp. Detection and query observation times do not establish when a configuration or plan changed.</p>
        <ul>{finding.contexts.map(context => <li key={context.event.id}>
          <a href={context.event.href}><strong>{context.event.title}</strong></a>
          <span>{context.databaseId === undefined ? "Server-wide configuration" : `Database ${context.databaseId} options`} · Detected {formatTime(context.event.timeUtc)}</span>
          <small>{context.relationship}. Temporal proximity does not establish causation or the actual change time.</small>
          <p>{context.event.detail}</p>
        </li>)}</ul>
      </details>}
    </li>)}</ol> : <p className="empty-state">No ranked exceptions or qualifying plan comparisons in the loaded window evidence. Check collection gaps and source coverage before treating this as all clear.</p>}
    <p className="dashboard-investigation-note">Showing {summary.items.length} of {summary.totalFindings} loaded window findings; at most five findings and three nearby change observations per finding. {blocking ? "Current graph roots are listed separately above. " : ""}{summary.omittedFindings > 0 ? `${summary.omittedFindings} additional loaded findings remain in their source screens. ` : ""}The list uses bounded source pages and may be incomplete. Overview exceptions remain server-wide, including with a database selected; temporal context is attached only inside the selected history window.</p>
    {summary.rejectedEvidence > 0 && <p className="dashboard-investigation-note" role="status">{summary.rejectedEvidence} loaded evidence entries were outside the comparable scope, duplicated or invalid and were excluded from temporal comparison.</p>}
    {sourceNotes.length > 0 && <details className="dashboard-investigation-coverage"><summary>Investigation source coverage</summary><ul>{sourceNotes.map((note, index) => <li key={`${index}:${note}`}>{note}</li>)}</ul></details>}
  </section>;
}
