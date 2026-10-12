import { utcMicroseconds } from "../../utcTimestamp.ts";
import { overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";

export type ActivityWindow =
  | { readonly state: "available"; readonly window: { readonly fromUtc: string; readonly toUtc: string }; readonly liveSnapshotsAvailable: boolean }
  | { readonly state: "unavailable"; readonly message: string };

export function resolveActivityWindow(scope: OverviewScope, now: number): ActivityWindow {
  try {
    const window = overviewWindow({ ...scope,
      from: scope.from?.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"),
      to: scope.to?.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") }, now);
    if (utcMicroseconds(window.toUtc) - utcMicroseconds(window.fromUtc) > 86_400_000_000n)
      return { state: "unavailable", message: "Activity history supports selected windows up to 24 hours. Choose a shorter range." };
    return { state: "available", window, liveSnapshotsAvailable: utcMicroseconds(window.toUtc) > BigInt(now - 24 * 3_600_000) * 1000n };
  } catch {
    return { state: "unavailable", message: "Choose a valid UTC window for activity history." };
  }
}
