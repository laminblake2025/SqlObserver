import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { backupAvailabilityWindow, backupAvailabilityCursor, validBackupAvailabilitySelection,
  parseBackupAvailabilityReceipts, parseBackupAvailabilitySnapshot, type BackupAvailabilityBinding,
  type BackupAvailabilityKind, type BackupAvailabilitySelection, type BackupAvailabilityReceiptPage,
  type BackupAvailabilitySnapshotPage, type BackupAvailabilitySource } from "./backupAvailabilityWindowHistoryModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: BackupAvailabilityBinding, kind: BackupAvailabilityKind, signal: AbortSignal,
  stream?: Exclude<BackupAvailabilitySource, "list">, selection?: BackupAvailabilitySelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !backupAvailabilityCursor(cursor) || selection && (!validBackupAvailabilitySelection(selection) || selection.sampleTargetRevision > binding.targetRevision) ||
    stream && (kind === "backup" ? stream !== "backup" : stream === "backup")) throw new Error("Choose a valid earlier collection identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...backupAvailabilityWindow(binding.fromUtc, binding.toUtc), limit: "100" });
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision));
  if (cursor) params.set("cursor", cursor);
  const base = kind === "backup" ? "backups" : "availability-groups";
  const exact = selection ? `/${selection.runId.toLowerCase()}${kind === "availability" ? "/" + stream : ""}` : "";
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/${base}/history${exact}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "Earlier collection evidence access was denied. Received receipts and sibling observations were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread earlier collection evidence changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This earlier collection evidence exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "This exact earlier collection receipt is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "Earlier collection evidence is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Earlier collection evidence returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getBackupAvailabilityReceipts(binding: BackupAvailabilityBinding, kind: BackupAvailabilityKind, signal: AbortSignal,
  prior: readonly BackupAvailabilityReceiptPage[] = [], cursor?: string) {
  const result = parseBackupAvailabilityReceipts(await request(binding, kind, signal, undefined, undefined, cursor), binding, kind, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getBackupAvailabilitySnapshot(binding: BackupAvailabilityBinding, kind: BackupAvailabilityKind,
  stream: Exclude<BackupAvailabilitySource, "list">, selection: BackupAvailabilitySelection, signal: AbortSignal,
  prior: readonly BackupAvailabilitySnapshotPage[] = [], cursor?: string) {
  const result = parseBackupAvailabilitySnapshot(await request(binding, kind, signal, stream, selection, cursor), binding, kind, stream, selection, prior, cursor);
  signal.throwIfAborted(); return result;
}
