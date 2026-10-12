import { FleetWaitChart } from "./FleetWaitChart";
import { useState } from 'react';
import { MetricCard } from '../../components/DiagnosticUi';
import { DiagnosticIcon } from '../../components/WorkspaceNavigation';
import { useTimeDisplay } from '../../TimeDisplayContext';
import { formatDisplayTime } from '../../timeDisplay';
import { aggregateValue, backupAssessmentHref, displayMetric, issueHref, overviewEvidenceFooter, overviewHref, overviewResourceObservationText, rankedIssues, readOverviewScope } from './overviewModel';
import { fleetServerRows } from './fleetGridModel';
import { useBackupSummaryClock } from './useBackupSummaryClock';
import { useOverviewAnalytics } from './useOverviewAnalytics';
import { OverviewChart } from './OverviewChart';
import { FleetServerGrid } from './FleetServerGrid';
import type { OverviewResource, OverviewScope, OverviewSnapshot } from './overviewTypes';
import { ChartAlignmentControls } from '../../components/ChartAlignmentControls.tsx';
import { chartInspectionHref, readChartInspection, retainChartInspectionHref } from '../../components/chartInspectionModel.ts';
import { useChartInspection } from '../../components/useChartInspection.ts';

const labels:Record<string,string>={'host.cpu.percent':'Host CPU','host.memory.available_bytes':'Available host memory','host.volume.free_bytes':'Volume free space','host.volume.read_latency_ms':'Volume read latency','host.volume.write_latency_ms':'Volume write latency','replication.latency_seconds':'Replication latency (worst subscription)'};
export function OverviewPage({refresh,onAdd,canAddServer}: {refresh:number;onAdd:()=>void;canAddServer:boolean}) {
  const {mode}=useTimeDisplay();
  const timeLabel=(value:string)=>formatDisplayTime(value,mode);
  const selectionHash=location.hash, scope=readOverviewScope(selectionHash);
  const [workload,setWorkload]=useState('engine.batch_requests_per_second');
  const [workloadView,setWorkloadView]=useState<'server'|'database'>('database');
  const [resourceMetric,setResourceMetric]=useState('host.memory.available_bytes');
  const [contention,setContention]=useState('blocking.sessions');
  const [serverSearch,setServerSearch]=useState('');
  const [groupingRefresh,setGroupingRefresh]=useState(0);
  const result=useOverviewAnalytics(scope,refresh+groupingRefresh); const data=result.data;
  const controlsContext=JSON.stringify([scope.target,scope.range,scope.from,scope.to,scope.databaseId,scope.compare]);
  const alignmentContext=JSON.stringify([controlsContext,selectionHash]);
  const requestedChartAt=readChartInspection(selectionHash);
  const inspection=useChartInspection(alignmentContext,data?.fromUtc,data?.toUtc,requestedChartAt);
  const crosshairUtc=inspection.crosshairUtc;
  const setCrosshairUtc=(utc:string|null)=>{if(location.hash===selectionHash)inspection.setCrosshairUtc(utc);};
  const navigate=(change:Partial<typeof scope>)=>{setCrosshairUtc(null);location.hash=retainChartInspectionHref(overviewHref({...scope,...change,...(change.target !== undefined && change.target.toLowerCase() !== scope.target.toLowerCase() ? {databaseId:undefined} : {})}),location.hash);};
  const href=(page:string,target:string)=>retainChartInspectionHref(overviewHref(scope,page,target),location.hash);
  const resources=data?.evidence.flatMap(e=>e.resources)??[];
  const allSeries=data?.evidence.flatMap(e=>e.series)??[];
  const previous=result.previous?.evidence.flatMap(e=>e.series)??[];
  const databaseBreakdown=Boolean(scope.target)&&workloadView==='database';
  const workloadMetric=databaseBreakdown?'activity.user_sessions':workload;
  const databaseSeriesPartial=databaseBreakdown&&allSeries.some(s=>s.metric===workloadMetric&&s.state==='partial');
  const issues=data?rankedIssues(data,null):[];
  const chartInteraction={
    markers:issues.filter(issue=>issue.observedAtUtc).map(issue=>({timeUtc:issue.observedAtUtc!,label:`${issue.server}: ${issue.title}`})),
    crosshairUtc,
    onCrosshairChange:setCrosshairUtc,
    onSelectWindow:(window:{fromUtc:string;toUtc:string})=>navigate({range:'custom',from:window.fromUtc,to:window.toUtc}),
  };
  const current=data?.evidence.filter(e=>e.collectionState==='current').length??0;
  const renderResources=(rows:readonly OverviewResource[],destination:string)=>rows.length ? <ul className="overview-resources">{rows.map((r,i)=><li key={`${r.targetId}:${r.label}:${i}`}><div><a href={href(destination,r.targetId)}>{r.server}</a><span>{Object.entries(labels).reduce((label,[key,name])=>label.replace(key,name),r.label)}</span></div><div><strong>{displayMetric(['unsupported','unavailable','disabled','stale'].includes(r.state) ? null : r.value)} <small>{r.unit}</small></strong><small>{overviewResourceObservationText(r,data?.refreshedAtUtc??'',timeLabel)}</small></div></li>)}</ul>:<p className="overview-empty">No observations available for this selection.</p>;
  const issueRow = (issue: typeof issues[number], index: number) => <a className="overview-issue" key={`${issue.targetId}:${index}`} href={issueHref(scope,issue.targetId,issue.destination,issue.observedAtUtc,issue.title)}>
    <DiagnosticIcon name={issue.destination === 'activity' ? 'sessions' : issue.destination === 'operations' ? 'collection' : 'alert'} />
    <strong className="issue-server">{issue.server}</strong><div><strong>{issue.title}</strong><small>{issue.detail}</small></div><small>{issue.observedAtUtc ? timeLabel(issue.observedAtUtc) : 'Time unavailable'}</small><span className="secondary-button">Open evidence →</span>
  </a>;
  const gaps = data ? new Set([...data.evidence.filter(e => e.gaps.length || !['current','disabled'].includes(e.collectionState)).map(e=>e.targetId), ...data.targets.filter(t=>t.lifecycle==='active'&&!data.evidence.some(e=>e.targetId===t.targetId)).map(t=>t.targetId)]).size : 0;
  return <div className="overview-page">
    {result.loading&&<p role="status" className="overview-refresh-status">{data ? `Refreshing · displayed evidence received ${timeLabel(data.refreshedAtUtc)}` : 'Loading authorized fleet evidence…'}</p>}
    {result.error&&<p role="alert" className="status-message">{result.error}</p>}
    {data&&<>
      {data.targets.length===0?<section className="panel overview-panel"><h2>Start monitoring your SQL environment</h2><p>{canAddServer ? 'Add a server to see workload trends and diagnostic evidence.' : 'A Target Administrator with all-target access can add a server.'}</p>{canAddServer&&<button onClick={onAdd}>+ Add server</button>}</section>:<>
        <div className="kpi-grid overview-kpis">
          <MetricCard label="Servers" value={data.targets.length} detail="Loaded authorized targets" icon={<DiagnosticIcon name="fleet"/>}/>
          <MetricCard label="Need attention" value={data.evidence.some(e=>e.activeAlerts.value!==null||e.blockedSessions.value!==null)?new Set(issues.map(e=>e.targetId)).size:'—'} detail="Observed exceptions" icon={<DiagnosticIcon name="alert"/>}/>
          <MetricCard label="Blocked sessions" value={aggregateValue(data.evidence,'blockedSessions').text} detail={aggregateValue(data.evidence,'blockedSessions').note} icon={<DiagnosticIcon name="sessions"/>}/>
          <MetricCard label="Collection gaps" value={gaps} detail="Servers with reported source gaps" icon={<DiagnosticIcon name="collection"/>}/>
        </div>
        <section className="panel overview-panel overview-attention"><div className="section-heading"><h2>Investigate first</h2><small>Loaded exceptions</small></div>
          {issues.length ? <>{issues.slice(0,3).map(issueRow)}{issues.length>3&&<details className="additional-issues"><summary>View all {issues.length} loaded issues</summary>{issues.slice(3).map((issue,index)=>issueRow(issue,index+3))}</details>}</> : <p className="overview-empty">No actionable exceptions in the received evidence. Review collection gaps before concluding the environment is healthy.</p>}
        </section>
        <FleetServerGrid snapshot={data} scope={scope} search={serverSearch} onSearch={setServerSearch} formatTime={timeLabel} onGroupingUpdated={()=>setGroupingRefresh(value=>value+1)}/>
        <div className="overview-grid primary-overview-charts">
          <section className="panel overview-panel"><div className="overview-panel-heading"><div><h2>Workload</h2><small>Selected window · {databaseBreakdown?'observed sessions by database':workload.endsWith('per_second')?'batches/sec':'connections'}</small></div><div className="overview-workload-controls"><select aria-label="Workload metric" value={workloadMetric} onChange={e=>setWorkload(e.target.value)}>{databaseBreakdown?<option value="activity.user_sessions">Observed user sessions</option>:<><option value="engine.batch_requests_per_second">Batch requests/sec</option><option value="engine.user_connections">Connections</option></>}</select>{scope.target&&<select aria-label="Workload breakdown" value={workloadView} onChange={e=>setWorkloadView(e.target.value as 'server'|'database')}><option value="database">Database</option><option value="server">Server</option></select>}</div></div>
            <OverviewChart series={allSeries.filter(s=>s.metric===workloadMetric)} previous={previous.filter(s=>s.metric===workloadMetric)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/>
            {databaseSeriesPartial&&<small>Busiest database series shown; overview coverage is bounded.</small>}
          </section>
          <section className="panel overview-panel"><div className="overview-panel-heading"><div><h2>Wait activity</h2><small>Latest comparable sample deltas · not selected-window totals</small></div></div><FleetWaitChart resources={resources}/></section>
        </div>
        <details className="panel supporting-dashboard-evidence"><summary>Historical contention, resources & operations</summary><div className="overview-grid">
          <section className="panel overview-panel"><div className="overview-panel-heading"><h2>Blocking and deadlocks</h2><select aria-label="Contention metric" value={contention} onChange={e=>setContention(e.target.value)}><option value="blocking.sessions">Blocked sessions</option><option value="deadlocks">Deadlocks</option></select></div><OverviewChart series={allSeries.filter(s=>s.metric===contention)} previous={previous.filter(s=>s.metric===contention)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/><small>Observed peaks and unique deadlocks; empty buckets do not prove continuous collection.</small></section>
          <section className="panel overview-panel"><div className="overview-panel-heading"><h2>Host and storage observations</h2><select aria-label="Resource metric" value={resourceMetric} onChange={e=>setResourceMetric(e.target.value)}>{Object.entries(labels).map(([key,label])=><option key={key} value={key} disabled={key==='host.cpu.percent'}>{key==='host.cpu.percent'?'Host CPU: not qualified':label}</option>)}</select></div><OverviewChart series={allSeries.filter(s=>s.metric===resourceMetric)} previous={previous.filter(s=>s.metric===resourceMetric)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/>{renderResources(resources.filter(r=>r.label.startsWith(resourceMetric)).slice(0,5),'analytics')}</section>
        </div><section className="overview-panel"><h2>Recovery, jobs, and database resources</h2><div className="overview-grid">{['TempDB used','SQL Agent failures','Availability replicas'].map(label=><div key={label}><h3>{label}</h3>{renderResources(resources.filter(r=>r.label===label).slice(0,3),'operations')}</div>)}<OverviewBackupChecks snapshot={data} scope={scope}/></div></section></details>
        <div className="overview-secondary-controls"><label className="overview-check"><input type="checkbox" checked={scope.compare} onChange={e=>navigate({compare:e.target.checked})}/>Compare previous period</label><small>{current}/{data.evidence.length} current SQL core sources · {timeLabel(data.fromUtc)} to {timeLabel(data.toUtc)}</small></div>
        <details className="panel chart-alignment-disclosure" open={requestedChartAt!==undefined?true:undefined}><summary>Inspect charts at a specific time</summary><ChartAlignmentControls key={controlsContext} context={controlsContext} label="Fleet" fromUtc={data.fromUtc} toUtc={data.toUtc} requestedUtc={requestedChartAt} crosshairUtc={crosshairUtc} onAlign={utc=>{if(location.hash!==selectionHash)return;setCrosshairUtc(null);location.hash=chartInspectionHref(selectionHash,utc);}} onClear={()=>{if(location.hash!==selectionHash)return;setCrosshairUtc(null);location.hash=chartInspectionHref(selectionHash);}}/></details>
        <details className="panel overview-panel"><summary>Collection coverage and evidence gaps</summary>{data.evidence.map(e=><div className="overview-gap" key={e.targetId}><a href={href('health',e.targetId)}>{e.displayName}</a><span>SQL collection: {e.collectionState} · Last success: {e.lastObservedUtc?timeLabel(e.lastObservedUtc):'unavailable'}</span>{e.gaps.map((gap,i)=><small key={i}>{gap}</small>)}</div>)}<p className="muted">{overviewEvidenceFooter(data,timeLabel)}</p></details>
        {result.comparisonError&&<p role="status">{result.comparisonError}</p>}
      </>}
    </>}
  </div>;
}

function OverviewBackupChecks({ snapshot, scope }: { readonly snapshot: OverviewSnapshot; readonly scope: OverviewScope }) {
  const clock = useBackupSummaryClock(snapshot);
  const rows = fleetServerRows(snapshot, '', clock).slice(0, 3);
  return <div><h3>Current backup policy checks</h3><ul className="overview-resources">{rows.map(row => <li key={row.target.targetId}>
    <div><a href={backupAssessmentHref(scope, row.target.targetId, row.evidence?.backupSummary?.assessedAtUtc ?? null)}>{row.target.displayName}</a>
      <span title={row.signals.backups.detail}>{row.signals.backups.text}</span></div>
    <small>{row.evidence?.backupSummary ? `Full: ${row.evidence.backupSummary.full.policyViolated} failed, ${row.evidence.backupSummary.full.indeterminate} unknown · Log: ${row.evidence.backupSummary.log.policyViolated} failed, ${row.evidence.backupSummary.log.indeterminate} unknown` : 'Open Health checks for evidence and policy settings'}</small>
  </li>)}</ul><small>Current assessments, independent of the selected history window.</small></div>;
}
