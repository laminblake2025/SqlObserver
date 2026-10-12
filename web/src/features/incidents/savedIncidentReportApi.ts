import { apiFetch } from "../../apiClient.ts";
import { IncidentRequestError } from "./incidentApi.ts";
import { incidentUtc, validIncidentId } from "./incidentModel.ts";
import type { SavedIncident } from "./incidentModel.ts";

export interface SavedInvestigationReportRun {
  readonly runId:string; readonly targetId:string; readonly targetRevision:number;
  readonly kind:4; readonly definitionVersion:1; readonly snapshotUtc:string; readonly expiresAtUtc:string;
  readonly parameterDigest:string; readonly state:"finalized";
}

export async function createSavedInvestigationReport(incident:SavedIncident,operationId:string,signal:AbortSignal):Promise<SavedInvestigationReportRun> {
  if(!validIncidentId(incident.targetId)||!validIncidentId(incident.threadId)||!validIncidentId(operationId))
    throw new IncidentRequestError("Select a saved investigation before generating its report.",false);
  try {
    const threadId=incident.threadId.toLowerCase();
    const digest=await crypto.subtle.digest("SHA-256",new TextEncoder().encode(JSON.stringify({reportKind:"saved-investigation",threadId})));
    const expected=Array.from(new Uint8Array(digest),value=>value.toString(16).padStart(2,"0")).join("");
    const response=await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(incident.targetId)}/reports`,{
      method:"POST",signal,cache:"no-store",headers:{"content-type":"application/json"},
      body:JSON.stringify({reportKind:"saved-investigation",operationId,threadId}),
    });
    if(!response.ok) throw new IncidentRequestError(response.status===403?"Viewer access to this server is required to generate the report."
      : response.status===404?"This saved investigation was not found."
      : response.status===409?"The report operation conflicted. Reopen the investigation to start a new report."
      : "The report outcome is unknown. Retry generation to check its outcome.",response.status>=500||response.status===429);
    const run=await response.json() as SavedInvestigationReportRun;
    if(run===null||typeof run!=="object"||!validIncidentId(run.runId)||!validIncidentId(run.targetId)
      ||run.targetId.toLowerCase()!==incident.targetId.toLowerCase()||run.targetRevision!==incident.targetRevision
      ||run.kind!==4||run.definitionVersion!==1||run.state!=="finalized"||run.parameterDigest!==expected
      ||incidentUtc(run.expiresAtUtc)-incidentUtc(run.snapshotUtc)!==24n*3600n*1000000n
      ||incidentUtc(run.snapshotUtc)<incidentUtc(incident.savedAtUtc))
      throw new Error("The report receipt does not match this investigation.");
    return run;
  } catch(error) {
    if(signal.aborted||error instanceof IncidentRequestError)throw error;
    throw new IncidentRequestError("The report outcome could not be verified. Retry generation to check its outcome.",true);
  }
}
