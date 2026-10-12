import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { parseVolumeHistoryPage, volumeHistoryWindow, validVolumeHistoryIdentity, isSqlVolumeHistoryCursor, invalidVolumeHistory,
  type VolumeHistoryBinding, type VolumeHistoryReadPage } from "./sqlVolumeHistoryModel.ts";
export async function getSqlVolumeHistory(binding: VolumeHistoryBinding, signal: AbortSignal, prior: readonly VolumeHistoryReadPage[] = [], cursor?: string) {
  if (!notificationId(binding.instanceId) || !Number.isSafeInteger(binding.targetRevision) || binding.targetRevision < 1 || binding.selected && !validVolumeHistoryIdentity(binding.selected)
    || cursor !== undefined && !isSqlVolumeHistoryCursor(cursor)) throw new Error(invalidVolumeHistory);
  const window = volumeHistoryWindow(binding.fromUtc, binding.toUtc), params = new URLSearchParams({ targetRevision: String(binding.targetRevision), ...window, limit: binding.selected ? "250" : "25" });
  if (binding.selected) params.set("sampleTargetRevision", String(binding.selected.sampleTargetRevision)); if (cursor) params.set("cursor", cursor);
  const path = `/api/v1/observation-targets/${binding.instanceId}/resources/volumes/` + (binding.selected ? binding.selected.volumeKey + "/history" : "history");
  const response = await apiFetch(path + "?" + params, { signal, cache: "no-store", headers: { Accept: "application/json" } }, { maximumResponseBytes: 512 * 1024 });
  if (!response.ok) throw new ApiRequestError([401,403].includes(response.status) ? "Volume history access was denied. Restore read access to this server, then explicitly retry access."
    : [400,409].includes(response.status) && cursor ? "The unread history continuation changed or expired. Refresh this read; already-received pages remain evidence."
      : response.status === 422 ? "This history window exceeds retained read limits. Choose a narrower shared time window."
        : response.status === 404 ? "The requested server or volume history is unavailable. Verify this exact identity and choose a retained window."
          : "Volume history is temporarily unavailable. Retry this exact read or refresh its received evidence.", response.status);
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error(invalidVolumeHistory);
  return parseVolumeHistoryPage(await readJsonBounded(response, 512 * 1024), binding, prior, cursor);
}
