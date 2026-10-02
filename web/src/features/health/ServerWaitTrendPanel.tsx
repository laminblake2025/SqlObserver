import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewSeries } from "../overview/overviewTypes";
import { waitTrendChartData } from "../activity/waitTrendChartModel";
import { getServerWaitTrend, type WaitTrendResponse } from "../activity/waitTrendApi";
import { ActivityRequestError } from "../activity/activityParser.mjs";

export function ServerWaitTrendPanel({ instanceId, fromUtc, toUtc, refresh, markers, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly instanceId: string;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly refresh: number;
  readonly markers: readonly { readonly timeUtc: string; readonly label: string }[];
  readonly crosshairUtc: string | null;
  readonly onCrosshairChange: (utc: string | null) => void;
  readonly onSelectWindow: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const formatTime = useTimeFormatter();
  const instant = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
  let available = false, bounds: readonly string[] = [fromUtc, toUtc];
  try {
    const from = instant(fromUtc), to = instant(toUtc);
    available = from < to && to - from <= 24n * 3_600_000_000n;
    bounds = [from.toString(), to.toString()];
  } catch { /* Invalid windows have no displayed or retained evidence. */ }
  const context = JSON.stringify([instanceId.toLowerCase(), ...bounds]);
  const key = JSON.stringify([context, refresh]);
  const latest = useRef(key); latest.current = key;
  const [state, setState] = useState<{ readonly context: string; readonly key: string;
    readonly trend?: WaitTrendResponse; readonly error?: string; readonly loading: boolean }>();

  useEffect(() => {
    const controller = new AbortController();
    setState(previous => ({ context, key, loading: available,
      trend: available && previous?.context === context ? previous.trend : undefined }));
    if (!available) return () => controller.abort();
    const current = () => !controller.signal.aborted && latest.current === key;
    void getServerWaitTrend(instanceId, { fromUtc, toUtc }, controller.signal)
      .then(value => {
        if (value.instanceId.toLowerCase() !== instanceId.toLowerCase() ||
            instant(value.fromUtc) !== instant(fromUtc) || instant(value.toUtc) !== instant(toUtc))
          throw new Error("Wait trend returned evidence outside the selected UTC window.");
        if (current()) setState({ context, key, trend: value, loading: false });
      })
      .catch((failure: unknown) => {
        if (current()) setState(previous => ({ context, key, loading: false,
          trend: previous?.context === context && !(failure instanceof ActivityRequestError && (failure.status === 401 || failure.status === 403))
            ? previous.trend : undefined,
          error: failure instanceof Error ? failure.message : "Wait trend is unavailable." }));
      });
    return () => controller.abort();
  }, [instanceId, fromUtc, toUtc, key, available]);

  const current = available && state?.context === context ? state : undefined;
  const trend = current?.trend;
  const loading = available && (!current || current.key !== key || current.loading);
  const error = current?.key === key ? current.error : undefined;
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
    {loading && <p role="status">{trend
      ? `Refreshing wait categories… Showing the previous snapshot in this same window, read at ${formatTime(trend.repositoryTimeUtc)}.`
      : "Loading wait categories…"}</p>}
    {error && <p role="alert">{error} {trend
      ? "Showing previously loaded wait categories in this same window."
      : "No wait trend was loaded for this context."} Refresh to retry.</p>}
    {trend && chart && <>
      <p className="table-note">Wait trend window: <time dateTime={trend.fromUtc}>{formatTime(trend.fromUtc)}</time> to{" "}
        <time dateTime={trend.toUtc}>{formatTime(trend.toUtc)}</time> (exclusive). Snapshot read at{" "}
        <time dateTime={trend.repositoryTimeUtc}>{formatTime(trend.repositoryTimeUtc)}</time>.</p>
      <OverviewChart series={series} fromUtc={trend.fromUtc} toUtc={trend.toUtc}
        markers={markers} crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange} onSelectWindow={onSelectWindow} />
      <small>{chart.comparableBuckets} of {chart.totalBuckets} five-minute buckets are comparable across all categories. Unknown and partial buckets remain gaps.{chart.oversizedValues ? ` ${chart.oversizedValues} values exceed chart precision.` : ""}</small>
    </>}
  </section>;
}
