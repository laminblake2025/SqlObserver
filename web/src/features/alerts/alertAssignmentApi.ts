import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { notificationId, validNotificationReason } from "./alertSnoozeModel.ts";
import { parseAssignmentHistory, parseAssignmentReceipt, parseAssignmentStatus, validAssigneeSid,
  type AlertAssignmentCommand, type AlertAssignmentHistory } from "./alertAssignmentModel.ts";
const path = (targetId: string, alertId: string) => {
  if (!notificationId(targetId) || !notificationId(alertId)) throw new Error("Choose a valid alert before changing assignment.");
  return `/api/v1/observation-targets/${targetId}/alerts/${alertId}/assignment`;
};
async function request(url: string, signal: AbortSignal, command?: AlertAssignmentCommand) {
  const response = await apiFetch(url, { signal, cache: "no-store", ...(command ? { method: "POST", body: JSON.stringify(command) } : {}),
    headers: { Accept: "application/json", ...(command ? { "Content-Type": "application/json" } : {}) } }, { maximumResponseBytes: 256 * 1024 });
  if (!response.ok) throw new ApiRequestError(response.status === 401 || response.status === 403 ? "Alert assignment access was denied."
    : response.status === 409 ? "Assignment episode, revision or approved responder changed. Refresh current status before a new intention."
    : "Alert assignment is temporarily unavailable.", response.status);
  return readJsonBounded(response, 256 * 1024);
}
export async function getAlertAssignment(targetId: string, alertId: string, signal: AbortSignal) {
  return parseAssignmentStatus(await request(path(targetId, alertId), signal), targetId, alertId);
}
export async function changeAlertAssignment(targetId: string, alertId: string, command: AlertAssignmentCommand, signal: AbortSignal) {
  if (!notificationId(command.operationId) || !notificationId(command.expectedEpisodeId) || !Number.isSafeInteger(command.expectedRevision)
    || command.expectedRevision < 1 || command.assigneeSid !== null && !validAssigneeSid(command.assigneeSid) || !validNotificationReason(command.reason))
    throw new Error("The alert assignment command is invalid.");
  return parseAssignmentReceipt(await request(path(targetId, alertId), signal, command), command);
}
export async function getAlertAssignmentHistory(targetId: string, alertId: string, signal: AbortSignal,
  prior: readonly AlertAssignmentHistory[] = [], before?: string) {
  if (before !== undefined && !notificationId(before)) throw new Error("The assignment history anchor is invalid.");
  const query = new URLSearchParams({ limit: "50" }); if (before) query.set("beforeOperationId", before);
  return parseAssignmentHistory(await request(`${path(targetId, alertId)}/history?${query}`, signal), targetId, alertId, prior, before);
}
