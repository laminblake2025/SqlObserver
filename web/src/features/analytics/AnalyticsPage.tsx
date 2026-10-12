import { useMemo, useRef, useState } from 'react';
import { useTimeFormatter } from '../../TimeDisplayContext';
import type { OverviewScope } from '../overview/overviewTypes';
import { AnalyticsSurfacePanel } from './AnalyticsSurfacePanel';
import { analyticsSurfaceCatalog } from './analyticsSurfaceCatalog';
import { analyticsWindow } from './analyticsWindow';
import type { AnalyticsSurface } from './analyticsTypes';

export function AnalyticsPage({targetId,targetRevision,scope,refresh,surface,onSurfaceChange}:{targetId:string;targetRevision?:number;scope:OverviewScope;refresh:number;surface:AnalyticsSurface;onSurfaceChange:(surface:AnalyticsSurface)=>void}) {
  const formatTime=useTimeFormatter();
  const inventory=surface==='jobs'||surface==='backfill';
  const result=useMemo(()=>{
    try{return {window:analyticsWindow(scope,Date.now())};}
    catch(error){return {error:error instanceof Error?error.message:'Choose a valid UTC window.'};}
  },[scope.target,scope.range,scope.from,scope.to,refresh]);
  const contextKey=JSON.stringify([scope.target,scope.range,scope.from??null,scope.to??null,scope.databaseId??null,scope.compare]);
  const authorityBoundary=JSON.stringify([targetId.toLowerCase(),targetRevision??null,contextKey]);
  const latestBoundary=useRef(authorityBoundary);latestBoundary.current=authorityBoundary;
  const [denial,setDenial]=useState<{boundary:string;generation:number}>();
  const denialEpoch=denial?.boundary===authorityBoundary?denial.generation:0;
  const denyAccess=()=>{
    if(latestBoundary.current!==authorityBoundary)return;
    setDenial(previous=>({boundary:authorityBoundary,generation:previous?.boundary===authorityBoundary?previous.generation+1:1}));
  };
  return <>
    <label className="surface-selector">Evidence surface<select aria-label="Evidence surface" value={surface} onChange={event=>onSurfaceChange(event.target.value as AnalyticsSurface)}>
      {analyticsSurfaceCatalog.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}
    </select></label>
    {inventory?<p role="status">{surface==='backfill'?'Backfill jobs only.':'All analytics jobs.'} Inventory is not filtered by the selected time range.</p>:<>
      {result.error?<p role="alert">{result.error}</p>:<p>Workspace time range: {formatTime(result.window!.fromUtc)} to {formatTime(result.window!.toUtc)}</p>}
    </>}
    {analyticsSurfaceCatalog.map(option=><AnalyticsSurfacePanel key={option.value} targetId={targetId} targetRevision={targetRevision}
      surface={option.value} timeWindow={option.value==='jobs'||option.value==='backfill'?undefined:result.window}
      contextKey={contextKey} denialEpoch={denialEpoch} onAccessDenied={denyAccess}
      refresh={refresh} active={option.value===surface&&(inventory||Boolean(result.window))}/>)}
  </>;
}
