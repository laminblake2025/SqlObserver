import { utcMicroseconds } from "../../utcTimestamp.ts";
import { overviewHref, overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import type { OperationalPage, OperationalState } from "./operationsTypes.ts";

export type HealthCheckTab = "backups" | "agent" | "tempdb" | "availability" | "replication";
export function healthCheckTab(value: string | null): HealthCheckTab | undefined {
  return value === "backups" || value === "agent" || value === "tempdb" || value === "availability" || value === "replication" ? value : undefined;
}
export function healthCheckHref(scope: OverviewScope, target: string, tab: HealthCheckTab): string {
  return `${overviewHref(scope, "operations", target)}&check=${tab}`;
}
export interface AgentHistoryWindow { readonly fromUtc: string; readonly toUtc: string; }
// DateTimeOffset serializes PostgreSQL microseconds with a trailing seventh zero.
const clock = (value: unknown): bigint => utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/, "$1") : value);
export function validateAgentWindow(window: AgentHistoryWindow): void {
  const duration = clock(window.toUtc) - clock(window.fromUtc);
  if (duration <= 0n || duration > 7n * 86400n * 1_000_000n) throw new Error("SQL Agent history supports a UTC window of up to seven days. Choose a shorter range.");
}
export function resolveAgentWindow(scope: OverviewScope, now: number): AgentHistoryWindow {
  const window = overviewWindow(scope, now);
  validateAgentWindow(window);
  return window;
}
export interface AgentHistoryRow {
  readonly jobId: string; readonly historyInstanceId: number; readonly stepId: number;
  readonly failureKind: string; readonly durationSeconds: number;
  readonly firstObservedAtUtc: string; readonly sourceLocalStart: string | null;
  readonly countsAsJobFailure: boolean;
}
export interface AgentHistoryPage {
  readonly targetId: string; readonly targetRevision: number; readonly runId: string | null;
  readonly observedAtUtc: string; readonly state: OperationalState; readonly window: AgentHistoryWindow;
  readonly rows: readonly AgentHistoryRow[]; readonly markers: readonly { readonly timeUtc: string; readonly label: string }[];
  readonly hasMore: boolean; readonly nextCursor: string | null; readonly truncated: boolean;
}
function field(row: Record<string, unknown>, key: string): unknown {
  const upper = key[0]!.toUpperCase() + key.slice(1);
  if (Object.hasOwn(row, key) && Object.hasOwn(row, upper) && row[key] !== row[upper]) throw new Error("SQL Agent history returned ambiguous fields.");
  return row[key] ?? row[upper];
}
export function agentHistoryPage(page: OperationalPage, target: string, revision: number, window: AgentHistoryWindow): AgentHistoryPage {
  validateAgentWindow(window);
  if (page.targetId.toLowerCase() !== target.toLowerCase() || page.targetRevision !== revision || page.items.length > 50
    || clock(page.coverageFromUtc) !== clock(window.fromUtc) || clock(page.coverageToUtc) !== clock(window.toUtc))
    throw new Error("SQL Agent history does not match the selected server, revision or window. Refresh the server.");
  const identities = new Set<string>();
  const rows = page.items.map(item => {
    const jobId = String(field(item, "jobId")), historyInstanceId = Number(field(item, "historyInstanceId"));
    const stepId = Number(field(item, "stepId")), durationSeconds = Number(field(item, "durationSeconds"));
    const firstObservedAtUtc = String(field(item, "firstObservedAtUtc")), failureKind = String(field(item, "failureKind"));
    if (clock(firstObservedAtUtc) < clock(window.fromUtc) || clock(firstObservedAtUtc) >= clock(window.toUtc)
      || !/^[0-9a-f-]{36}$/i.test(jobId) || !Number.isSafeInteger(historyInstanceId) || historyInstanceId < 0
      || !Number.isSafeInteger(stepId) || stepId < 0 || !Number.isSafeInteger(durationSeconds) || durationSeconds < 0
      || !["Failed", "Retry", "Cancelled"].includes(failureKind))
      throw new Error("SQL Agent history contains invalid or out-of-window evidence.");
    const identity = `${jobId.toLowerCase()}:${historyInstanceId}`;
    if (identities.has(identity)) throw new Error("SQL Agent history repeated a history row.");
    identities.add(identity);
    return { jobId, historyInstanceId, stepId, durationSeconds, firstObservedAtUtc, failureKind,
      sourceLocalStart: field(item, "sourceLocalStart") as string | null ?? null,
      countsAsJobFailure: stepId === 0 && field(item, "runStatus") === 0 && failureKind === "Failed" };
  });
  const markers = rows.filter(row => row.countsAsJobFailure).map(row => ({ timeUtc: row.firstObservedAtUtc,
    label: `SQL Agent job failure first observed · ${row.jobId} · history ${row.historyInstanceId}` }));
  return { targetId: page.targetId, targetRevision: page.targetRevision, runId: page.runId,
    observedAtUtc: page.observedAtUtc, state: page.state, window, rows, markers,
    hasMore: page.hasMore, nextCursor: page.nextCursor, truncated: page.truncated };
}
