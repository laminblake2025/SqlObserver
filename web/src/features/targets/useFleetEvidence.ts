import { useEffect, useState } from "react";
import { getTargetHealth } from "../health/healthApi";
import { getActiveAlerts } from "../alerts/alertApi";
import type { TargetHealthSnapshot } from "../health/healthTypes";
import type { ActiveAlertPage } from "../alerts/alertTypes";
import type { ObservationTargetSummary } from "./targetTypes";
export interface FleetEvidence { readonly health?: TargetHealthSnapshot; readonly alerts?: ActiveAlertPage; readonly error?: string; readonly loading?: boolean; readonly updatedAt?: string; }
export function useFleetEvidence(targets: readonly ObservationTargetSummary[], enabled: boolean, refresh = 0) {
  const [evidence, setEvidence] = useState<Readonly<Record<string, FleetEvidence>>>({});
  useEffect(() => {
    const controller = new AbortController();
    setEvidence(previous => Object.fromEntries(targets.map(target => [target.instanceId, {...previous[target.instanceId], loading: enabled}])));
    if (!enabled) return () => controller.abort();
    let index = 0;
    async function worker() {
      while (!controller.signal.aborted) {
        const target = targets[index++]; if (!target) return;
        let health: TargetHealthSnapshot | undefined, alerts: ActiveAlertPage | undefined, error: string | undefined;
        try { health = await getTargetHealth(target.instanceId, controller.signal); } catch { error = "Collection evidence unavailable"; }
        if (controller.signal.aborted) return;
        try { alerts = await getActiveAlerts(target.instanceId, controller.signal, 5); } catch { error = error ? `${error}; alerts unavailable` : "Alerts unavailable"; }
        if (!controller.signal.aborted) setEvidence(current => ({...current, [target.instanceId]: {health: health ?? current[target.instanceId]?.health, alerts: alerts ?? current[target.instanceId]?.alerts, error, loading: false, updatedAt: error ? current[target.instanceId]?.updatedAt : new Date().toISOString()}}));
      }
    }
    for (let i = 0; i < Math.min(4, targets.length); i++) void worker();
    return () => controller.abort();
  }, [targets, enabled, refresh]);
  return evidence;
}
export function lastMetric(health: TargetHealthSnapshot | undefined, key: string): string {
  const metric = health?.coreMetrics.filter(item => item.metricId === key && item.dimensions.length === 0).sort((a,b) => Date.parse(b.observedAtUtc) - Date.parse(a.observedAtUtc))[0];
  if (!metric) return "Unavailable";
  const value = key.endsWith('_bytes') ? `${(metric.value / 1073741824).toFixed(2)} GiB` : metric.value.toLocaleString();
  return `${value} · last known ${metric.observedAtUtc}`;
}
