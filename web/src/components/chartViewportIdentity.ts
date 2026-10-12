import { readOverviewScope } from "../features/overview/overviewModel.ts";

/** A moving Live window is a refresh, not a user navigation. */
export function chartViewportIdentity(series: readonly string[], hash: string): string {
  const scope = readOverviewScope(hash);
  return JSON.stringify([scope.target, scope.range, scope.range === "custom" ? [scope.from, scope.to] : null, series]);
}
