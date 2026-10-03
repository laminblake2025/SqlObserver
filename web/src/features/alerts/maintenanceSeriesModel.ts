import { notificationId, notificationUtc, validNotificationReason } from "./alertSnoozeModel.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
export type SeriesFrequency = "daily" | "weekly" | "monthly";
export interface MaintenanceSeriesDefinition {
  readonly frequency: SeriesFrequency; readonly interval: number; readonly startDate: string; readonly endDate: string;
  readonly localStartTime: string; readonly durationMinutes: number; readonly timeZoneId: string; readonly weekdays: readonly number[];
  readonly dayOfMonth: number | null; readonly gapPolicy: "skip"; readonly foldPolicy: "earlier" | "later"; readonly reason: string;
}
export interface MaintenanceSeriesStatus {
  readonly targetId: string; readonly seriesId: string; readonly revision: number; readonly definition: MaintenanceSeriesDefinition;
  readonly cancelledFromDate: string | null; readonly createdAtUtc: string; readonly updatedAtUtc: string;
  readonly expansionVersion: string; readonly serverNowUtc: string; readonly occurrenceCount: number;
}
export interface MaintenanceSeriesOccurrence {
  readonly windowId: string; readonly localDate: string; readonly startsAtUtc: string; readonly endsAtUtc: string;
  readonly utcOffsetMinutes: number; readonly ambiguous: boolean;
}
export interface MaintenanceSeriesPreview {
  readonly targetId: string; readonly seriesId: string; readonly serverNowUtc: string; readonly definitionDigest: string;
  readonly expansionVersion: string; readonly occurrences: readonly MaintenanceSeriesOccurrence[];
  readonly skippedDates: readonly { readonly localDate: string; readonly reason: "nonexistent_local_time" | "missing_month_day" }[];
}
export interface MaintenanceSeriesOccurrenceStatus extends MaintenanceSeriesOccurrence {
  readonly reason: string; readonly revision: number; readonly cancelledAtUtc: string | null; readonly seriesRevision: number;
  readonly exception: boolean; readonly expansionVersion: string;
}
export interface MaintenanceSeriesPage {
  readonly targetId: string; readonly serverNowUtc: string; readonly items: readonly MaintenanceSeriesStatus[]; readonly nextBeforeSeriesId: string | null;
}
export interface MaintenanceSeriesOccurrencePage {
  readonly targetId: string; readonly seriesId: string; readonly serverNowUtc: string;
  readonly items: readonly MaintenanceSeriesOccurrenceStatus[]; readonly nextBeforeWindowId: string | null;
}
export type MaintenanceSeriesCommand =
  { readonly kind: "create"; readonly body: { readonly operationId: string; readonly seriesId: string; readonly definition: MaintenanceSeriesDefinition } }
  | { readonly kind: "edit"; readonly seriesId: string; readonly body: { readonly operationId: string; readonly expectedRevision: number; readonly effectiveFromDate: string; readonly definition: MaintenanceSeriesDefinition } }
  | { readonly kind: "cancel"; readonly seriesId: string; readonly body: { readonly operationId: string; readonly expectedRevision: number; readonly effectiveFromDate: string; readonly reason: string } };
export interface MaintenanceSeriesReceipt {
  readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string; readonly seriesId: string; readonly revision: number;
  readonly appliedAtUtc: string; readonly createdOccurrences: number; readonly updatedOccurrences: number;
  readonly cancelledOccurrences: number; readonly preservedExceptions: number;
}
const fail = (): never => { throw new Error("The recurring maintenance response could not be verified."); };
const object = (value: unknown): Record<string, unknown> => !value || typeof value !== "object" || Array.isArray(value) ? fail() : value as Record<string, unknown>;
const integer = (value: unknown, low: number, high: number): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= low && value <= high;
export function validSeriesDate(value: unknown): value is string {
  if (typeof value !== "string" || !/^(?!0000)\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])$/u.test(value)) return false;
  const date = new Date(value + "T00:00:00Z"); return Number.isFinite(date.getTime()) && date.toISOString().slice(0, 10) === value;
}
export function validSeriesZone(value: unknown): value is string {
  return typeof value === "string" && value.length <= 128 && /^[A-Za-z0-9_+/-]+$/u.test(value)
    && !value.startsWith("Etc/GMT");
}
export function parseSeriesDefinition(value: unknown): MaintenanceSeriesDefinition {
  const row = object(value);
  if (!["daily", "weekly", "monthly"].includes(String(row.frequency)) || !validSeriesDate(row.startDate) || !validSeriesDate(row.endDate)
    || Number(row.startDate.slice(0, 4)) > 9994 || row.endDate < row.startDate || !validSeriesZone(row.timeZoneId)
    || typeof row.localStartTime !== "string" || !/^(?:[01]\d|2[0-3]):[0-5]\d$/u.test(row.localStartTime)
    || !integer(row.durationMinutes, 1, 10080) || row.gapPolicy !== "skip" || !["earlier", "later"].includes(String(row.foldPolicy))
    || !validNotificationReason(row.reason) || !Array.isArray(row.weekdays) || row.weekdays.length > 7) return fail();
  const [year, month, day] = row.startDate.split("-").map(Number) as [number, number, number];
  const leapDay = month === 2 && day === 29, limit = String(year + 5).padStart(4, "0") + row.startDate.slice(4, 8) + (leapDay ? "28" : row.startDate.slice(8));
  if (row.endDate > limit || !integer(row.interval, 1, row.frequency === "daily" ? 365 : row.frequency === "weekly" ? 52 : 12)) return fail();
  const weekdays = row.weekdays as unknown[];
  if (weekdays.some(day => !integer(day, 0, 6)) || new Set(weekdays).size !== weekdays.length
    || row.frequency === "weekly" && (!weekdays.length || row.dayOfMonth !== null)
    || row.frequency !== "weekly" && weekdays.length > 0
    || (row.frequency === "monthly" ? !integer(row.dayOfMonth, 1, 31) : row.dayOfMonth !== null)) return fail();
  return { frequency: row.frequency as SeriesFrequency, interval: row.interval, startDate: row.startDate, endDate: row.endDate,
    localStartTime: row.localStartTime, durationMinutes: row.durationMinutes, timeZoneId: row.timeZoneId,
    weekdays: (weekdays as number[]).slice().sort((a, b) => a - b), dayOfMonth: row.dayOfMonth as number | null,
    gapPolicy: "skip", foldPolicy: row.foldPolicy as "earlier" | "later", reason: row.reason };
}
export async function seriesDefinitionDigest(definition: MaintenanceSeriesDefinition): Promise<string> {
  const d = parseSeriesDefinition(definition);
  const canonical = [d.frequency, d.interval, d.startDate, d.endDate, d.localStartTime, d.durationMinutes, d.timeZoneId,
    d.weekdays.join(","), d.dayOfMonth ?? "<null>", d.gapPolicy, d.foldPolicy, d.reason].join("|");
  return Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical))),
    value => value.toString(16).padStart(2, "0")).join("");
}
function bound(row: Record<string, unknown>, targetId: string, seriesId?: string) {
  if (!notificationId(row.targetId) || row.targetId.toLowerCase() !== targetId.toLowerCase()
    || seriesId && (!notificationId(row.seriesId) || row.seriesId.toLowerCase() !== seriesId.toLowerCase())) return fail();
}
const version = (value: unknown): string => value === "civil-v1" ? value : fail();
export function parseSeriesStatus(value: unknown, targetId: string, seriesId?: string): MaintenanceSeriesStatus {
  const row = object(value); bound(row, targetId, seriesId);
  if (!notificationId(row.seriesId) || !integer(row.revision, 1, Number.MAX_SAFE_INTEGER) || !integer(row.occurrenceCount, 0, 2048)
    || row.cancelledFromDate !== null && !validSeriesDate(row.cancelledFromDate)) return fail();
  return { targetId, seriesId: row.seriesId, revision: row.revision, definition: parseSeriesDefinition(row.definition),
    cancelledFromDate: row.cancelledFromDate as string | null, createdAtUtc: notificationUtc(row.createdAtUtc),
    updatedAtUtc: notificationUtc(row.updatedAtUtc), expansionVersion: version(row.expansionVersion),
    serverNowUtc: notificationUtc(row.serverNowUtc), occurrenceCount: row.occurrenceCount };
}
function occurrence(value: unknown, definition?: MaintenanceSeriesDefinition): MaintenanceSeriesOccurrence {
  const row = object(value);
  if (!notificationId(row.windowId) || !validSeriesDate(row.localDate) || !integer(row.utcOffsetMinutes, -840, 840) || typeof row.ambiguous !== "boolean") return fail();
  const startsAtUtc = notificationUtc(row.startsAtUtc), endsAtUtc = notificationUtc(row.endsAtUtc);
  const duration = utcMicroseconds(endsAtUtc) - utcMicroseconds(startsAtUtc);
  if (duration <= 0n || duration > 604800000000n || definition && (duration !== BigInt(definition.durationMinutes) * 60000000n
    || row.localDate < definition.startDate || row.localDate > definition.endDate)) return fail();
  return { windowId: row.windowId, localDate: row.localDate, startsAtUtc, endsAtUtc, utcOffsetMinutes: row.utcOffsetMinutes, ambiguous: row.ambiguous };
}
export function parseSeriesPreview(value: unknown, targetId: string, seriesId: string, definition: MaintenanceSeriesDefinition, digest: string): MaintenanceSeriesPreview {
  const row = object(value); bound(row, targetId, seriesId);
  if (row.definitionDigest !== digest || !/^[a-f0-9]{64}$/u.test(digest) || !Array.isArray(row.occurrences) || row.occurrences.length > 2048
    || !Array.isArray(row.skippedDates) || row.skippedDates.length > 2048) return fail();
  const seen = new Set<string>(), dates = new Set<string>();
  const occurrences = row.occurrences.map(value => {
    const item = occurrence(value, definition); if (seen.has(item.windowId.toLowerCase()) || dates.has(item.localDate)) return fail();
    if (utcMicroseconds(item.startsAtUtc) + BigInt(item.utcOffsetMinutes) * 60000000n
      !== utcMicroseconds(item.localDate + "T" + definition.localStartTime + ":00Z")) return fail();
    seen.add(item.windowId.toLowerCase()); dates.add(item.localDate); return item;
  });
  const skippedDates = row.skippedDates.map(value => {
    const skip = object(value);
    if (skip.reason !== "missing_month_day" && skip.reason !== "nonexistent_local_time"
      || typeof skip.localDate !== "string" || !/^(?!0000)\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])$/u.test(skip.localDate)
      || skip.reason === "nonexistent_local_time" && !validSeriesDate(skip.localDate)) return fail();
    return { localDate: skip.localDate, reason: skip.reason as "missing_month_day" | "nonexistent_local_time" };
  });
  return { targetId, seriesId, serverNowUtc: notificationUtc(row.serverNowUtc), definitionDigest: digest,
    expansionVersion: version(row.expansionVersion), occurrences, skippedDates };
}
function anchor(value: unknown, ids: readonly string[]): string | null {
  if (value === null) return null;
  if (!notificationId(value) || ids.length !== 50 || value.toLowerCase() !== ids.at(-1)?.toLowerCase()) return fail();
  return value;
}
export function parseSeriesPage(value: unknown, targetId: string, prior: readonly MaintenanceSeriesPage[] = [], before?: string): MaintenanceSeriesPage {
  const row = object(value); bound(row, targetId);
  if (!Array.isArray(row.items) || row.items.length > 50 || Boolean(before) !== Boolean(prior.length)
    || before && prior.at(-1)?.nextBeforeSeriesId !== before) return fail();
  const seen = new Set(prior.flatMap(page => page.items.map(item => item.seriesId.toLowerCase())));
  const items = row.items.map(value => {
    const item = parseSeriesStatus(value, targetId); if (seen.has(item.seriesId.toLowerCase())) return fail(); seen.add(item.seriesId.toLowerCase()); return item;
  });
  return { targetId, serverNowUtc: notificationUtc(row.serverNowUtc), items, nextBeforeSeriesId: anchor(row.nextBeforeSeriesId, items.map(item => item.seriesId)) };
}
export function parseSeriesOccurrences(value: unknown, targetId: string, seriesId: string, prior: readonly MaintenanceSeriesOccurrencePage[] = [], before?: string): MaintenanceSeriesOccurrencePage {
  const row = object(value); bound(row, targetId, seriesId);
  if (!Array.isArray(row.items) || row.items.length > 50 || Boolean(before) !== Boolean(prior.length)
    || before && prior.at(-1)?.nextBeforeWindowId !== before) return fail();
  const seen = new Set(prior.flatMap(page => page.items.map(item => item.windowId.toLowerCase())));
  const items = row.items.map(value => {
    const item = object(value), base = occurrence(item);
    if (seen.has(base.windowId.toLowerCase()) || !integer(item.revision, 1, Number.MAX_SAFE_INTEGER)
      || !integer(item.seriesRevision, 1, Number.MAX_SAFE_INTEGER) || !validNotificationReason(item.reason) || typeof item.exception !== "boolean") return fail();
    seen.add(base.windowId.toLowerCase());
    return { ...base, revision: item.revision, seriesRevision: item.seriesRevision, reason: item.reason,
      exception: item.exception, cancelledAtUtc: item.cancelledAtUtc === null ? null : notificationUtc(item.cancelledAtUtc), expansionVersion: version(item.expansionVersion) };
  });
  return { targetId, seriesId, serverNowUtc: notificationUtc(row.serverNowUtc), items, nextBeforeWindowId: anchor(row.nextBeforeWindowId, items.map(item => item.windowId)) };
}
export function seriesCommandId(command: MaintenanceSeriesCommand): string { return command.kind === "create" ? command.body.seriesId : command.seriesId; }
export function parseSeriesReceipt(value: unknown, command: MaintenanceSeriesCommand): MaintenanceSeriesReceipt {
  const row = object(value), revision = command.kind === "create" ? 1 : command.body.expectedRevision + 1;
  if (row.operationId !== command.body.operationId || row.seriesId !== seriesCommandId(command) || !notificationId(row.auditId)
    || !integer(row.revision, 1, Number.MAX_SAFE_INTEGER) || row.revision !== revision
    || ["createdOccurrences", "updatedOccurrences", "cancelledOccurrences", "preservedExceptions"].some(key => !integer(row[key], 0, 2048))) return fail();
  return { operationId: command.body.operationId, auditId: row.auditId, seriesId: seriesCommandId(command), revision,
    recordedAtUtc: notificationUtc(row.recordedAtUtc), appliedAtUtc: notificationUtc(row.appliedAtUtc),
    createdOccurrences: row.createdOccurrences as number, updatedOccurrences: row.updatedOccurrences as number,
    cancelledOccurrences: row.cancelledOccurrences as number, preservedExceptions: row.preservedExceptions as number };
}
export class MaintenanceMutationLane {
  private key = ""; private owner: object | undefined; private readonly changed: () => void;
  public constructor(changed: () => void = () => {}) { this.changed = changed; }
  public setContext(key: string) { if (key !== this.key) { this.key = key; this.owner = undefined; } }
  public claim(key: string, token: object): boolean { if (this.key !== key || this.owner) return false; this.owner = token; this.changed(); return true; }
  public release(token: object) { if (this.owner === token) { this.owner = undefined; this.changed(); } }
  public busy(key: string) { return this.key === key && Boolean(this.owner); }
}
