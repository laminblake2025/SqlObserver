import type { OverviewResource } from './overviewTypes';

/** Latest comparable collector deltas remain separate observations, never window totals. */
export function FleetWaitChart({ resources }: { readonly resources: readonly OverviewResource[] }) {
  const waits = resources.filter(row => row.label.startsWith('Wait · '));
  const comparable = waits.filter(row => row.value !== null && row.value >= 0 && ['observed', 'partial'].includes(row.state))
    .sort((left, right) => right.value! - left.value!).slice(0, 5);
  const maximum = Math.max(1, ...comparable.map(row => row.value!));
  return <>
    {comparable.length ? <div className="fleet-wait-bars" role="img" aria-label="Largest loaded wait deltas in seconds since each server's prior sample">
      {comparable.map(row => <div className="fleet-wait-bar" key={`${row.targetId}:${row.label}`}>
        <span>{row.label.slice(7)}<small>{row.server} · {row.state}</small></span>
        <span className="wait-bar-track"><i style={{ width: `${row.value! / maximum * 100}%` }} /></span>
        <strong>{row.value!.toLocaleString(undefined, { maximumFractionDigits: 1 })}s</strong>
      </div>)}
    </div> : <p className="overview-empty">No comparable wait deltas available. Missing or stale observations are unavailable.</p>}
    <details className="chart-context-details"><summary>Wait evidence & qualifications ({waits.length})</summary>
      <p>Each bar is a separate server's latest comparable sample delta. Collection intervals can differ; these are not totals for the selected window. Only the largest five loaded comparable deltas are charted.</p>
      {waits.map(row => <p key={`${row.targetId}:${row.label}`}><strong>{row.server} · {row.label}</strong>: {row.value === null ? 'Unavailable' : `${row.value.toLocaleString()} ${row.unit}`} · {row.state} · <time dateTime={row.observedAtUtc ?? undefined}>{row.observedAtUtc ?? 'Observation time unavailable'}</time></p>)}
    </details>
  </>;
}
