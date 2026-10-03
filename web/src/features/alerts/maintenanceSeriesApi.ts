import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "./alertRuntimeParser.ts";
import { parseMaintenanceAdminPage } from "./alertAdminListsApi.ts";
import { notificationId, notificationUtc, validNotificationReason } from "./alertSnoozeModel.ts";
import { parseSeriesStatus, parseSeriesPage, parseSeriesOccurrences, parseSeriesPreview, parseSeriesReceipt,
  parseSeriesDefinition, seriesDefinitionDigest, seriesCommandId, validSeriesDate,
  type MaintenanceSeriesDefinition, type MaintenanceSeriesCommand, type MaintenanceSeriesPage, type MaintenanceSeriesOccurrencePage } from "./maintenanceSeriesModel.ts";
const base = (targetId: string) => {
  if (!notificationId(targetId)) throw new Error("Choose a valid server for recurring maintenance.");
  return "/api/v1/observation-targets/" + targetId + "/alerts/maintenance/series";
};
const id = (value: string) => { if (!notificationId(value)) throw new Error("Choose a valid maintenance series."); return value; };
async function request(url: string, signal: AbortSignal, method = "GET", body?: unknown) {
  const response = await apiFetch(url, { signal, method, cache: "no-store", headers: { Accept: "application/json",
    ...(body !== undefined ? { "Content-Type": "application/json" } : {}) }, ...(body !== undefined ? { body: JSON.stringify(body) } : {}) },
    { maximumResponseBytes: 1024 * 1024 });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status) ? "Recurring maintenance access was denied."
    : response.status === 409 ? "This series changed. Refresh current status before a new intention."
      : response.status === 400 || response.status === 422 ? "The server refused this schedule. Review the definition and preview again."
        : "Recurring maintenance is temporarily unavailable.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("The maintenance response could not be verified.");
  return readJsonBounded(response, 1024 * 1024);
}
export async function getMaintenanceSeries(targetId: string, signal: AbortSignal, prior: readonly MaintenanceSeriesPage[] = [], before?: string) {
  if (before) id(before); const query = new URLSearchParams({ limit: "50" }); if (before) query.set("beforeSeriesId", before);
  return parseSeriesPage(await request(base(targetId) + "?" + query, signal), targetId, prior, before);
}
export async function getMaintenanceSeriesStatus(targetId: string, seriesId: string, signal: AbortSignal) {
  return parseSeriesStatus(await request(base(targetId) + "/" + id(seriesId), signal), targetId, seriesId);
}
export async function getMaintenanceSeriesOccurrences(targetId: string, seriesId: string, signal: AbortSignal, prior: readonly MaintenanceSeriesOccurrencePage[] = [], before?: string) {
  if (before) id(before); const query = new URLSearchParams({ limit: "50" }); if (before) query.set("beforeWindowId", before);
  return parseSeriesOccurrences(await request(base(targetId) + "/" + id(seriesId) + "/occurrences?" + query, signal), targetId, seriesId, prior, before);
}
export async function previewMaintenanceSeries(targetId: string, seriesId: string, definition: MaintenanceSeriesDefinition, effectiveFromDate: string | null, signal: AbortSignal) {
  const canonical = parseSeriesDefinition(definition), digest = await seriesDefinitionDigest(canonical);
  if (effectiveFromDate !== null && !validSeriesDate(effectiveFromDate)) throw new Error("Choose a valid effective civil date.");
  if (signal.aborted) throw signal.reason;
  return parseSeriesPreview(await request(base(targetId) + "/preview", signal, "POST",
    { seriesId: id(seriesId), definition: canonical, effectiveFromDate }), targetId, seriesId, canonical, digest);
}
export async function changeMaintenanceSeries(targetId: string, command: MaintenanceSeriesCommand, signal: AbortSignal) {
  if (!notificationId(command.body.operationId)) throw new Error("A maintenance operation identity is required.");
  if (command.kind !== "create" && (!Number.isSafeInteger(command.body.expectedRevision) || command.body.expectedRevision < 1 || !validSeriesDate(command.body.effectiveFromDate)))
    throw new Error("An exact series revision and effective civil date are required.");
  if (command.kind === "cancel" ? !validNotificationReason(command.body.reason) : !parseSeriesDefinition(command.body.definition)) throw new Error("A safe maintenance definition is required.");
  const endpoint = base(targetId) + (command.kind === "create" ? "" : "/" + id(seriesCommandId(command)) + (command.kind === "cancel" ? "/cancel" : ""));
  return parseSeriesReceipt(await request(endpoint, signal, command.kind === "edit" ? "PUT" : "POST", command.body), command);
}
export async function getOneOffMaintenanceForSeries(targetId: string, signal: AbortSignal, cursor?: string) {
  const query = new URLSearchParams({ limit: "100" }); if (cursor) query.set("cursor", cursor);
  try { return parseMaintenanceAdminPage(await request(base(targetId).slice(0, -7) + "?" + query, signal), targetId); }
  catch (failure) {
    if (failure instanceof ApiRequestError && ![401, 403].includes(failure.status))
      throw new ApiRequestError("Maintenance windows are temporarily unavailable.", failure.status);
    throw failure;
  }
}
export async function mutateOneOffMaintenance(targetId: string, action: "create" | "edit" | "cancel", body: Record<string, unknown>, signal: AbortSignal, windowId?: string) {
  const endpoint = base(targetId).slice(0, -7) + (windowId ? "/" + id(windowId) + (action === "cancel" ? "/cancel" : "") : "");
  const response = await apiFetch(endpoint, { method: action === "edit" ? "PUT" : "POST", signal,
    headers: { Accept: "application/json", "Content-Type": "application/json" }, body: JSON.stringify(body) }, { maximumResponseBytes: 16 * 1024 });
  if (!response.ok) throw new ApiRequestError(response.status === 403 || response.status === 401 ? "A Target Administrator role on this server is required."
    : response.status === 409 ? "This window changed. Refresh it before trying again."
      : response.status === 429 ? "Too many changes. Wait and retry the same action."
        : response.status === 503 ? "Maintenance changes are temporarily unavailable. Retry the same action." : "The maintenance change could not be saved.", response.status);
  if (signal.aborted) throw signal.reason;
  if (action === "edit") {
    try {
      if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error();
      const receipt = await readJsonBounded(response, 16 * 1024);
      if (!receipt || typeof receipt !== "object" || Array.isArray(receipt)) throw new Error();
      const value = receipt as Record<string, unknown>;
      if (!notificationId(value.auditId)) throw new Error(); notificationUtc(value.recordedAtUtc);
    } catch {
      throw new Error("The maintenance change could not be confirmed. Retry the same action or refresh the window.");
    }
  }
}
