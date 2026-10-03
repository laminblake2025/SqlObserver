import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import type { AlertState } from "./alertTypes.ts";

export interface AlertNotificationState {
  readonly targetId: string; readonly alertId: string; readonly episodeId: string; readonly revision: number;
  readonly state: AlertState; readonly serverNowUtc: string; readonly snoozed: boolean; readonly deliverySuppressed: boolean;
  readonly snoozedUntilUtc?: string; readonly snoozeReason?: string; readonly snoozedBy?: string; readonly snoozedAtUtc?: string;
}
export interface AlertSnoozeProjection {
  readonly episodeId?: string; readonly revision?: number; readonly snoozedUntilUtc?: string;
  readonly snoozeReason?: string; readonly snoozedBy?: string; readonly snoozedAtUtc?: string; readonly snoozed?: boolean;
}
export type NotificationAction = "snooze" | "unsnooze";
export interface AlertNotificationCommand {
  readonly operationId: string; readonly expectedEpisodeId: string; readonly expectedRevision: number;
  readonly reason: string; readonly endsAtUtc?: string;
}
export const snoozeDurations = [15, 60, 480, 1440] as const;
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/iu;
export const notificationId = (value: unknown): value is string => typeof value === "string" && guid.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
const revision = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value > 0;
const invalid = (): never => { throw new Error("The alert notification receipt could not be verified."); };
export function notificationUtc(value: unknown): string {
  if (typeof value !== "string") return invalid();
  try { return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
export function validNotificationReason(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0 && new TextEncoder().encode(value).length <= 512
    && !/\p{Cc}/u.test(value) && new TextDecoder().decode(new TextEncoder().encode(value)) === value;
}
function optionalUtc(value: unknown): string | undefined { return value === undefined || value === null ? undefined : notificationUtc(value); }
function optionalText(value: unknown, maximum: number): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string" || new TextEncoder().encode(value).length > maximum || /\p{Cc}/u.test(value)
    || new TextDecoder().decode(new TextEncoder().encode(value)) !== value) return invalid();
  return value;
}
export function parseAlertSnoozeProjection(value: Record<string, unknown>): AlertSnoozeProjection {
  const episodeId = value.episodeId === null ? undefined : value.episodeId;
  const rowRevision = value.revision === null ? undefined : value.revision;
  if (episodeId !== undefined && !notificationId(episodeId) || rowRevision !== undefined && !revision(rowRevision)
    || (episodeId === undefined) !== (rowRevision === undefined)
    || value.snoozed !== undefined && value.snoozed !== null && typeof value.snoozed !== "boolean") return invalid();
  return { episodeId, revision: rowRevision, snoozedUntilUtc: optionalUtc(value.snoozedUntilUtc),
    snoozeReason: optionalText(value.snoozeReason, 512), snoozedBy: optionalText(value.snoozedBy, 512),
    snoozedAtUtc: optionalUtc(value.snoozedAtUtc), snoozed: value.snoozed === null ? undefined : value.snoozed } as AlertSnoozeProjection;
}
export function parseAlertNotificationState(value: unknown, targetId: string, alertId: string): AlertNotificationState {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return invalid();
  const source = value as Record<string, unknown>, projected = parseAlertSnoozeProjection(source);
  if (!notificationId(source.targetId) || source.targetId.toLowerCase() !== targetId.toLowerCase()
    || !notificationId(source.alertId) || source.alertId.toLowerCase() !== alertId.toLowerCase()
    || !projected.episodeId || !projected.revision || typeof source.snoozed !== "boolean" || typeof source.deliverySuppressed !== "boolean"
    || !["normal", "pending", "firing", "acknowledged", "resolved"].includes(String(source.state))) return invalid();
  const serverNowUtc = notificationUtc(source.serverNowUtc);
  if (source.snoozed && (!projected.snoozedUntilUtc || !projected.snoozeReason || !projected.snoozedBy || !projected.snoozedAtUtc
    || !source.deliverySuppressed || utcMicroseconds(projected.snoozedUntilUtc) <= utcMicroseconds(serverNowUtc))) return invalid();
  return { ...projected, targetId: source.targetId, alertId: source.alertId, episodeId: projected.episodeId,
    revision: projected.revision, state: source.state as AlertState, serverNowUtc, snoozed: source.snoozed,
    deliverySuppressed: source.deliverySuppressed };
}
/** New intentions use repository time; uncertain retries retain the entire original command. */
export function notificationCommand(current: AlertNotificationState, action: NotificationAction, minutes: number,
  reason: string, operationId = crypto.randomUUID()): AlertNotificationCommand {
  if (!notificationId(operationId) || !validNotificationReason(reason) || action === "snooze" && !snoozeDurations.includes(minutes as typeof snoozeDurations[number]))
    throw new Error("Choose a supported duration and a reason of at most 512 UTF-8 bytes without control characters.");
  const end = utcMicroseconds(current.serverNowUtc) + BigInt(minutes) * 60_000_000n;
  const seconds = end / 1_000_000n, fraction = String(end % 1_000_000n).padStart(6, "0");
  return { operationId, expectedEpisodeId: current.episodeId, expectedRevision: current.revision, reason,
    ...(action === "snooze" ? { endsAtUtc: `${new Date(Number(seconds) * 1000).toISOString().slice(0, 19)}.${fraction}Z` } : {}) };
}
