import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { agentActivityWindow, agentActivityCursor, validAgentActivitySelection, parseAgentActivityReceiptPage, parseAgentActivityDetailPage,
  type AgentActivityBinding, type AgentActivitySelection, type AgentActivityReceiptPage } from "./agentActivityHistoryModel.ts";

const maximumBytes = 768 * 1024;
async function request(binding: AgentActivityBinding, signal: AbortSignal, selection?: AgentActivitySelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 ||
    cursor !== undefined && !agentActivityCursor(cursor) || selection && (!validAgentActivitySelection(selection) || selection.sampleTargetRevision > binding.targetRevision))
    throw new Error("Choose a valid Agent activity identity and continuation.");
  const params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...agentActivityWindow(binding.fromUtc, binding.toUtc) });
  if (selection) params.set("sampleTargetRevision", String(selection.sampleTargetRevision)); else params.set("limit", "100");
  if (cursor) params.set("cursor", cursor);
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/sql-agent/activity/observations${selection ? "/" + selection.runId.toLowerCase() : ""}?${params}`,
    { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: maximumBytes });
  if (!response.ok) throw new ApiRequestError([401, 403].includes(response.status)
    ? "Agent activity access was denied. Received summaries and exact source evidence were removed. Restore target access, then explicitly retry access."
    : response.status === 409 ? "Unread Agent activity history changed or expired. Refresh this source; received pages remain evidence."
    : response.status === 422 ? "Agent activity history exceeds its read bound. Choose a narrower shared window."
    : response.status === 404 ? "This exact Agent activity observation is unavailable. Verify its run, sample revision and shared window; no replacement is selected."
    : "Agent activity history is temporarily unavailable. Retry this exact read.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Agent activity history returned an invalid content type.");
  return readJsonBounded(response, maximumBytes);
}
export async function getAgentActivityReceipts(binding: AgentActivityBinding, signal: AbortSignal, prior: readonly AgentActivityReceiptPage[] = [], cursor?: string) {
  const result = parseAgentActivityReceiptPage(await request(binding, signal, undefined, cursor), binding, prior, cursor);
  signal.throwIfAborted(); return result;
}
export async function getAgentActivityDetail(binding: AgentActivityBinding, selection: AgentActivitySelection, signal: AbortSignal) {
  const result = parseAgentActivityDetailPage(await request(binding, signal, selection), binding, selection);
  signal.throwIfAborted(); return result;
}
