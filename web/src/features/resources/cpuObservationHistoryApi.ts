import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { cpuObservationWindow, cpuObservationCursor, validCpuObservationSelection, parseCpuObservationReceiptPage, parseCpuObservationDetailPage,
  type CpuObservationBinding, type CpuObservationSelection, type CpuObservationReceiptPage } from "./cpuObservationHistoryModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: CpuObservationBinding, signal: AbortSignal, selection?: CpuObservationSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !cpuObservationCursor(cursor) || selection && (!validCpuObservationSelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid CPU observation identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...cpuObservationWindow(binding.fromUtc, binding.toUtc) });
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision)); else params.set("limit", "100");
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/resources/cpu/observations${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "CPU observation evidence access was denied. Received receipts and diagnostics were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread CPU observation history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This CPU observation history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "The exact CPU observation is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "CPU observation history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("CPU observation history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getCpuObservationReceipts(binding: CpuObservationBinding, signal: AbortSignal, prior: readonly CpuObservationReceiptPage[] = [], cursor?: string) {
  const result = parseCpuObservationReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getCpuObservationDetail(binding: CpuObservationBinding, selection: CpuObservationSelection, signal: AbortSignal) {
  const result = parseCpuObservationDetailPage(await request(binding, signal, selection), binding, selection);
  signal.throwIfAborted(); return result;
}
