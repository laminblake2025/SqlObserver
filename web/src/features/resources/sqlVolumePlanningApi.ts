import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { sqlVolumePlanningWindow, sqlVolumePlanningCursor, validSqlVolumePlanningSelection, parseSqlVolumePlanningReceiptPage, parseSqlVolumePlanningSnapshotPage,
  type SqlVolumePlanningBinding, type SqlVolumePlanningSelection, type SqlVolumePlanningReceiptPage, type SqlVolumePlanningSnapshotPage } from "./sqlVolumePlanningModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: SqlVolumePlanningBinding, signal: AbortSignal, selection?: SqlVolumePlanningSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !sqlVolumePlanningCursor(cursor) || selection && (!validSqlVolumePlanningSelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid Volume planning identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...sqlVolumePlanningWindow(binding.fromUtc, binding.toUtc) });
  params.set("limit", "100");
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision));
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/resources/volume-planning/history${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "Volume planning evidence access was denied. Received receipts and diagnostics were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread Volume planning history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This Volume planning history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "The exact Volume planning receipt is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "Volume planning history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Volume planning history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getSqlVolumePlanningReceipts(binding: SqlVolumePlanningBinding, signal: AbortSignal, prior: readonly SqlVolumePlanningReceiptPage[] = [], cursor?: string) {
  const result = parseSqlVolumePlanningReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getSqlVolumePlanningSnapshot(binding: SqlVolumePlanningBinding, selection: SqlVolumePlanningSelection, signal: AbortSignal, prior: readonly SqlVolumePlanningSnapshotPage[] = [], cursor?: string) {
  const result = parseSqlVolumePlanningSnapshotPage(await request(binding, signal, selection, cursor), binding, selection, prior, cursor);
  signal.throwIfAborted(); return result;
}
