import { useMemo } from 'react';
import { useTimeFormatter } from '../../TimeDisplayContext';
import type { OverviewScope } from '../overview/overviewTypes';
import { AnalyticsSurfacePanel } from './AnalyticsSurfacePanel';
import { analyticsSurfaceCatalog } from './analyticsSurfaceCatalog';
import { analyticsWindow } from './analyticsWindow';
import type { AnalyticsSurface } from './analyticsTypes';

export function AnalyticsPage({targetId,scope,refresh,surface,onSurfaceChange}:{targetId:string;scope:OverviewScope;refresh:number;surface:AnalyticsSurface;onSurfaceChange:(surface:AnalyticsSurface)=>void}) {
  const formatTime=useTimeFormatter();
  const inventory=surface==='jobs'||surface==='backfill';
  const result=useMemo(()=>{
    try{return {window:analyticsWindow(scope,Date.now())};}
    catch(error){return {error:error instanceof Error?error.message:'Choose a valid UTC window.'};}
  },[scope.target,scope.range,scope.from,scope.to,refresh]);
  return <>
    <label className="surface-selector">Evidence surface<select aria-label="Evidence surface" value={surface} onChange={event=>onSurfaceChange(event.target.value as AnalyticsSurface)}>
      {analyticsSurfaceCatalog.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}
    </select></label>
    {inventory?<p role="status">{surface==='backfill'?'Backfill jobs only.':'All analytics jobs.'} Inventory is not filtered by the selected time range.</p>:<>
      {result.error?<p role="alert">{result.error}</p>:<p>Selected window: {formatTime(result.window!.fromUtc)} to {formatTime(result.window!.toUtc)}</p>}
    </>}
    {(inventory||result.window)&&<AnalyticsSurfacePanel key={`${targetId}:${surface}:${scope.range}:${scope.from ?? ''}:${scope.to ?? ''}`} targetId={targetId} surface={surface} timeWindow={inventory?undefined:result.window} refresh={refresh}/>}
  </>;
}
