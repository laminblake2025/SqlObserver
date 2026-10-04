import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { tempDbWindow, tempDbCursor, validTempDbSelection, parseTempDbReceiptPage, parseTempDbSnapshotPage,
  type TempDbWindowBinding, type TempDbSelection, type TempDbReceiptPage, type TempDbSnapshotPage } from "./tempDbWindowHistoryModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: TempDbWindowBinding, signal: AbortSignal, selection?: TempDbSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !tempDbCursor(cursor) || selection && (!validTempDbSelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid TempDB identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...tempDbWindow(binding.fromUtc, binding.toUtc) });
  params.set("limit", "100");
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision));
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/tempdb/history${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "TempDB evidence access was denied. Received receipts and diagnostics were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread TempDB history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This TempDB history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "The exact TempDB receipt is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "TempDB history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("TempDB history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getTempDbReceipts(binding: TempDbWindowBinding, signal: AbortSignal, prior: readonly TempDbReceiptPage[] = [], cursor?: string) {
  const result = parseTempDbReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getTempDbSnapshot(binding: TempDbWindowBinding, selection: TempDbSelection, signal: AbortSignal, prior: readonly TempDbSnapshotPage[] = [], cursor?: string) {
  const result = parseTempDbSnapshotPage(await request(binding, signal, selection, cursor), binding, selection, prior, cursor);
  signal.throwIfAborted(); return result;
}
