import type { ReactNode } from "react";
import type { OverviewSeries } from "../features/overview/overviewTypes";
import { chartEvidenceStatus } from "./evidenceStatusModel.ts";
import "./compactEvidenceStatus.css";

export function EvidenceDisclosure({ label, attention = false, children }: {
  readonly label: string; readonly attention?: boolean; readonly children: ReactNode;
}) {
  return <details className={`compact-evidence${attention ? " compact-evidence-attention" : ""}`}>
    <summary><span className="compact-evidence-dot" aria-hidden="true" />{label}</summary>
    <div className="compact-evidence-content">{children}</div>
  </details>;
}

export function ChartEvidenceIndicator({ label, series }: { readonly label: string; readonly series: readonly OverviewSeries[] }) {
  const status = chartEvidenceStatus(series);
  return <EvidenceDisclosure label={`${label} evidence: ${status.summary}`} attention={status.attention}>
    {status.rows.length ? <ul>{status.rows.map((row, index) => <li key={index}>{row.label}: {row.state} · {row.observed} observed of {row.total} loaded sample buckets{row.missing ? ` · ${row.missing} missing` : ""}.</li>)}</ul>
      : <p>No source series was returned for this chart. Check collection and target access, then refresh.</p>}
    <p>Counts describe the loaded buckets. They do not establish continuous coverage, freshness, or a health verdict. Missing observations remain gaps.</p>
  </EvidenceDisclosure>;
}

export function ReferenceEvidenceStatus({ baselineStatus, ruleStatus, baselineAttention = true, ruleAttention = true }: {
  readonly baselineStatus: string; readonly ruleStatus: string; readonly baselineAttention?: boolean; readonly ruleAttention?: boolean;
}) {
  return <div className="compact-evidence-references">
    {baselineAttention && baselineStatus && <p className="compact-evidence-action">{baselineStatus}</p>}
    <EvidenceDisclosure label="Baseline reference details" attention={baselineAttention}>
      <p>{baselineStatus}</p>
    </EvidenceDisclosure>
    {ruleAttention && ruleStatus && <p className="compact-evidence-action">{ruleStatus}</p>}
    <EvidenceDisclosure label="Rule threshold details" attention={ruleAttention}>
      <p>{ruleStatus}</p>
    </EvidenceDisclosure>
  </div>;
}
