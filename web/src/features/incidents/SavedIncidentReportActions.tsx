import { useEffect, useRef, useState } from "react";
import { IncidentRequestError } from "./incidentApi.ts";
import { createSavedInvestigationReport } from "./savedIncidentReportApi.ts";
import type { SavedInvestigationReportRun } from "./savedIncidentReportApi.ts";
import type { SavedIncident } from "./incidentModel.ts";
import { reportCsvUrl, reportHtmlUrl } from "../reports/reportsApi.ts";

export function SavedIncidentReportActions({incident,formatTime,compact=false}:{readonly incident:SavedIncident;readonly formatTime:(value:string)=>string;readonly compact?:boolean}) {
  const Heading=compact?"h3":"h5";
  const operation=useRef<string|undefined>(undefined);
  const active=useRef<AbortController|null>(null);
  const [pending,setPending]=useState(false);
  const [run,setRun]=useState<SavedInvestigationReportRun>();
  const [error,setError]=useState<{message:string;retryable:boolean}>();
  useEffect(()=>()=>active.current?.abort(),[]);
  const generate=async()=>{
    if(active.current)return;
    const controller=new AbortController();active.current=controller;
    operation.current??=crypto.randomUUID();setPending(true);setError(undefined);
    try {
      const result=await createSavedInvestigationReport(incident,operation.current,controller.signal);
      if(!controller.signal.aborted)setRun(result);
    } catch(reason) {
      if(!controller.signal.aborted)setError({message:reason instanceof Error?reason.message:"The report is unavailable.",retryable:reason instanceof IncidentRequestError&&reason.retryable});
    } finally {if(active.current===controller){active.current=null;setPending(false);}}
  };
  return <div className="saved-incident-report">
    <Heading>Report and exports</Heading>
    {!run && <button type="button" disabled={pending||error!==undefined&&!error.retryable} onClick={()=>void generate()}>{pending?"Generating report…":operation.current?"Retry report generation":"Generate investigation report"}</button>}
    {error && <p role="alert">{error.message}</p>}
    {run && <div className="toolbar" aria-live="polite">
      <span>Report ready through {formatTime(run.expiresAtUtc)}.</span>
      <a href={reportHtmlUrl(incident.targetId,run.runId)} target="_blank" rel="noreferrer">Open printable investigation</a>
      <a href={reportCsvUrl(incident.targetId,run.runId,"investigation")} download>Download saved details CSV</a>
      <a href={reportCsvUrl(incident.targetId,run.runId,"saved-evidence")} download>Download saved evidence CSV</a>
    </div>}
    <small>Reports preserve the saved notes and captured evidence. Large captures may exceed printable-view limits; use the CSV sections to export all retained content.</small>
  </div>;
}
