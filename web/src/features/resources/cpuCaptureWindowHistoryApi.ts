import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { cpuCaptureWindow, cpuCaptureCursor, validCpuCaptureSelection, parseCpuCaptureReceiptPage, parseCpuCaptureDetailPage,
  type CpuCaptureBinding, type CpuCaptureSelection, type CpuCaptureReceiptPage } from "./cpuCaptureWindowHistoryModel.ts";

const maximumBytes = 512 * 1024;
async function request(binding: CpuCaptureBinding, signal: AbortSignal, selection?: CpuCaptureSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !cpuCaptureCursor(cursor) || selection && (!validCpuCaptureSelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid CPU capture identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...cpuCaptureWindow(binding.fromUtc, binding.toUtc) });
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision)); else params.set("limit", "100");
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/resources/cpu/captures${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "CPU capture evidence access was denied. Received receipts and diagnostics were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "The unread CPU capture history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "This CPU capture history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "The exact CPU capture is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "CPU capture history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("CPU capture history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getCpuCaptureReceipts(binding: CpuCaptureBinding, signal: AbortSignal, prior: readonly CpuCaptureReceiptPage[] = [], cursor?: string) {
  const result = parseCpuCaptureReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getCpuCaptureDetail(binding: CpuCaptureBinding, selection: CpuCaptureSelection, signal: AbortSignal) {
  const result = parseCpuCaptureDetailPage(await request(binding, signal, selection), binding, selection);
  signal.throwIfAborted(); return result;
}
