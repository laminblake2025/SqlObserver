import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { fileHistoryWindow, fileHistoryCursor, validFileSnapshotSelection, parseFileReceiptPage, parseFileSnapshotPage,
  type FileHistoryBinding, type FileSnapshotSelection, type FileReceiptPage, type FileSnapshotPage } from "./databaseFileWindowHistoryModel.ts";
const maximumBytes = 512 * 1024;
async function request(binding: FileHistoryBinding, signal: AbortSignal, selected?: FileSnapshotSelection, cursor?: string,limit=selected?100:50) {
  if (!notificationId(binding.targetId)||!Number.isSafeInteger(binding.targetRevision)||binding.targetRevision<1||cursor !== undefined && !fileHistoryCursor(cursor) || selected && (!validFileSnapshotSelection(selected)||selected.sampleTargetRevision>binding.targetRevision)) throw new Error("Choose a valid exact file snapshot and continuation.");
  if(!Number.isSafeInteger(limit)||limit<1||limit>100)throw new Error("Choose a bounded file page size.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...fileHistoryWindow(binding.fromUtc,binding.toUtc), limit: String(limit) });
  if (selected) params.set("sampleTargetRevision",String(selected.sampleTargetRevision));
  if (cursor) params.set("cursor",cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/resources/files/history${selected ? "/" + selected.runId.toLowerCase() : ""}?${params}`,
    {signal, cache:"no-store",headers:{Accept:"application/json"}},{maximumResponseBytes:maximumBytes});
  if (!response.ok) throw new ApiRequestError([401,403].includes(response.status) ? "Database file evidence access was denied. Received receipts and file facts were removed. Restore target access, then explicitly retry access."
    :response.status===409 ? "The unread file history changed or expired. Refresh this source; received pages remain evidence."
      :response.status===422 ? "This file history exceeds its read limit. Choose a narrower shared window."
        :response.status===404 ? "The exact file receipt is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
          :"Database file history is temporarily unavailable. Retry this exact read.",response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type")??"")) throw new Error("Database file history returned an invalid content type.");
  return readJsonBounded(response,maximumBytes);
}
export async function getFileReceiptHistory(binding:FileHistoryBinding,signal:AbortSignal,prior:readonly FileReceiptPage[]=[],cursor?:string) {
  const page=parseFileReceiptPage(await request(binding,signal,undefined,cursor),binding,prior,cursor);signal.throwIfAborted();return page;
}
export async function getFileSnapshotHistory(binding:FileHistoryBinding,selected:FileSnapshotSelection,signal:AbortSignal,prior:readonly FileSnapshotPage[]=[],cursor?:string,limit=100) {
  const page=parseFileSnapshotPage(await request(binding,signal,selected,cursor,limit),binding,selected,prior,cursor,limit);signal.throwIfAborted();return page;
}
