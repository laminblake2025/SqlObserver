import { useEffect, useState } from "react";
import { IncidentRequestError, listSavedIncidents } from "./incidentApi.ts";
import { savedIncidentHref } from "./incidentModel.ts";
import type { SavedIncidentPage } from "./incidentModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";

export function SavedIncidentList({targetId,scope,formatTime}: {
  readonly targetId:string; readonly scope:OverviewScope; readonly formatTime:(value:string)=>string;
}) {
  const [expanded,setExpanded]=useState(false);
  const [page,setPage]=useState<SavedIncidentPage>();
  const [prior,setPrior]=useState<SavedIncidentPage>();
  const [reload,setReload]=useState(0);
  const [pending,setPending]=useState(false);
  const [error,setError]=useState<{message:string;retryable:boolean}>();
  useEffect(()=>{
    if(!expanded) return;
    const controller=new AbortController(); setPending(true); setError(undefined);
    void listSavedIncidents(targetId,controller.signal,prior)
      .then(value=>{if(!controller.signal.aborted)setPage(value);})
      .catch(reason=>{if(!controller.signal.aborted)setError({message:reason instanceof Error?reason.message:"Saved investigations are unavailable.",retryable:reason instanceof IncidentRequestError&&reason.retryable});})
      .finally(()=>{if(!controller.signal.aborted)setPending(false);});
    return()=>controller.abort();
  },[targetId,expanded,prior,reload]);
  const refresh=()=>{setPrior(undefined);setReload(value=>value+1);};
  return <section className="saved-incident-list">
    <button type="button" aria-expanded={expanded} onClick={()=>setExpanded(value=>!value)}>Saved investigations</button>
    {expanded && <div className="panel">
      <div className="section-heading"><h4>Saved investigations</h4><button type="button" disabled={pending} onClick={refresh}>Refresh investigations</button></div>
      <p>Newest saves first, across this server's saved revisions. Open an investigation to restore its window and evidence.</p>
      {pending && <p role="status">Loading saved investigations…</p>}
      {error && <p role="alert">{error.message}</p>}
      {error?.retryable && <button type="button" onClick={()=>setReload(value=>value+1)}>Retry loading investigations</button>}
      {!pending && !error && page?.items.length===0 && <p>No saved investigations were found.</p>}
      {page && !error && !pending && <ul className="incident-summary-list">{page.items.map(item=><li key={item.threadId}>
        <a href={savedIncidentHref(item,scope)}>{item.title}</a>
        <span>Saved {formatTime(item.savedAtUtc)} · Window {formatTime(item.fromUtc)} to {formatTime(item.toUtc)}</span>
        <small>{item.evidenceCount} evidence packet{item.evidenceCount===1?"":"s"} · Server revision {item.targetRevision}{item.evidenceTruncated?" · Capture truncated":""}</small>
      </li>)}</ul>}
      {page?.hasMore && !error && !pending && <button type="button" onClick={()=>setPrior(page)}>Older investigations</button>}
    </div>}
  </section>;
}
