import { overviewHref, overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";

const maximumQueryWindowMilliseconds = 7 * 24 * 60 * 60 * 1000;

export type QueryWindow = { readonly fromUtc: string; readonly toUtc: string };

export type QueryWindowResult =
  | { readonly state: "valid"; readonly window: QueryWindow }
  | { readonly state: "unsupported"; readonly message: string }
  | { readonly state: "invalid"; readonly message: string };

export function resolveQueryWindow(scope: OverviewScope, now: number): QueryWindowResult {
  const customDuration = customWindowDuration(scope, now);
  if (customDuration !== undefined && customDuration > maximumQueryWindowMilliseconds) {
    return {
      state: "unsupported",
      message: "Query performance supports ranges up to 7 days. Choose a shorter range.",
    };
  }

  try {
    const window = overviewWindow(scope, now);
    const duration = Date.parse(window.toUtc) - Date.parse(window.fromUtc);
    if (!Number.isFinite(duration) || duration <= 0) {
      return invalidQueryWindow();
    }
    if (duration > maximumQueryWindowMilliseconds) {
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

function customWindowDuration(scope: OverviewScope, now: number): number | undefined {
  if (scope.range !== "custom") return undefined;
  const from = Date.parse(scope.from ?? "");
  const to = Date.parse(scope.to ?? "");
  if (!Number.isFinite(from) || !Number.isFinite(to) || !Number.isFinite(now)) return undefined;
  if (from >= to || to > now + 60_000) return undefined;
  return to - from;
}

function invalidQueryWindow(): { readonly state: "invalid"; readonly message: string } {
  return {
    state: "invalid",
    message: "Choose a valid UTC range for query performance, ending no later than now and no more than 7 days long.",
  };
}
