import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { serverLifecycleWindow,serverLifecycleCursor,serverLifecycleUtc,validServerLifecycleSelection,
  parseServerLifecyclePage,parseServerLifecycleDetail,verifyServerLifecycleIdentity,verifyServerLifecycleProof,invalidServerLifecycle,
  type ServerLifecycleBinding,type ServerLifecycleSelection,type ServerLifecyclePage } from "./serverLifecycleHistoryModel.ts";
const maximumBytes=512*1024;
async function request(binding: ServerLifecycleBinding,signal: AbortSignal,selection?: ServerLifecycleSelection,cursor?: string) {
  if(!notificationId(binding.targetId)||!Number.isSafeInteger(binding.targetRevision)||binding.targetRevision<1||selection&&!validServerLifecycleSelection(selection)||cursor!==undefined&&!serverLifecycleCursor(cursor))throw new Error(invalidServerLifecycle);
  const params=new URLSearchParams({targetRevision:String(binding.targetRevision),...serverLifecycleWindow(binding.fromUtc,binding.toUtc)});
  if(selection){params.set("sampleTargetRevision",String(selection.sampleTargetRevision));params.set("afterRunId",selection.afterRunId.toLowerCase());params.set("detectedAtUtc",serverLifecycleUtc(selection.detectedAtUtc));}
  else{params.set("limit","100");if(cursor)params.set("cursor",cursor);}
  const response=await apiFetch(`/api/v1/observation-targets/${binding.targetId.toLowerCase()}/lifecycle/history${selection?"/"+selection.eventId.toLowerCase():""}?${params}`,
    {signal,cache:"no-store",headers:{Accept:"application/json"}},{maximumResponseBytes:maximumBytes});
  if(!response.ok)throw new ApiRequestError([401,403].includes(response.status)?"Server lifecycle history access was denied. Restore target read access, then explicitly retry access."
    :response.status===409?"The unread lifecycle history changed or expired. Refresh this read; received pages remain evidence."
      :response.status===422?"This server lifecycle window exceeds retained read limits. Choose a narrower shared window."
        :response.status===404?"This exact target or retained event is unavailable. Verify the identity and window; no other event is selected."
          :"Server lifecycle history is temporarily unavailable. Retry this exact read.",response.status);
  if(!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type")??""))throw new Error(invalidServerLifecycle);
  return readJsonBounded(response,maximumBytes);
}
export async function getServerLifecycleHistory(binding: ServerLifecycleBinding,signal: AbortSignal,prior: readonly ServerLifecyclePage[]=[],cursor?: string) {
  const page=parseServerLifecyclePage(await request(binding,signal,undefined,cursor),binding,prior,cursor);
  await Promise.all(page.items.map(verifyServerLifecycleIdentity));signal.throwIfAborted();return page;
}
export async function getServerLifecycleEvent(binding: ServerLifecycleBinding,selection: ServerLifecycleSelection,signal: AbortSignal) {
  const receipt=parseServerLifecycleDetail(await request(binding,signal,selection),binding,selection);
  if(receipt.event){await Promise.all([verifyServerLifecycleIdentity(receipt.event),verifyServerLifecycleProof(receipt.beforeEngine!),verifyServerLifecycleProof(receipt.afterEngine!)]);}
  signal.throwIfAborted();return receipt;
}
