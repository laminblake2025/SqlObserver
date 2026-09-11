import type { AlertState } from "./alertTypes";

export type AlertFilter = "active" | "firing" | "all";

export function alertMatchesFilter(filter: AlertFilter, state: AlertState): boolean {
  if (filter === "all") return true;
  if (filter === "firing") return state === "firing";
  return state !== "resolved" && state !== "normal";
}
