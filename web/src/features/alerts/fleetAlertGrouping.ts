import { activityHistoryHref } from "../../dashboardModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import type { FleetAlert } from "./alertTypes";

export type FleetAlertGrouping = "none" | "server" | "rule";

export interface FleetAlertGroup {
  readonly key: string;
  readonly label: string;
  readonly items: readonly FleetAlert[];
}

export function groupFleetAlerts(items: readonly FleetAlert[], grouping: FleetAlertGrouping): readonly FleetAlertGroup[] {
  if (grouping === "none") return [{ key: "all", label: "", items }];
  const groups = new Map<string, { label: string; items: FleetAlert[] }>();
  for (const item of items) {
    const key = grouping === "server" ? item.targetId.toLowerCase() : item.ruleName.trim().toLowerCase();
    const label = grouping === "server" ? item.targetName : item.ruleName;
    let group = groups.get(key);
    if (!group) {
      group = { label, items: [] };
      groups.set(key, group);
    }
    group.items.push(item);
  }
  return [...groups].map(([key, group]) => ({ key, label: group.label, items: group.items }));
}

export function alertActivityHref(item: FleetAlert, scope: OverviewScope, now = Date.now()): string {
  const occurredAtUtc = item.firedUtc ?? item.firstObservedUtc;
  const occurred = Date.parse(occurredAtUtc);
  if (!Number.isFinite(occurred) || occurred > now)
    return activityHistoryHref(item.targetId, occurredAtUtc, undefined, scope);
  const from = new Date(occurred - 30 * 60000).toISOString();
  const to = new Date(Math.min(now, occurred + 30 * 60000)).toISOString();
  return activityHistoryHref(item.targetId, occurredAtUtc, undefined,
    { ...scope, range: "custom", from, to, compare: false });
}
