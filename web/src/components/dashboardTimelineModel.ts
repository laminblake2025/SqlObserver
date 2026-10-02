import { utcMicroseconds } from "../utcTimestamp.ts";
import { databaseJumpHref, queryJumpHref } from "./commandPaletteModel.ts";
import { overviewHref } from "../features/overview/overviewModel.ts";
import { queryViewHref } from "../features/queries/queryViewModel.ts";
import type { OverviewScope } from "../features/overview/overviewTypes";
import type { QueryStorePlanRegressionList } from "../features/queries/queryPerformanceTypes";
import type { DatabaseOptionChangeResult, ServerConfigurationChangeResult } from "../features/health/healthTypes";

export type TimelineKind = "deadlock" | "agent_failure" | "database_options" | "configuration" | "plan_regression" | "finding";
export type TimelineTimeBasis = "occurred" | "first_observed" | "detected" | "last_observed" | "snapshot";
export interface TimelineEvent {
  readonly id: string;
  readonly kind: TimelineKind;
  readonly timeUtc: string;
  readonly title: string;
  readonly detail: string;
  readonly href: string;
  readonly timeBasis: TimelineTimeBasis;
}
const kinds = new Set<TimelineKind>(["deadlock", "agent_failure", "database_options", "configuration", "plan_regression", "finding"]);
const bases = new Set<TimelineTimeBasis>(["occurred", "first_observed", "detected", "last_observed", "snapshot"]);
function instant(value: unknown): bigint | undefined {
  try { return utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value); } catch { return undefined; }
}

/** Change sources accept a positive UTC window of at most seven days at repository precision. */
export function supportsChangeTimelineWindow(fromUtc: string, toUtc: string): boolean {
  const from = instant(fromUtc), to = instant(toUtc);
  return from !== undefined && to !== undefined && to > from && to - from <= 7n * 86400n * 1000000n;
}

/** Latest 120 loaded events, shown chronologically. An empty list says nothing about uncollected events. */
export function timelineEventsInWindow(events: readonly TimelineEvent[], fromUtc: string, toUtc: string): readonly TimelineEvent[] {
  const from = instant(fromUtc), to = instant(toUtc);
  if (from === undefined || to === undefined || from >= to) return [];
  const ids = new Set<string>();
  const valid: { event: TimelineEvent; time: bigint }[] = [];
  for (const event of events) {
    if (!event || typeof event !== "object") continue;
    const time = instant(event.timeUtc);
    if (typeof event.id !== "string" || !event.id || ids.has(event.id) || !kinds.has(event.kind) || !bases.has(event.timeBasis) ||
        typeof event.title !== "string" || !event.title || typeof event.href !== "string" ||
        !/^#\/[a-z][a-z0-9-]*(?:\?[^\u0000-\u0020\u007f]*)?$/u.test(event.href) || time === undefined || time < from || time >= to) continue;
    ids.add(event.id);
    valid.push({ event, time });
  }
  valid.sort((a, b) => a.time < b.time ? -1 : a.time > b.time ? 1 : a.event.id.localeCompare(b.event.id));
  return valid.slice(-120).map(item => item.event);
}

const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const readable = (value: unknown, maximum: number): value is string => typeof value === "string" &&
  value.trim().length > 0 && new TextEncoder().encode(value).length <= maximum && !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
function changeWindow(targetId: string, scope: OverviewScope,
    response: { readonly targetId: string; readonly fromUtc: string; readonly toUtc: string; readonly items: readonly unknown[] }):
    { readonly from: bigint; readonly to: bigint; readonly scope: OverviewScope } {
  if (!response || !guid(targetId) || typeof scope.target !== "string" || scope.target.toLowerCase() !== targetId.toLowerCase() ||
      typeof response.targetId !== "string" || response.targetId.toLowerCase() !== targetId.toLowerCase() ||
      !Array.isArray(response.items) || response.items.length > 100)
    throw new Error("Change evidence does not match this server or its loaded page bounds.");
  const from = instant(response.fromUtc), to = instant(response.toUtc);
  if (from === undefined || to === undefined || from >= to || to - from > 7n * 86400n * 1000000n ||
      scope.range === "custom" && (instant(scope.from) !== from || instant(scope.to) !== to))
    throw new Error("Change evidence does not match the selected UTC window of up to seven days.");
  return { from, to, scope: { ...scope, target: targetId, range: "custom", from: response.fromUtc, to: response.toUtc } };
}
function observationDetail(previous: unknown, current: unknown, detected: unknown, from: bigint, to: bigint): string {
  const time = instant(detected);
  if (instant(previous) === undefined || instant(current) === undefined || time === undefined || time < from || time >= to)
    throw new Error("Change evidence contains an invalid observation or detection timestamp.");
  return `Detected ${detected}. Previous observation ${previous}; current observation ${current}. The actual change time is unknown.`;
}
function latestChangeGroups(events: readonly TimelineEvent[], fromUtc: string, toUtc: string): readonly TimelineEvent[] {
  return timelineEventsInWindow(events, fromUtc, toUtc).slice(-20);
}

/** Database changes are detected between complete inventory runs, not timestamped change events. */
export function databaseOptionTimelineEvents(targetId: string, scope: OverviewScope,
    response: DatabaseOptionChangeResult): readonly TimelineEvent[] {
  const window = changeWindow(targetId, scope, response);
  if (response.semantics !== "detected_between_consecutive_complete_inventory_runs")
    throw new Error("Database option evidence has unsupported change semantics.");
  const options = new Set(["recovery_model", "user_access", "is_read_only", "compatibility_level"]);
  const groups = new Map<string, { event: TimelineEvent; changes: string[] }>();
  const identities = new Set<string>();
  for (const row of response.items) {
    if (!row || !Number.isSafeInteger(row.databaseId) || row.databaseId <= 4 || row.databaseId > 32767 ||
        !readable(row.databaseName, 512) || !options.has(row.optionKey) || !readable(row.previousValue, 128) ||
        !readable(row.currentValue, 128) || row.previousValue === row.currentValue || !guid(row.collectionRunId))
      throw new Error("Database option evidence contains an invalid change.");
    const observations = observationDetail(row.previousObservedAtUtc, row.observedAtUtc, row.detectedAtUtc, window.from, window.to);
    const id = `database-options:${row.collectionRunId.toLowerCase()}:${row.databaseId}`;
    const identity = `${id}:${row.optionKey}`;
    if (identities.has(identity)) throw new Error("Database option evidence repeated a change identity.");
    identities.add(identity);
    if (scope.databaseId !== undefined && scope.databaseId !== row.databaseId) continue;
    const group: { event: TimelineEvent; changes: string[] } = groups.get(id) ?? { event: { id, kind: "database_options", timeBasis: "detected", timeUtc: row.detectedAtUtc,
      title: `${row.databaseName} options detected`, detail: "", href: databaseJumpHref(window.scope, targetId, row.databaseId) }, changes: [] };
    if (instant(row.detectedAtUtc)! > instant(group.event.timeUtc)!) group.event = { ...group.event, timeUtc: row.detectedAtUtc };
    group.changes.push(`${row.optionKey.replaceAll("_", " ")}: ${row.previousValue} → ${row.currentValue}. ${observations}`);
    groups.set(id, group);
  }
  return latestChangeGroups([...groups.values()].map(group => ({ ...group.event, detail: group.changes.join(" ") })), response.fromUtc, response.toUtc);
}

/** Configuration evidence stays server-wide, including when a database context is selected. */
export function configurationTimelineEvents(targetId: string, scope: OverviewScope,
    response: ServerConfigurationChangeResult, targetRevision: number): readonly TimelineEvent[] {
  const window = changeWindow(targetId, scope, response);
  if (!Number.isSafeInteger(targetRevision) || targetRevision < 1 || response.targetRevision !== targetRevision ||
      !Number.isSafeInteger(response.publicationGeneration) || response.publicationGeneration < 0 ||
      response.coverage !== (response.publicationGeneration === 0 ? "never_collected" : "observed_changes_only") ||
      response.coverage === "never_collected" && (response.items.length > 0 || response.nextCursor !== null) ||
      response.nextCursor !== null && (typeof response.nextCursor !== "string" || response.nextCursor.length === 0 ||
        response.nextCursor.length > 4096 || /[\u0000-\u0020\u007f]/u.test(response.nextCursor)))
    throw new Error("Configuration evidence has invalid revision, coverage or continuation metadata.");
  const groups = new Map<string, { event: TimelineEvent; changes: string[] }>();
  const identities = new Set<string>();
  for (const row of response.items) {
    if (!row || !Number.isSafeInteger(row.configurationId) || row.configurationId < 1 || row.configurationId > 1_000_000 ||
        !readable(row.configurationName, 512) || !readable(row.sourceType, 32) || !/^[a-z][a-z0-9_]*$/u.test(row.sourceType) ||
        !guid(row.runId) || !readable(row.previousConfiguredValue, 128) || !readable(row.configuredValue, 128) ||
        !readable(row.previousInUseValue, 128) || !readable(row.inUseValue, 128) ||
        row.configuredChanged !== (row.previousConfiguredValue !== row.configuredValue) ||
        row.inUseChanged !== (row.previousInUseValue !== row.inUseValue) || !row.configuredChanged && !row.inUseChanged)
      throw new Error("Configuration evidence contains an invalid change.");
    const observations = observationDetail(row.previousObservedAtUtc, row.observedAtUtc, row.detectedAtUtc, window.from, window.to);
    const id = `configuration:${row.runId.toLowerCase()}`;
    const identity = `${id}:${row.configurationId}`;
    if (identities.has(identity)) throw new Error("Configuration evidence repeated a change identity.");
    identities.add(identity);
    const group: { event: TimelineEvent; changes: string[] } = groups.get(id) ?? { event: { id, kind: "configuration", timeBasis: "detected", timeUtc: row.detectedAtUtc,
      title: "Server configuration detected", detail: "", href: `${overviewHref(window.scope, "health", targetId)}&healthSection=summary&configurationRun=${row.runId.toLowerCase()}` }, changes: [] };
    if (instant(row.detectedAtUtc)! > instant(group.event.timeUtc)!) group.event = { ...group.event, timeUtc: row.detectedAtUtc };
    const values = [row.configuredChanged ? `configured ${row.previousConfiguredValue} → ${row.configuredValue}` : "",
      row.inUseChanged ? `in use ${row.previousInUseValue} → ${row.inUseValue}` : ""].filter(Boolean).join("; ");
    group.changes.push(`${row.configurationName}: ${values}. ${observations}`);
    groups.set(id, group);
  }
  return latestChangeGroups([...groups.values()].map(group => ({ ...group.event, detail: `Server-wide evidence. ${group.changes.join(" ")}` })), response.fromUtc, response.toUtc);
}

/** A regression comparison identifies different observed plans; its last group is not a plan-change timestamp. */
export function planRegressionTimelineEvents(targetId: string, scope: OverviewScope,
    response: QueryStorePlanRegressionList, databaseId = scope.databaseId): readonly TimelineEvent[] {
  if (!targetId || scope.target.toLowerCase() !== targetId.toLowerCase()) return [];
  const from = instant(response.fromUtc), to = instant(response.toUtc), comparisonFrom = instant(response.comparisonFromUtc);
  if (from === undefined || to === undefined || comparisonFrom === undefined || from >= to ||
      to - from > 7n * 86400n * 1000000n || comparisonFrom !== from - (to - from)) return [];
  if (scope.range === "custom" && (instant(scope.from ?? "") !== from || instant(scope.to ?? "") !== to)) return [];
  const fixedScope: OverviewScope = { ...scope, target: targetId, range: "custom", from: response.fromUtc, to: response.toUtc };
  const fingerprint = (value: string) => /^[0-9a-f]{64}$/iu.test(value);
  return response.items.filter(row => Number.isInteger(row.databaseId) && row.databaseId >= 1 && row.databaseId <= 32767 &&
    (databaseId === undefined || row.databaseId === databaseId) && fingerprint(row.queryFingerprint) &&
    fingerprint(row.previousPlanFingerprint) && fingerprint(row.currentPlanFingerprint) &&
    row.previousPlanFingerprint !== row.currentPlanFingerprint).map(row => ({
      id: `plan:${row.databaseId}:${row.queryFingerprint}:${row.currentPlanFingerprint}`,
      kind: "plan_regression" as const,
      timeUtc: row.lastObservedAtUtc,
      title: `Plan regression comparison · database ${row.databaseId} · query ${row.queryFingerprint.slice(0, 12)}…`,
      detail: `Different observed plans: duration per execution ${row.previousAverageMilliseconds} → ${row.currentAverageMilliseconds} ms (${row.slowdownRatio}×). Earlier/current comparable groups: ${row.previousComparableRows}/${row.currentComparableRows}. Correlation only; the last observed group does not establish when the plan changed.`,
      timeBasis: "last_observed" as const,
      href: queryViewHref(queryJumpHref(fixedScope, targetId, row.databaseId, row.queryFingerprint,
        row.currentPlanFingerprint, "query_store"), { view: "regressed", metric: "duration" }),
    })).filter(event => {
      const time = instant(event.timeUtc);
      return time !== undefined && time >= from && time < to;
    });
}
