import { getServerWaitTrend as getTrend } from "./waitTrendParser.mjs";

export interface WaitTrendPoint {
  readonly bucketStartUtc: string;
  readonly category: "Lock" | "I/O" | "CPU/signal" | "Memory" | "Parallelism" | "Log" | "Other";
  readonly waitMilliseconds: string | null;
  readonly runCount: number;
  readonly missingSummaryRuns: number;
  readonly partialRuns: number;
  readonly incomparableTypes: number;
  readonly comparableTypes: number;
}

export interface WaitTrendResponse {
  readonly instanceId: string;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly repositoryTimeUtc: string;
  readonly points: readonly WaitTrendPoint[];
}

export async function getServerWaitTrend(instanceId: string,
  window: { readonly fromUtc: string; readonly toUtc: string },
  signal: AbortSignal): Promise<WaitTrendResponse> {
  return getTrend(instanceId, window, signal) as Promise<WaitTrendResponse>;
}
