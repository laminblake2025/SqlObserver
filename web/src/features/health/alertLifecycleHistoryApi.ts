import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { lifecycleWindow, lifecycleCursor, validLifecycleSelection, lifecycleUtc, parseAlertLifecyclePage, parseAlertLifecycleDetail, invalidAlertLifecycle,
  type AlertLifecycleBinding, type AlertLifecycleSelection, type AlertLifecyclePage } from "./alertLifecycleHistoryModel.ts";
async function request(binding: AlertLifecycleBinding, signal: AbortSignal, selection?: AlertLifecycleSelection, cursor?: string) {
  if (!notificationId(binding.targetId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 || selection && !validLifecycleSelection(selection) || cursor !== undefined && !lifecycleCursor(cursor)) throw new Error(invalidAlertLifecycle);
  const params = new URLSearchParams({targetRevision:String(binding.targetRevision),...lifecycleWindow(binding.fromUtc,binding.toUtc)});
  if (selection) { params.set("alertId",selection.alertId);params.set("episodeId",selection.episodeId??"legacy");params.set("eventAtUtc",lifecycleUtc(selection.eventAtUtc)); }
  else { params.set("limit","100");if (cursor) params.set("cursor",cursor); }
  const response = await apiFetch(`/api/v1/observation-targets/${binding.targetId}/alerts/history${selection ? "/"+selection.eventId : ""}?${params}`,{signal,cache:"no-store",headers:{Accept:"application/json"}},{maximumResponseBytes:2*1024*1024});
  if (!response.ok) throw new ApiRequestError([401,403].includes(response.status) ? "Alert history access was denied. Restore read access for this server, then explicitly retry access."
    : response.status === 409 ? "The unread alert history changed or expired. Refresh this read; received pages remain evidence."
      : response.status === 422 ? "This alert history window exceeds retained read limits. Choose a narrower shared window."
        : response.status === 404 ? "This exact server or alert event is unavailable. Verify its identity and retained window; no other cycle is selected."
          : "Alert lifecycle history is temporarily unavailable. Retry this exact read.",response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type")??"")) throw new Error(invalidAlertLifecycle);
  return readJsonBounded(response,2*1024*1024);
}
export async function getAlertLifecycleHistory(binding: AlertLifecycleBinding, signal: AbortSignal, prior: readonly AlertLifecyclePage[] = [], cursor?: string) { return parseAlertLifecyclePage(await request(binding,signal,undefined,cursor),binding,prior,cursor); }
export async function getAlertLifecycleEvent(binding: AlertLifecycleBinding, selection: AlertLifecycleSelection, signal: AbortSignal) { return parseAlertLifecycleDetail(await request(binding,signal,selection),binding,selection); }
