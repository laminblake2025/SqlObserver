import { useMemo, useState } from 'react';
import { TimeRangeControls } from '../../components/TimeRangeControls';
import { overviewHref } from '../overview/overviewModel';
import type { OverviewScope } from '../overview/overviewTypes';
import { AnalyticsSurfacePanel } from './AnalyticsSurfacePanel';
import { analyticsSurfaceCatalog } from './analyticsSurfaceCatalog';
import { analyticsWindow } from './analyticsWindow';
import type { AnalyticsSurface } from './analyticsTypes';

export function AnalyticsPage({targetId,scope,refresh,surface,onSurfaceChange}:{targetId:string;scope:OverviewScope;refresh:number;surface:AnalyticsSurface;onSurfaceChange:(surface:AnalyticsSurface)=>void}) {
  const [windowRevision,setWindowRevision]=useState(0);
  const inventory=surface==='jobs'||surface==='backfill';
  const result=useMemo(()=>{
    try{return {window:analyticsWindow(scope,Date.now())};}
    catch(error){return {error:error instanceof Error?error.message:'Choose a valid UTC window.'};}
  },[scope.target,scope.range,scope.from,scope.to,windowRevision]);
  return <>
    <label className="surface-selector">Evidence surface<select aria-label="Evidence surface" value={surface} onChange={event=>onSurfaceChange(event.target.value as AnalyticsSurface)}>
      {analyticsSurfaceCatalog.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}
    </select></label>
    {inventory?<p role="status">{surface==='backfill'?'Backfill jobs only.':'All analytics jobs.'} Inventory is not filtered by the selected time range.</p>:<>
      <div className="overview-controls"><TimeRangeControls fixed scope={scope} maximumDays={7} onChange={change=>{location.hash=overviewHref({...scope,...change},'analytics');}}/>{scope.range!=='custom'&&<button type="button" onClick={()=>setWindowRevision(value=>value+1)}>Move window to now</button>}</div>
      {result.error?<p role="alert">{result.error}</p>:<p>Fixed investigation UTC: {result.window!.fromUtc} to {result.window!.toUtc}. Refresh reloads this window.</p>}
    </>}
    {(inventory||result.window)&&<AnalyticsSurfacePanel key={`${targetId}:${surface}:${inventory?'inventory':JSON.stringify(result.window)}`} targetId={targetId} refresh={refresh} surface={surface} timeWindow={inventory?undefined:result.window}/>}
  </>;
}
