import { agentActivityTicks, agentActivityWindow } from "./agentActivityHistoryModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { healthCheckHref } from "./agentHistoryModel.ts";

export interface AgentActivitySelection { readonly runId: string; readonly sampleTargetRevision: number }
const guid = (value: string | undefined): value is string => value !== undefined &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const revision = (value: string | undefined): number | undefined => {
  if (value === undefined || !/^[1-9]\d{0,15}$/u.test(value)) return undefined;
  const number = Number(value);
  return Number.isSafeInteger(number) ? number : undefined;
};
const clock = agentActivityTicks;
function context(hash: string, expectedTarget?: string): URLSearchParams | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const params = new URLSearchParams(query);
  const targets = params.getAll("target"), checks = params.getAll("check"), ranges = params.getAll("range");
  if (path !== "operations" || checks.length !== 1 || checks[0] !== "agent" || targets.length !== 1 || !guid(targets[0]) ||
      expectedTarget !== undefined && targets[0].toLowerCase() !== expectedTarget.toLowerCase() ||
      ranges.length !== 1 || !["1h", "6h", "24h", "7d", "custom"].includes(ranges[0]!)) return undefined;
  const from = params.getAll("from"), to = params.getAll("to"), database = params.getAll("databaseId"), compare = params.getAll("compare");
  if (from.length > 1 || to.length > 1 || database.length > 1 || compare.length > 1 ||
      database.length === 1 && (!/^[1-9]\d{0,4}$/u.test(database[0]!) || Number(database[0]) > 32767) ||
      compare.length === 1 && !["0", "1"].includes(compare[0]!)) return undefined;
  try {
    if (ranges[0] === "custom") {
      if (from.length !== 1 || to.length !== 1) return undefined;
      agentActivityWindow(from[0]!, to[0]!);
      const duration = clock(to[0]!) - clock(from[0]!);
      if (duration <= 0n || duration > 31n * 86400n * 10000000n) return undefined;
    } else if (from.length || to.length) return undefined;
  } catch { return undefined; }
  return params;
}

/** Undefined is no pin; null keeps an explicit invalid pin from silently choosing a substitute. */
export function readAgentActivitySelection(hash: string, expectedTarget?: string): AgentActivitySelection | null | undefined {
  const raw = new URLSearchParams(hash.split("?")[1]);
  if (!raw.has("agentActivityRun") && !raw.has("agentActivityRevision")) return undefined;
  const params = context(hash, expectedTarget);
  if (!params) return null;
  const runs = params.getAll("agentActivityRun"), revisions = params.getAll("agentActivityRevision");
  const sampleTargetRevision = revision(revisions[0]);
  return runs.length === 1 && revisions.length === 1 && guid(runs[0]) && sampleTargetRevision !== undefined
    ? { runId: runs[0].toLowerCase(), sampleTargetRevision } : null;
}

export function agentActivitySelectionHref(scope: OverviewScope, selection?: AgentActivitySelection): string {
  const href = healthCheckHref(scope, scope.target, "agent");
  if (!context(href)) throw new Error("Choose a valid Agent activity server and window of up to 31 days.");
  if (selection === undefined) return href;
  if (!guid(selection.runId) || revision(String(selection.sampleTargetRevision)) === undefined)
    throw new Error("Choose an exact received Agent activity receipt.");
  return `${href}&agentActivityRun=${selection.runId.toLowerCase()}&agentActivityRevision=${selection.sampleTargetRevision}`;
}

/** Clear only the explicit pin; invalid or repeated window fields remain visible for correction. */
export function clearAgentActivitySelectionHref(hash: string): string {
  const [path, query] = hash.split("?");
  const params = new URLSearchParams(query);
  params.delete("agentActivityRun"); params.delete("agentActivityRevision");
  return `${path}?${params}`;
}

/** Preserve raw valid or invalid pins only when the owning server/window remains the same. */
export function retainAgentActivitySelectionHref(href: string, currentHash: string): string {
  const next = context(href), current = context(currentHash);
  if (!next || !current || next.get("target")!.toLowerCase() !== current.get("target")!.toLowerCase() ||
      next.get("range") !== current.get("range")) return href;
  if (next.get("range") === "custom") {
    try {
      if (clock(next.get("from")!) !== clock(current.get("from")!) || clock(next.get("to")!) !== clock(current.get("to")!)) return href;
    } catch { return href; }
  }
  for (const key of ["agentActivityRun", "agentActivityRevision"]) {
    next.delete(key);
    for (const value of current.getAll(key)) next.append(key, value);
  }
  return `${href.split("?")[0]}?${next}`;
}

/** A selected live receipt belongs to a fixed received window, so reload never moves its bounds. */
export function retainedAgentActivitySelectionHref(scope: OverviewScope, window: { readonly fromUtc: string; readonly toUtc: string }, selection?: AgentActivitySelection): string {
  return agentActivitySelectionHref({ ...scope, range: "custom", from: window.fromUtc, to: window.toUtc }, selection);
}
