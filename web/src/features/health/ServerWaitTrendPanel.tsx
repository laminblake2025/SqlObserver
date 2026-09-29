import { useEffect, useState } from "react";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewSeries } from "../overview/overviewTypes";
import { waitTrendChartData } from "../activity/waitTrendChartModel";
import { getServerWaitTrend, type WaitTrendResponse } from "../activity/waitTrendApi";

export function ServerWaitTrendPanel({ instanceId, fromUtc, toUtc, refresh, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly instanceId: string;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly refresh: number;
  readonly crosshairUtc: string | null;
  readonly onCrosshairChange: (utc: string | null) => void;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const [trend, setTrend] = useState<WaitTrendResponse>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(false);
  const duration = Date.parse(toUtc) - Date.parse(fromUtc);
  const available = Number.isFinite(duration) && duration > 0 && duration <= 24 * 3_600_000;

  useEffect(() => {
    const controller = new AbortController();
    setTrend(undefined);
    setError(undefined);
    if (!available) { setLoading(false); return () => controller.abort(); }
    setLoading(true);
    void getServerWaitTrend(instanceId, { fromUtc, toUtc }, controller.signal)
      .then(value => { if (!controller.signal.aborted) setTrend(value); })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Wait trend is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [instanceId, fromUtc, toUtc, refresh, available]);

  const chart = trend ? waitTrendChartData(trend) : undefined;
  const series: OverviewSeries[] = chart?.series.map(category => ({
    targetId: instanceId, metric: "wait.category.delta", label: "Wait time", unit: "ms / 5 min",
    state: chart.comparableBuckets === chart.totalBuckets ? "comparable" : "partial",
    dimension: category.category,
    points: category.points,
  })) ?? [];
  return <section className="panel server-dashboard-chart" aria-label="Wait categories over time">
    <h4>Wait categories</h4>
    {!available && <p className="overview-empty">Wait categories support a selected window of up to 24 hours. Choose a shorter range.</p>}
    {loading && <p role="status">Loading wait categories…</p>}
    {error && <p role="alert">{error} Refresh to retry.</p>}
    {trend && chart && <>
      <OverviewChart series={series} fromUtc={trend.fromUtc} toUtc={trend.toUtc}
        crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange} onSelectWindow={onSelectWindow} />
      <small>{chart.comparableBuckets} of {chart.totalBuckets} five-minute buckets are comparable across all categories. Unknown and partial buckets remain gaps.{chart.oversizedValues ? ` ${chart.oversizedValues} values exceed chart precision.` : ""}</small>
    </>}
  </section>;
}
