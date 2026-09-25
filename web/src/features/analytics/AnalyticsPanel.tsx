import { useEffect, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { ObservationChart } from "../../components/ObservationChart";
import { getMetricSeries } from "./analyticsApi";
import { panelStateForSurface } from "./analyticsState";
import type { AnalyticsPanelState, MetricSeries } from "./analyticsTypes";

export function AnalyticsPanel({ targetId, metricKey }: { targetId: string; metricKey: string }) {
  const formatTime = useTimeFormatter();
  const [state, setState] = useState<AnalyticsPanelState>("loading");
  const [data, setData] = useState<MetricSeries | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setState("loading");
    setData(null);
    getMetricSeries(targetId, metricKey, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return;
        setData(value);
        setState(panelStateForSurface(value.state, value.items.length > 0));
      })
      .catch(error => {
        if (controller.signal.aborted) return;
        const status = error?.status;
        setState(status === 403 ? "permission-denied" : status === 404 ? "no-data" :
          status === 410 ? "retention-blocked" : status === 202 ? "backfilling" :
          status === 409 ? "stale" : "degraded");
      });
    return () => controller.abort();
  }, [targetId, metricKey]);

  if (state === "loading") return <section className="status-message">Loading analytics…</section>;
  if (state === "permission-denied") return <section className="status-message">You are not authorized to view this analytics surface.</section>;
  if (state === "unsupported") return <section className="status-message">This analytics surface is unsupported for the target.</section>;
  if (state === "backfilling") return <section className="status-message">Analytics are backfilling; data will appear when the job completes.</section>;
  if (state === "retention-blocked") return <section className="status-message">This analytics window is blocked by retention policy.</section>;
  if (state === "stale") return <section className="status-message">Analytics are stale; refresh to obtain a new snapshot.</section>;
  if (state === "no-data") return <section className="empty-state">No analytics data is available.</section>;
  if (!data) return <section className="status-message">Analytics are degraded.</section>;

  return <section className="target-card">
    <h2>{data.metricKey}</h2>
    <p>{state === "partial" ? "Data is partial or has a visibility gap." :
      state === "degraded" ? "Data is degraded or stale." : `${data.items.length} points`}</p>
    <ObservationChart label={data.metricKey} fromUtc={data.fromUtc} toUtc={data.toUtc}
      series={[{ id: data.metricKey, label: data.metricKey,
        items: data.items.map(item => ({ time: item.observedAtUtc, value: item.value })) }]} />
    <p>{formatTime(data.fromUtc)} to {formatTime(data.toUtc)}</p>
  </section>;
}
