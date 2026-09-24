import { overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";

export type ActivityWindow =
  | { readonly state: "available"; readonly window: { readonly fromUtc: string; readonly toUtc: string }; readonly liveSnapshotsAvailable: boolean }
  | { readonly state: "unavailable"; readonly message: string };

export function resolveActivityWindow(scope: OverviewScope, now: number): ActivityWindow {
  try {
    const window = overviewWindow(scope, now);
    if (Date.parse(window.toUtc) - Date.parse(window.fromUtc) > 24 * 3_600_000)
      return { state: "unavailable", message: "Activity history supports selected windows up to 24 hours. Choose a shorter range." };
    return { state: "available", window, liveSnapshotsAvailable: Date.parse(window.toUtc) > now - 24 * 3_600_000 };
  } catch {
    return { state: "unavailable", message: "Choose a valid UTC window for activity history." };
  }
}
