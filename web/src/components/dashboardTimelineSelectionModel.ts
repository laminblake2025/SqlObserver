import { overviewHref } from "../features/overview/overviewModel.ts";
import type { OverviewScope } from "../features/overview/overviewTypes";
import { canonicalUtcTimestamp, utcMicroseconds } from "../utcTimestamp.ts";

export interface DashboardTimelineSelection {
  readonly eventId: string;
  readonly timeUtc: string;
}
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const eventIdentity = (value: unknown, timeUtc: unknown): value is string => {
  if (typeof value !== "string" || value.length > 512) return false;
  if (value.startsWith("alert:")) {
    const parts = /^alert:([0-9a-f-]{36}):(0|-?[1-9]\d{0,17})$/iu.exec(value);
    const time = clock(timeUtc);
    return parts !== null && guid(parts[1]) && time !== undefined && BigInt(parts[2]!) === time;
  }
  return /^(?:deadlock|agent|database-options|configuration|plan|finding):[A-Za-z0-9_.:%+!'()*-]+$/u.test(value);
};
const normalizedEventId = (value: string) => value.startsWith("alert:") ? value.toLowerCase() : value;
const normalizedUtc = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
function clock(value: unknown): bigint | undefined {
  try { return utcMicroseconds(typeof value === "string" ? normalizedUtc(value) : value); }
  catch { return undefined; }
}
function inWindow(selection: DashboardTimelineSelection, scope: OverviewScope): boolean {
  const from = clock(scope.from), to = clock(scope.to), at = clock(selection.timeUtc);
  return scope.range === "custom" && from !== undefined && to !== undefined && at !== undefined &&
    from < to && to - from <= 31n * 86400n * 1000000n && at >= from && at < to;
}

/** An absent selection is undefined; a malformed, ambiguous or contradictory selection is null. */
export function readDashboardTimelineSelection(hash: string, expectedTargetId?: string): DashboardTimelineSelection | null | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const parameters = new URLSearchParams(query);
  const at = parameters.getAll("timelineAt"), event = parameters.getAll("timelineEvent");
  if (at.length === 0 && event.length === 0) return undefined;
  const targets = parameters.getAll("target"), ranges = parameters.getAll("range"),
    from = parameters.getAll("from"), to = parameters.getAll("to"), databases = parameters.getAll("databaseId"),
    compare = parameters.getAll("compare"), sections = parameters.getAll("healthSection");
  if (path !== "health" || targets.length !== 1 || !guid(targets[0]) ||
      expectedTargetId !== undefined && (!guid(expectedTargetId) || targets[0].toLowerCase() !== expectedTargetId.toLowerCase()) ||
      at.length !== 1 || event.length !== 1 || !eventIdentity(event[0], at[0]) || clock(at[0]) === undefined ||
      ranges.length !== 1 || ranges[0] !== "custom" || from.length !== 1 || to.length !== 1 ||
      parameters.has("at") || parameters.has("event") || databases.length > 1 ||
      databases.length === 1 && (!/^[1-9]\d{0,4}$/u.test(databases[0]!) || Number(databases[0]) > 32767) ||
      compare.length > 1 || compare.length === 1 && compare[0] !== "0" && compare[0] !== "1" ||
      sections.length > 1 || sections.length === 1 && sections[0] !== "summary") return null;
  const selection = { eventId: normalizedEventId(event[0]!), timeUtc: canonicalUtcTimestamp(normalizedUtc(at[0]!)) };
  return inWindow(selection, { target: targets[0], range: "custom", from: from[0], to: to[0], compare: compare[0] === "1" })
    ? selection : null;
}

/** Event alignment fixes an effective window; ordinary server navigation keeps its own Live/Rewind mode. */
export function dashboardTimelineSelectionHref(scope: OverviewScope, selection?: DashboardTimelineSelection): string {
  if (!guid(scope.target)) throw new Error("Select an authorized server before aligning its timeline.");
  const href = overviewHref(scope, "health", scope.target);
  if (selection === undefined) return href;
  if (!eventIdentity(selection.eventId, selection.timeUtc) || !inWindow(selection, scope))
    throw new Error("Select a loaded timeline event inside the fixed investigation window.");
  return `${href}&healthSection=summary&timelineAt=${encodeURIComponent(canonicalUtcTimestamp(normalizedUtc(selection.timeUtc)))}`
    + `&timelineEvent=${encodeURIComponent(normalizedEventId(selection.eventId))}`;
}

/** Identity and time must both match; a time collision alone never substitutes another event. */
export function matchesDashboardTimelineSelection(selection: DashboardTimelineSelection,
    event: { readonly id: string; readonly timeUtc: string }): boolean {
  const requested = clock(selection.timeUtc), received = clock(event.timeUtc);
  return selection.eventId === event.id && requested !== undefined && requested === received;
}
