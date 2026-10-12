import { issueHref } from "../overview/overviewModel.ts";
import type { OverviewIssue, OverviewScope } from "../overview/overviewTypes.ts";
import { timelineEventsInWindow } from "../../components/dashboardTimelineModel.ts";
import type { TimelineEvent } from "../../components/dashboardTimelineModel.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface InvestigationContext {
  readonly event: TimelineEvent;
  readonly relationship: string;
  readonly databaseId?: number;
}
export interface InvestigationFinding {
  readonly id: string;
  readonly title: string;
  readonly detail: string;
  readonly href: string;
  readonly timeUtc: string | null;
  readonly timeBasis: "snapshot" | "last_observed";
  readonly windowRelation: "in_window" | "outside_window" | "unknown";
  readonly databaseId?: number;
  readonly contexts: readonly InvestigationContext[];
}
export interface InvestigationSummary {
  readonly items: readonly InvestigationFinding[];
  readonly totalFindings: number;
  readonly omittedFindings: number;
  readonly rejectedEvidence: number;
}
export interface InvestigationSummaryInput {
  readonly targetId: string;
  readonly scope: OverviewScope;
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly issues: readonly OverviewIssue[];
  readonly events: readonly TimelineEvent[];
}

const maximumFindings = 5;
const maximumContexts = 3;
const proximity = 15n * 60n * 1000000n;
const destinations = new Set(["health", "activity", "waits", "resources", "queries", "deadlocks", "operations", "alerts", "analytics"]);
const guid = (value: unknown): value is string => typeof value === "string" &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const text = (value: unknown, maximum: number): value is string => typeof value === "string" &&
  value.trim().length > 0 && new TextEncoder().encode(value).length <= maximum &&
  !/[\u0000-\u001f\u007f-\u009f]/u.test(value);
function utc(value: unknown): bigint | undefined {
  try { return utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value); }
  catch { return undefined; }
}
const distance = (value: bigint) => value < 0n ? -value : value;

function evidenceDatabase(event: TimelineEvent, targetId: string, from: bigint, to: bigint, scope: OverviewScope): number | null | undefined {
  if (!text(event.id, 2048) || !text(event.title, 2048) || !text(event.detail, 262144)) return null;
  const match = /^#\/(health|queries)\?([^\u0000-\u0020\u007f]*)$/u.exec(event.href);
  if (!match) return null;
  const parameters = new URLSearchParams(match[2]);
  if (["target", "range", "from", "to", "databaseId", "compare"].some(key => parameters.getAll(key).length > 1) ||
      parameters.get("target")?.toLowerCase() !== targetId.toLowerCase() || parameters.get("range") !== "custom" ||
      utc(parameters.get("from")) !== from || utc(parameters.get("to")) !== to ||
      parameters.get("compare") !== (scope.compare ? "1" : null)) return null;
  const database = parameters.get("databaseId");
  if (database !== null && (!/^[1-9]\d{0,4}$/u.test(database) || Number(database) > 32767)) return null;
  if (event.kind === "configuration") {
    const run = event.id.slice("configuration:".length);
    return match[1] === "health" && event.id.startsWith("configuration:") && guid(run) &&
      parameters.getAll("configurationRun").length === 1 && parameters.get("configurationRun")?.toLowerCase() === run.toLowerCase() &&
      parameters.getAll("healthSection").length === 1 && parameters.get("healthSection") === "summary" &&
      (database === null ? scope.databaseId === undefined : Number(database) === scope.databaseId) ? undefined : null;
  }
  if (database === null) return null;
  const databaseId = Number(database);
  // IDs and links must agree; a database context in a link is not sufficient identity.
  if (event.kind === "plan_regression") {
    const parts = event.id.split(":");
    if (match[1] !== "queries" || !new RegExp(`^plan:${databaseId}:[0-9a-f]{64}:[0-9a-f]{64}$`, "iu").test(event.id) ||
        ["queryFingerprint", "planFingerprint", "source"].some(key => parameters.getAll(key).length !== 1) ||
        parameters.get("queryFingerprint")?.toLowerCase() !== parts[2]!.toLowerCase() ||
        parameters.get("planFingerprint")?.toLowerCase() !== parts[3]!.toLowerCase() || parameters.get("source") !== "query_store") return null;
  }
  if (event.kind === "database_options") {
    const parts = event.id.split(":");
    if (match[1] !== "health" || parts.length !== 3 || parts[0] !== "database-options" || !guid(parts[1]) || parts[2] !== String(databaseId)) return null;
  }
  return databaseId;
}

function relationship(delta: bigint, basis: InvestigationFinding["timeBasis"]): string {
  const anchor = basis === "snapshot" ? "this finding snapshot" : "this last observed query group";
  if (delta === 0n) return `Detected at the same timestamp as ${anchor}`;
  const seconds = distance(delta) / 1000000n;
  const duration = seconds === 0n ? "less than 1 second" : seconds >= 60n
    ? `${seconds / 60n} min${seconds % 60n ? ` ${seconds % 60n} sec` : ""}` : `${seconds} sec`;
  return `Detected ${duration} ${delta < 0n ? "before" : "after"} ${anchor}`;
}

/** Describes proximity between bounded loaded evidence, never a cause or condition-start time. */
export function buildInvestigationSummary(input: InvestigationSummaryInput): InvestigationSummary {
  const { targetId, scope, fromUtc, toUtc, issues, events } = input;
  const from = utc(fromUtc), to = utc(toUtc);
  if (!guid(targetId) || typeof scope.target !== "string" || scope.target.toLowerCase() !== targetId.toLowerCase() || from === undefined || to === undefined ||
      from >= to || to - from > 31n * 86400n * 1000000n ||
      scope.range === "custom" && (utc(scope.from) !== from || utc(scope.to) !== to) ||
      scope.databaseId !== undefined && (!Number.isInteger(scope.databaseId) || scope.databaseId < 1 || scope.databaseId > 32767) ||
      !Array.isArray(issues) || issues.length > 100 || !Array.isArray(events) || events.length > 120)
    throw new Error("Investigation evidence does not match the selected server and bounded window.");
  const fixedScope: OverviewScope = { ...scope, target: targetId, range: "custom", from: fromUtc, to: toUtc };
  let rejectedEvidence = 0;
  const identities = new Set<string>();
  type Candidate = InvestigationFinding & { readonly priority: number; readonly time?: bigint; readonly kind: "overview" | "plan" };
  const candidates: Candidate[] = [];
  for (const issue of issues) {
    if (!issue || typeof issue.targetId !== "string" || issue.targetId.toLowerCase() !== targetId.toLowerCase() ||
        !text(issue.title, 2048) || !text(issue.detail, 16384) || !destinations.has(issue.destination) ||
        !Number.isSafeInteger(issue.priority) || issue.priority < 0 || issue.priority > 1000 ||
        issue.observedAtUtc !== null && utc(issue.observedAtUtc) === undefined) { rejectedEvidence++; continue; }
    const time = issue.observedAtUtc === null ? undefined : utc(issue.observedAtUtc)!;
    const id = `overview:${issue.destination}:${issue.title}:${time ?? "unknown"}`;
    if (identities.has(id)) continue;
    identities.add(id);
    const windowRelation = time === undefined ? "unknown" : time >= from && time < to ? "in_window" : "outside_window";
    // A latest snapshot outside the history window is valid current evidence, but cannot pin historical activity.
    const at = windowRelation === "in_window" ? issue.observedAtUtc : null;
    candidates.push({ id, title: issue.title, detail: issue.detail,
      href: issueHref(fixedScope, targetId, issue.destination,
        issue.destination === "operations" && (issue.title === "Backup faults observed" || issue.title === "Backup policy checks failed")
          ? issue.observedAtUtc : at, issue.title),
      timeUtc: issue.observedAtUtc, timeBasis: "snapshot", windowRelation, contexts: [], priority: issue.priority, time, kind: "overview" });
  }
  const changes: { readonly event: TimelineEvent; readonly time: bigint; readonly databaseId?: number }[] = [];
  const validEvents = timelineEventsInWindow(events, fromUtc, toUtc);
  rejectedEvidence += events.length - validEvents.length;
  const eventIdentities = new Set<string>();
  for (const event of validEvents) {
    if (!["plan_regression", "configuration", "database_options"].includes(event.kind)) continue;
    if (event.timeBasis !== (event.kind === "plan_regression" ? "last_observed" : "detected")) { rejectedEvidence++; continue; }
    const databaseId = evidenceDatabase(event, targetId, from, to, scope);
    if (databaseId === null || databaseId !== undefined && scope.databaseId !== undefined && databaseId !== scope.databaseId) { rejectedEvidence++; continue; }
    const eventIdentity = event.id.toLowerCase();
    if (eventIdentities.has(eventIdentity)) { rejectedEvidence++; continue; }
    eventIdentities.add(eventIdentity);
    const time = utc(event.timeUtc)!;
    if (event.kind === "plan_regression") candidates.push({ id: event.id, title: event.title, detail: event.detail, href: event.href,
      timeUtc: event.timeUtc, timeBasis: "last_observed", windowRelation: "in_window", databaseId,
      contexts: [], priority: 1, time, kind: "plan" });
    else changes.push({ event, time, databaseId });
  }
  candidates.sort((a, b) => a.priority - b.priority || (a.kind === b.kind ? 0 : a.kind === "plan" ? -1 : 1) ||
    (a.time === b.time ? 0 : a.time === undefined ? 1 : b.time === undefined ? -1 : a.time < b.time ? -1 : 1) || a.id.localeCompare(b.id));
  const items = candidates.slice(0, maximumFindings).map(candidate => {
    const contexts = candidate.windowRelation !== "in_window" || candidate.time === undefined ? [] : changes
      .filter(change => distance(change.time - candidate.time!) <= proximity &&
        (change.databaseId === undefined || candidate.databaseId === undefined || change.databaseId === candidate.databaseId))
      .sort((a, b) => {
        const difference = distance(a.time - candidate.time!) - distance(b.time - candidate.time!);
        return difference < 0n ? -1 : difference > 0n ? 1 : a.event.id.localeCompare(b.event.id);
      }).slice(0, maximumContexts).map(change => ({ event: change.event, databaseId: change.databaseId,
        relationship: relationship(change.time - candidate.time!, candidate.timeBasis) }));
    const { priority: _priority, time: _time, kind: _kind, ...finding } = candidate;
    return { ...finding, contexts };
  });
  return { items, totalFindings: candidates.length, omittedFindings: Math.max(0, candidates.length - maximumFindings), rejectedEvidence };
}
