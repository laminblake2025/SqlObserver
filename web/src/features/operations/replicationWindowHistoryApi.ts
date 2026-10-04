import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { replicationWindow, replicationCursor, validReplicationSelection, parseReplicationReceiptPage, parseReplicationSnapshotPage,
  type ReplicationWindowBinding, type ReplicationSelection, type ReplicationReceiptPage, type ReplicationSnapshotPage } from "./replicationWindowHistoryModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: ReplicationWindowBinding, signal: AbortSignal, selection?: ReplicationSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !replicationCursor(cursor) || selection && (!validReplicationSelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid replication identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...replicationWindow(binding.fromUtc, binding.toUtc) });
  params.set("limit", "100");
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision));
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/replication/history${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "Replication evidence access was denied. Received receipts and observations were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread replication history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This replication history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "The exact replication receipt is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "Replication history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Replication history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getReplicationReceipts(binding: ReplicationWindowBinding, signal: AbortSignal, prior: readonly ReplicationReceiptPage[] = [], cursor?: string) {
  const result = parseReplicationReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getReplicationSnapshot(binding: ReplicationWindowBinding, selection: ReplicationSelection, signal: AbortSignal, prior: readonly ReplicationSnapshotPage[] = [], cursor?: string) {
  const result = parseReplicationSnapshotPage(await request(binding, signal, selection, cursor), binding, selection, prior, cursor);
  signal.throwIfAborted(); return result;
}
