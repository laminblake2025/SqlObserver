import { overviewHref } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";

export function issueHref(scope: OverviewScope, instanceId: string, destination: string, observedAtUtc: string | null): string {
  const href = overviewHref(scope, destination, instanceId);
  return destination === "activity" && observedAtUtc ? `${href}&at=${encodeURIComponent(observedAtUtc)}` : href;
}
