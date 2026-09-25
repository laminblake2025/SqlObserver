import type { FleetAlert } from "../features/alerts/alertTypes";
import { overviewHref } from "../features/overview/overviewModel.ts";
import type { OverviewScope } from "../features/overview/overviewTypes";

export interface AlertSelection { readonly targetId: string; readonly alertId: string }

export function alertSelectionFromHash(hash: string): AlertSelection | undefined {
  const parameters = new URLSearchParams(hash.split("?")[1]);
  const targetId = parameters.get("alertTarget");
  const alertId = parameters.get("alert");
  return targetId && alertId ? { targetId, alertId } : undefined;
}

export function alertJumpHref(scope: OverviewScope, alert: Pick<FleetAlert, "targetId" | "alertId">): string {
  const href = overviewHref(scope, "alerts", "");
  return `${href}&alertTarget=${encodeURIComponent(alert.targetId)}&alert=${encodeURIComponent(alert.alertId)}`;
}

export function matchingAlerts(alerts: readonly FleetAlert[], query: string): readonly FleetAlert[] {
  const needle = query.trim().toLocaleLowerCase();
  return alerts.filter(alert => !needle || [alert.targetName, alert.ruleName, alert.alertId, alert.targetId]
    .some(value => value.toLocaleLowerCase().includes(needle))).slice(0, 10);
}
