import { overviewHref, readOverviewScope } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";

export interface DeadlockSelection { readonly eventId: string; readonly occurredAtUtc: string; }
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const normalized = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
function clock(value: unknown): bigint | undefined {
  try { return utcMicroseconds(typeof value === "string" ? normalized(value) : value); } catch { return undefined; }
}
export function deadlockOccurrenceInScope(occurredAtUtc: string, scope: OverviewScope): boolean {
  const at = clock(occurredAtUtc);
  if (at === undefined) return false;
  if (scope.range !== "custom") return ["1h", "6h", "24h", "7d"].includes(scope.range);
  const from = clock(scope.from), to = clock(scope.to);
  return from !== undefined && to !== undefined && from < to && to - from <= 31n * 86400n * 1000000n && at >= from && at < to;
}
/** Undefined means absent; null means malformed, ambiguous or contradictory selection. */
export function readDeadlockSelection(hash: string, expectedTarget?: string): DeadlockSelection | null | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const params = new URLSearchParams(query), ids = params.getAll("deadlockEvent"), times = params.getAll("deadlockAt");
  if (!ids.length && !times.length) return undefined;
  const targets = params.getAll("target"), ranges = params.getAll("range"), from = params.getAll("from"), to = params.getAll("to"),
    databases = params.getAll("databaseId"), compare = params.getAll("compare");
  if (path !== "deadlocks" || targets.length !== 1 || !guid(targets[0]) ||
      expectedTarget !== undefined && (!guid(expectedTarget) || targets[0].toLowerCase() !== expectedTarget.toLowerCase()) ||
      ids.length !== 1 || !guid(ids[0]) || times.length !== 1 || clock(times[0]) === undefined ||
      ranges.length !== 1 || !["1h", "6h", "24h", "7d", "custom"].includes(ranges[0]!) ||
      from.length > 1 || to.length > 1 || (ranges[0] === "custom" ? from.length !== 1 || to.length !== 1 : from.length !== 0 || to.length !== 0) ||
      databases.length > 1 || databases.length === 1 && (!/^[1-9]\d{0,4}$/u.test(databases[0]!) || Number(databases[0]) > 32767) ||
      compare.length > 1 || compare.length === 1 && !["0", "1"].includes(compare[0]!) ||
      ["at", "event", "timelineAt", "timelineEvent"].some(name => params.has(name))) return null;
  const selection = { eventId: ids[0]!.toLowerCase(), occurredAtUtc: canonicalUtcTimestamp(normalized(times[0]!)) };
  return deadlockOccurrenceInScope(selection.occurredAtUtc, readOverviewScope(hash)) ? selection : null;
}
export function deadlockSelectionHref(scope: OverviewScope, selection?: DeadlockSelection): string {
  if (!guid(scope.target)) throw new Error("Select an authorized server before inspecting a deadlock.");
  const href = overviewHref(scope, "deadlocks");
  if (selection === undefined) return href;
  if (!guid(selection.eventId) || !deadlockOccurrenceInScope(selection.occurredAtUtc, scope))
    throw new Error("Select a retained deadlock inside the workspace window.");
  return `${href}&deadlockEvent=${selection.eventId.toLowerCase()}&deadlockAt=${encodeURIComponent(canonicalUtcTimestamp(normalized(selection.occurredAtUtc)))}`;
}
export function retainDeadlockSelectionHref(href: string, currentHash: string): string {
  if (href.split("?")[0] !== "#/deadlocks") return href;
  const scope = readOverviewScope(href), selection = readDeadlockSelection(currentHash, scope.target);
  if (!selection) return href;
  try { return deadlockSelectionHref(scope, selection); } catch { return href; }
}
export function matchesDeadlockSelection(selection: DeadlockSelection, summary: DeadlockSelection): boolean {
  const requested = clock(selection.occurredAtUtc), received = clock(summary.occurredAtUtc);
  return guid(selection.eventId) && guid(summary.eventId) && selection.eventId.toLowerCase() === summary.eventId.toLowerCase() &&
    requested !== undefined && requested === received;
}
