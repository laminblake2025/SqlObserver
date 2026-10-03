import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { notificationId, notificationUtc, parseAlertNotificationState, validNotificationReason,
  type AlertNotificationCommand, type AlertNotificationState, type NotificationAction } from "./alertSnoozeModel.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export interface AlertNotificationReceipt {
  readonly operationId: string; readonly auditId: string; readonly recordedAtUtc: string;
  readonly revision: number; readonly endsAtUtc?: string; readonly appliedAtUtc: string;
}
const path = (targetId: string, alertId: string) => {
  if (!notificationId(targetId) || !notificationId(alertId)) throw new Error("Choose a valid alert before changing notifications.");
  return `/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/${encodeURIComponent(alertId)}`;
};
export async function getAlertNotificationState(targetId: string, alertId: string, signal: AbortSignal): Promise<AlertNotificationState> {
  const response = await apiFetch(`${path(targetId, alertId)}/snooze`, { credentials: "same-origin", cache: "no-store", headers: { Accept: "application/json" }, signal }, { maximumResponseBytes: 16 * 1024 });
  if (!response.ok) throw new ApiRequestError(response.status === 401 || response.status === 403
    ? "Alert notification access was denied." : "Current alert notification status is unavailable.", response.status);
  return parseAlertNotificationState(await readJsonBounded(response, 16 * 1024), targetId, alertId);
}
export function parseAlertNotificationReceipt(value: unknown, command: AlertNotificationCommand): AlertNotificationReceipt {
  const fail = (): never => { throw new Error("The alert notification mutation receipt could not be verified; completion is uncertain."); };
  if (value === null || typeof value !== "object" || Array.isArray(value)) return fail();
  const row = value as Record<string, unknown>;
  if (row.operationId !== command.operationId || !notificationId(row.auditId) || typeof row.revision !== "number"
    || !Number.isSafeInteger(row.revision) || row.revision <= command.expectedRevision) return fail();
  const endsAtUtc = row.endsAtUtc === null ? undefined : notificationUtc(row.endsAtUtc);
  if ((endsAtUtc === undefined) !== (command.endsAtUtc === undefined)
    || endsAtUtc !== undefined && utcMicroseconds(endsAtUtc) !== utcMicroseconds(command.endsAtUtc)) return fail();
  return { operationId: command.operationId, auditId: row.auditId, revision: row.revision, endsAtUtc,
    recordedAtUtc: notificationUtc(row.recordedAtUtc), appliedAtUtc: notificationUtc(row.appliedAtUtc) };
}
export async function changeAlertNotifications(targetId: string, alertId: string, action: NotificationAction,
  command: AlertNotificationCommand, signal: AbortSignal): Promise<AlertNotificationReceipt> {
  if (!notificationId(command.operationId) || !notificationId(command.expectedEpisodeId) || !Number.isSafeInteger(command.expectedRevision)
    || command.expectedRevision < 1 || !validNotificationReason(command.reason)
    || (action === "snooze") !== (command.endsAtUtc !== undefined)) throw new Error("The alert notification command is invalid.");
  if (command.endsAtUtc !== undefined) {
    // Mutation input accepts microsecond UTC only, unlike DateTimeOffset response round-trip formatting.
    utcMicroseconds(command.endsAtUtc);
  }
  const response = await apiFetch(`${path(targetId, alertId)}/${action}`, { method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" }, body: JSON.stringify(command), signal }, { maximumResponseBytes: 16 * 1024 });
  if (!response.ok) throw new ApiRequestError(response.status === 401 || response.status === 403 ? "Alert notification access was denied."
    : response.status === 409 ? "The alert episode or revision changed. Refresh current notification status before starting a new operation."
    : "Alert notification completion is uncertain. Retry the same operation or refresh current status.", response.status);
  return parseAlertNotificationReceipt(await readJsonBounded(response, 16 * 1024), command);
}
