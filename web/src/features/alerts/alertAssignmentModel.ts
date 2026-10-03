import type { AlertState } from "./alertTypes.ts";
import { notificationId, notificationUtc, validNotificationReason } from "./alertSnoozeModel.ts";
export interface ApprovedAssignee { readonly sid: string; readonly displayName: string; }
export interface AlertAssignmentProjection {
  readonly assigneeSid?: string; readonly assigneeName?: string; readonly assignmentReason?: string;
  readonly assignedBy?: string; readonly assignedAtUtc?: string;
}
export interface AlertAssignmentStatus extends AlertAssignmentProjection {
  readonly targetId: string; readonly alertId: string; readonly episodeId: string; readonly revision: number;
  readonly state: AlertState; readonly serverNowUtc: string; readonly eligibleAssignees: readonly ApprovedAssignee[]; readonly canAssign: boolean;
}
export interface AlertAssignmentCommand {
  readonly operationId: string; readonly expectedEpisodeId: string; readonly expectedRevision: number;
  readonly assigneeSid: string | null; readonly reason: string;
}
export interface AlertAssignmentReceipt {
  readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string; readonly revision: number;
  readonly assigneeSid: string | null; readonly appliedAtUtc: string;
}
export interface AlertAssignmentChange {
  readonly operationId: string; readonly episodeId: string; readonly revision: number; readonly assigneeSid?: string; readonly assigneeName?: string;
  readonly previousAssigneeSid?: string; readonly previousAssigneeName?: string; readonly reason: string; readonly actorSid: string; readonly appliedAtUtc: string;
}
export interface AlertAssignmentHistory {
  readonly targetId: string; readonly alertId: string; readonly items: readonly AlertAssignmentChange[]; readonly nextBeforeOperationId?: string;
}
const fail = (): never => { throw new Error("The alert assignment receipt could not be verified."); };
export const validAssigneeSid = (value: unknown): value is string => typeof value === "string" && value.length <= 184
  && !/\p{Cc}/u.test(value) && /^S-1-\d+-\d+(?:-\d+)*$/u.test(value);
export const safeAssignmentName = (value: unknown): value is string => validNotificationReason(value) && new TextEncoder().encode(value).length <= 128;
const positiveRevision = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value > 0;
function sid(value: unknown): string | undefined { if (value === undefined || value === null) return undefined; if (!validAssigneeSid(value)) return fail(); return value; }
function name(value: unknown): string | undefined { if (value === undefined || value === null) return undefined; if (!safeAssignmentName(value)) return fail(); return value; }
function utc(value: unknown): string | undefined { return value === undefined || value === null ? undefined : notificationUtc(value); }
function reason(value: unknown): string | undefined { if (value === undefined || value === null) return undefined; if (!validNotificationReason(value)) return fail(); return value; }
export function parseAssignmentProjection(row: Record<string, unknown>): AlertAssignmentProjection {
  const assigneeSid = sid(row.assigneeSid), assigneeName = name(row.assigneeName);
  if ((assigneeSid === undefined) !== (assigneeName === undefined)) return fail();
  return { assigneeSid, assigneeName, assignmentReason: reason(row.assignmentReason), assignedBy: sid(row.assignedBy), assignedAtUtc: utc(row.assignedAtUtc) };
}
function object(value: unknown): Record<string, unknown> { if (!value || typeof value !== "object" || Array.isArray(value)) return fail(); return value as Record<string, unknown>; }
function bound(row: Record<string, unknown>, targetId: string, alertId: string) {
  if (!notificationId(row.targetId) || !notificationId(row.alertId) || row.targetId.toLowerCase() !== targetId.toLowerCase() || row.alertId.toLowerCase() !== alertId.toLowerCase()) return fail();
}
export function parseAssignmentStatus(value: unknown, targetId: string, alertId: string): AlertAssignmentStatus {
  const row = object(value); bound(row, targetId, alertId);
  if (!notificationId(row.episodeId) || !positiveRevision(row.revision) || typeof row.canAssign !== "boolean"
    || !["normal", "pending", "firing", "acknowledged", "resolved"].includes(String(row.state)) || !Array.isArray(row.eligibleAssignees)
    || row.eligibleAssignees.length > 256 || !row.canAssign && row.eligibleAssignees.length > 0) return fail();
  const seen = new Set<string>(), eligibleAssignees = row.eligibleAssignees.map(value => {
    const candidate = object(value); if (!validAssigneeSid(candidate.sid) || !safeAssignmentName(candidate.displayName) || seen.has(candidate.sid)) return fail();
    seen.add(candidate.sid); return { sid: candidate.sid, displayName: candidate.displayName };
  });
  const projection = parseAssignmentProjection(row);
  if (["assigneeSid", "assigneeName", "assignmentReason", "assignedBy", "assignedAtUtc"].some(field => row[field] === undefined)
    || !projection.assigneeSid && (projection.assignmentReason || projection.assignedBy || projection.assignedAtUtc)
    || projection.assigneeSid && (!projection.assignmentReason || !projection.assignedBy || !projection.assignedAtUtc)) return fail();
  return { ...projection, targetId, alertId, episodeId: row.episodeId, revision: row.revision,
    state: row.state as AlertState, canAssign: row.canAssign, serverNowUtc: notificationUtc(row.serverNowUtc), eligibleAssignees };
}
export function assignmentCommand(current: AlertAssignmentStatus, assigneeSid: string | null, reason: string,
  operationId = crypto.randomUUID()): AlertAssignmentCommand {
  if (!notificationId(operationId) || !validNotificationReason(reason) || !current.canAssign
    || assigneeSid !== null && !current.eligibleAssignees.some(candidate => candidate.sid === assigneeSid))
    throw new Error("Choose an approved responder and a safe assignment reason of at most 512 UTF-8 bytes.");
  return { operationId, expectedEpisodeId: current.episodeId, expectedRevision: current.revision, assigneeSid, reason };
}
export function parseAssignmentReceipt(value: unknown, command: AlertAssignmentCommand): AlertAssignmentReceipt {
  const row = object(value);
  if (row.operationId !== command.operationId || !notificationId(row.auditId) || !positiveRevision(row.revision)
    || row.revision !== command.expectedRevision + 1 || row.assigneeSid !== command.assigneeSid) return fail();
  return { operationId: command.operationId, auditId: row.auditId, revision: row.revision, assigneeSid: command.assigneeSid,
    recordedAtUtc: notificationUtc(row.recordedAtUtc), appliedAtUtc: notificationUtc(row.appliedAtUtc) };
}
export function parseAssignmentHistory(value: unknown, targetId: string, alertId: string,
  prior: readonly AlertAssignmentHistory[] = [], before?: string): AlertAssignmentHistory {
  const row = object(value); bound(row, targetId, alertId);
  if (!Array.isArray(row.items) || row.items.length > 50 || Boolean(prior.length) !== Boolean(before)
    || before && prior.at(-1)?.nextBeforeOperationId !== before) return fail();
  const seen = new Set(prior.flatMap(page => page.items.map(item => item.operationId.toLowerCase())));
  const items = row.items.map(value => {
    const change = object(value);
    if (!notificationId(change.operationId) || !notificationId(change.episodeId) || !positiveRevision(change.revision)
      || !validAssigneeSid(change.actorSid) || !validNotificationReason(change.reason) || seen.has(change.operationId.toLowerCase())) return fail();
    const assigned = parseAssignmentProjection(change), previousAssigneeSid = sid(change.previousAssigneeSid), previousAssigneeName = name(change.previousAssigneeName);
    if ((previousAssigneeSid === undefined) !== (previousAssigneeName === undefined)) return fail();
    const item: AlertAssignmentChange = { operationId: change.operationId, episodeId: change.episodeId, revision: change.revision,
      assigneeSid: assigned.assigneeSid, assigneeName: assigned.assigneeName, previousAssigneeSid, previousAssigneeName,
      reason: change.reason, actorSid: change.actorSid, appliedAtUtc: notificationUtc(change.appliedAtUtc) };
    // Native order follows a private immutable sequence, not clock time or UUID order.
    seen.add(item.operationId.toLowerCase()); return item;
  });
  const next = row.nextBeforeOperationId;
  if (next !== null && (items.length !== 50 || !notificationId(next) || next.toLowerCase() !== items.at(-1)?.operationId.toLowerCase())) return fail();
  return { targetId, alertId, items, nextBeforeOperationId: next === null ? undefined : next as string };
}
