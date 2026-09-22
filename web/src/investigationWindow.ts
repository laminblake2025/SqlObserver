import { overviewWindow } from "./features/overview/overviewModel.ts";
import type { OverviewScope } from "./features/overview/overviewTypes";

export interface InvestigationWindow { readonly fromUtc: string; readonly toUtc: string; }
export function investigationWindow(scope: OverviewScope, now: number): { window?: InvestigationWindow; error?: string } {
  try { return { window: overviewWindow(scope, now) }; }
  catch (error) { return { error: error instanceof Error ? error.message : "Choose a valid UTC window." }; }
}
export function windowLimitMessage(window: InvestigationWindow, maximumDays: number, feature: string): string | undefined {
  return Date.parse(window.toUtc) - Date.parse(window.fromUtc) > maximumDays * 86_400_000
    ? `${feature} supports a maximum window of ${maximumDays === 1 ? "24 hours" : `${maximumDays} days`}. Choose a shorter investigation range.`
    : undefined;
}
