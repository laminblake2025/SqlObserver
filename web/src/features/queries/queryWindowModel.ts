import { overviewHref, overviewWindow, workspaceUtcTimestamp } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { utcMicroseconds } from "../../utcTimestamp.ts";

const maximumQueryWindowMilliseconds = 7 * 24 * 60 * 60 * 1000;

export type QueryWindow = { readonly fromUtc: string; readonly toUtc: string };

export type QueryWindowResult =
  | { readonly state: "valid"; readonly window: QueryWindow }
  | { readonly state: "unsupported"; readonly message: string }
  | { readonly state: "invalid"; readonly message: string };

export function resolveQueryWindow(scope: OverviewScope, now: number): QueryWindowResult {
  try {
    const window = scope.range === "custom" ? {
      fromUtc: workspaceUtcTimestamp(scope.from ?? ""), toUtc: workspaceUtcTimestamp(scope.to ?? ""),
    } : overviewWindow(scope, now);
    const from = utcMicroseconds(window.fromUtc), to = utcMicroseconds(window.toUtc), duration = to - from;
    if (!Number.isFinite(now) || duration <= 0n || to > BigInt(Math.trunc(now + 60_000)) * 1000n) return invalidQueryWindow();
    if (duration > BigInt(maximumQueryWindowMilliseconds) * 1000n) {
      return {
        state: "unsupported",
        message: "Query performance supports ranges up to 7 days. Choose a shorter range.",
      };
    }
    return { state: "valid", window };
  } catch {
    return invalidQueryWindow();
  }
}

export function queryPerformanceDefaultHref(scope: OverviewScope): string {
  return overviewHref(
    { ...scope, range: "24h", from: undefined, to: undefined },
    "queries",
    scope.target,
  );
}

function invalidQueryWindow(): { readonly state: "invalid"; readonly message: string } {
  return {
    state: "invalid",
    message: "Choose a valid UTC range for query performance, ending no later than now and no more than 7 days long.",
  };
}
