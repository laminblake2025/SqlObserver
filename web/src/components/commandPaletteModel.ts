import type { FleetAlert } from "../features/alerts/alertTypes";
import { overviewHref } from "../features/overview/overviewModel.ts";
import type { OverviewScope } from "../features/overview/overviewTypes";
import type { DatabaseHealthSummary } from "../features/health/healthTypes";

export interface AlertSelection { readonly targetId: string; readonly alertId: string }

export function alertSelectionFromHash(hash: string): AlertSelection | undefined {
  if (hash.split("?")[0] !== "#/alerts") return undefined;
  const parameters = new URLSearchParams(hash.split("?")[1]);
  if (["target", "alertTarget", "alert"].some(key => parameters.getAll(key).length > 1)) return undefined;
  const selectedTarget = parameters.get("target");
  const alertTarget = parameters.get("alertTarget");
  const targetId = selectedTarget || alertTarget;
  const alertId = parameters.get("alert");
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
  if (!targetId || !alertId || !guid.test(targetId) || !guid.test(alertId)) return undefined;
  if (selectedTarget && alertTarget && selectedTarget.toLowerCase() !== alertTarget.toLowerCase()) return undefined;
  return { targetId: targetId.toLowerCase(), alertId: alertId.toLowerCase() };
}

export function alertJumpHref(scope: OverviewScope, alert: Pick<FleetAlert, "targetId" | "alertId">): string {
  const href = overviewHref(scope, "alerts", "");
  return `${href}&alertTarget=${encodeURIComponent(alert.targetId)}&alert=${encodeURIComponent(alert.alertId)}`;
}

export function targetAlertHref(scope: OverviewScope, targetId: string, alertId: string): string {
  return `${overviewHref(scope, "alerts", targetId)}&alert=${encodeURIComponent(alertId)}`;
}

export function matchingAlerts(alerts: readonly FleetAlert[], query: string): readonly FleetAlert[] {
  const needle = query.trim().toLocaleLowerCase();
  return alerts.filter(alert => !needle || [alert.targetName, alert.ruleName, alert.alertId, alert.targetId]
    .some(value => value.toLocaleLowerCase().includes(needle))).slice(0, 10);
}

export function databaseJumpHref(scope: OverviewScope, targetId: string, databaseId: number): string {
  return overviewHref({ ...scope, target: targetId, databaseId }, "health", targetId);
}

export function matchingDatabases(databases: readonly DatabaseHealthSummary[], query: string): readonly DatabaseHealthSummary[] {
  const needle = query.trim().toLocaleLowerCase();
  return databases.filter(database => !needle || database.name.toLocaleLowerCase().includes(needle)
    || String(database.databaseId).includes(needle)).slice(0, 10);
}

export function queryJumpHref(scope: OverviewScope, targetId: string, databaseId: number,
    queryFingerprint: string, planFingerprint?: string,
    source?: "query_store" | "plan_cache"): string {
  if (planFingerprint !== undefined && !/^[0-9a-f]{64}$/iu.test(planFingerprint))
    throw new Error("Plan fingerprint is invalid.");
  if (planFingerprint !== undefined && source === "plan_cache")
    throw new Error("A Query Store plan cannot use a plan-cache source.");
  return `${overviewHref({ ...scope, target: targetId, databaseId }, "queries", targetId)}&queryFingerprint=${queryFingerprint.toLowerCase()}`
    + (planFingerprint === undefined ? "" : `&planFingerprint=${planFingerprint.toLowerCase()}`)
    + (source === undefined ? "" : `&source=${source}`);
}
