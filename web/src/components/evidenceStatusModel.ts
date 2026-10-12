import type { OverviewSeries } from "../features/overview/overviewTypes";

/** Describes loaded observations only; never turns coverage into a health verdict. */
export function chartEvidenceStatus(series: readonly OverviewSeries[]) {
  const rows = series.map(item => {
    const observed = item.points.filter(point => point.value !== null && Number.isFinite(point.value) && point.samples > 0).length;
    return { label: item.dimension ?? item.label, state: item.state.replaceAll("_", " "),
      observed, missing: item.points.length - observed, total: item.points.length };
  });
  const missing = rows.reduce((count, row) => count + row.missing, 0);
  const observed = rows.reduce((count, row) => count + row.observed, 0);
  const states = [...new Set(rows.map(row => row.state))];
  const attention = !observed || missing > 0 || states.some(state => !["observed", "current", "complete"].includes(state));
  const summary = !rows.length ? "no source series" : !observed ? "no observed samples"
    : `${states.join(", ")}${missing ? ` · ${missing} missing sample buckets` : ""}`;
  return { rows, summary, attention };
}
