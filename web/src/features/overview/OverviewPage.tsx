import { useState } from 'react';
import { useTimeDisplay } from '../../TimeDisplayContext';
import { formatDisplayTime } from '../../timeDisplay';
import { aggregateValue, displayMetric, issueHref, overviewEvidenceFooter, overviewHref, overviewResourceObservationText, rankedIssues, readOverviewScope } from './overviewModel';
import { useOverviewAnalytics } from './useOverviewAnalytics';
import { OverviewChart } from './OverviewChart';
import { FleetServerGrid } from './FleetServerGrid';
import type { OverviewResource } from './overviewTypes';

const labels:Record<string,string>={'host.cpu.percent':'Host CPU','host.memory.available_bytes':'Available host memory','host.volume.free_bytes':'Volume free space','host.volume.read_latency_ms':'Volume read latency','host.volume.write_latency_ms':'Volume write latency','replication.latency_seconds':'Replication latency (worst subscription)'};
export function OverviewPage({refresh,onAdd,canAddServer}: {refresh:number;onAdd:()=>void;canAddServer:boolean}) {
  const {mode}=useTimeDisplay();
  const timeLabel=(value:string)=>formatDisplayTime(value,mode);
  const scope=readOverviewScope(location.hash);
  const [crosshairUtc,setCrosshairUtc]=useState<string|null>(null);
  const [workload,setWorkload]=useState('engine.batch_requests_per_second');
  const [workloadView,setWorkloadView]=useState<'server'|'database'>('database');
  const [resourceMetric,setResourceMetric]=useState('host.cpu.percent');
  const [contention,setContention]=useState('blocking.sessions');
  const [serverSearch,setServerSearch]=useState('');
  const result=useOverviewAnalytics(scope,refresh); const data=result.data;
  // Keep the authorized selector choices while evidence reloads; no old evidence is shown under a new selection.
  const [choices,setChoices]=useState(data?.targets??[]);
  if (data && choices!==data.targets) setChoices(data.targets);
  const navigate=(change:Partial<typeof scope>)=>{setCrosshairUtc(null);location.hash=overviewHref({...scope,...change});};
  const href=(page:string,target:string)=>overviewHref(scope,page,target);
  const resources=data?.evidence.flatMap(e=>e.resources)??[];
  const allSeries=data?.evidence.flatMap(e=>e.series)??[];
  const previous=result.previous?.evidence.flatMap(e=>e.series)??[];
  const databaseBreakdown=Boolean(scope.target)&&workloadView==='database';
  const workloadMetric=databaseBreakdown?'activity.user_sessions':workload;
  const databaseSeriesPartial=databaseBreakdown&&allSeries.some(s=>s.metric===workloadMetric&&s.state==='partial');
  const issues=data?rankedIssues(data):[];
  const chartInteraction={
    markers:issues.filter(issue=>issue.observedAtUtc).map(issue=>({timeUtc:issue.observedAtUtc!,label:`${issue.server}: ${issue.title}`})),
    crosshairUtc,
    onCrosshairChange:setCrosshairUtc,
    onSelectWindow:(window:{fromUtc:string;toUtc:string})=>navigate({range:'custom',from:window.fromUtc,to:window.toUtc}),
  };
  const current=data?.evidence.filter(e=>e.collectionState==='current').length??0;
  const renderResources=(rows:readonly OverviewResource[],destination:string)=>rows.length ? <ul className="overview-resources">{rows.map((r,i)=><li key={`${r.targetId}:${r.label}:${i}`}><div><a href={href(destination,r.targetId)}>{r.server}</a><span>{Object.entries(labels).reduce((label,[key,name])=>label.replace(key,name),r.label)}</span></div><div><strong>{displayMetric(r.value)} <small>{r.unit}</small></strong><small>{overviewResourceObservationText(r,data?.refreshedAtUtc??'',timeLabel)}</small></div></li>)}</ul>:<p className="overview-empty">No observations available for this selection.</p>;
  return <div className="overview-page">
    <div className="overview-controls"><label>Server<select aria-label="Overview server" value={scope.target} onChange={e=>navigate({target:e.target.value})}><option value="">All servers</option>{scope.target&&!choices.some(t=>t.targetId===scope.target)&&<option value={scope.target}>Selected server</option>}{choices.map(t=><option key={t.targetId} value={t.targetId}>{t.displayName}{t.lifecycle!=='active'?` · ${t.lifecycle}`:''}</option>)}</select></label>
      <label className="overview-check"><input type="checkbox" checked={scope.compare} onChange={e=>navigate({compare:e.target.checked})}/>Compare previous period</label>
      <span className="server-dashboard-mode">Drag a chart to select a shared time window.</span>
    </div>
    {result.loading&&<p role="status" className="overview-coverage">Loading analytics for {scope.target?'the selected server':'all authorized servers'}…</p>}
    {result.error&&<p role="alert" className="status-message">{result.error}</p>}
    {data&&<><div className="overview-coverage" role="status"><span className={current===data.evidence.length?'coverage-dot':'coverage-dot partial'}/><strong>{current}/{data.evidence.length} servers reporting current SQL core evidence</strong><span>{data.excludedTargets} disabled/retired excluded from All servers</span><small>Refreshed {timeLabel(data.refreshedAtUtc)} · SQL core freshness does not imply coverage for other sources</small></div>
      {data.targets.length===0?<section className="panel overview-panel"><h2>Start monitoring your SQL environment</h2><p>{canAddServer ? "Add a server to see workload trends, contention, and operational evidence." : "A Target Administrator with all-target access can add a server."}</p>{canAddServer ? <button onClick={onAdd}>+ Add server</button> : null}</section>:<>
      <div className="kpi-grid overview-kpis"><section className="kpi"><p>Instances needing attention</p><strong>{data.evidence.some(e=>e.activeAlerts.value!==null||e.blockedSessions.value!==null)?new Set(data.evidence.filter(e=>e.issues.length).map(e=>e.targetId)).size:'—'}</strong><small>Observed exceptions · monitoring gaps shown separately</small></section>{([['activeAlerts','Active alerts'],['blockedSessions','Blocked sessions now'],['deadlocks','Deadlocks in window']] as const).map(([key,label])=>{const total=aggregateValue(data.evidence,key);return <section className="kpi" key={key}><p>{label}</p><strong>{total.text}</strong><small>{total.note}</small></section>;})}</div>
      <div className="overview-main"><section className="panel overview-panel"><div className="overview-panel-heading"><div><p className="eyebrow">WORKLOAD</p><h2>How busy is your SQL environment?</h2></div><div className="overview-workload-controls"><label>Metric<select aria-label="Workload metric" value={workloadMetric} onChange={e=>setWorkload(e.target.value)}>{databaseBreakdown?<option value="activity.user_sessions">Observed user sessions by database</option>:<><option value="engine.batch_requests_per_second">Batch requests / sec</option><option value="engine.user_connections">Connections</option></>}</select></label>{scope.target&&<label>Break down by<select aria-label="Workload breakdown" value={workloadView} onChange={e=>setWorkloadView(e.target.value as 'server'|'database')}><option value="database">Database</option><option value="server">Server</option></select></label>}</div></div><OverviewChart series={allSeries.filter(s=>s.metric===workloadMetric)} previous={previous.filter(s=>s.metric===workloadMetric)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/>{databaseBreakdown?<small>Observed user sessions are grouped by database context, including idle user sessions. Server-wide batch requests/sec cannot be attributed to individual databases.{databaseSeriesPartial?' The busiest database series are shown when the database count exceeds the bounded overview limit.':''}</small>:workload.endsWith('per_second')&&<small>Rates require two successful samples with the same engine start marker. Older history without the marker is unavailable.</small>}</section>
      <section className="panel overview-panel overview-attention"><p className="eyebrow">INVESTIGATE FIRST</p><h2>Where to focus</h2>{issues.length?issues.map((issue,i)=><a className="overview-issue" key={`${issue.targetId}:${i}`} href={issueHref(scope,issue.targetId,issue.destination,issue.observedAtUtc)}><span className="issue-order">{i+1}</span><div><strong>{issue.title}</strong><span>{issue.server}</span><small>{issue.detail}</small><small>{issue.observedAtUtc?timeLabel(issue.observedAtUtc):'Observation time unavailable'} · Open investigation →</small></div></a>):<p className="overview-empty">No actionable exceptions in the available evidence. Check collection coverage before concluding the environment is healthy.</p>}</section></div>
      <FleetServerGrid snapshot={data} scope={scope} search={serverSearch} onSearch={setServerSearch} formatTime={timeLabel} />
      <div className="overview-grid"><section className="panel overview-panel"><p className="eyebrow">CONTENTION</p><h2>Leading observed waits</h2><p className="muted">Latest comparable sample deltas per server; known background waits excluded. These are not selected-window totals.</p>{renderResources(resources.filter(r=>r.label.startsWith('Wait ·')).sort((a,b)=>(b.value??0)-(a.value??0)).slice(0,5),'activity')}</section>
      <section className="panel overview-panel"><div className="overview-panel-heading"><div><p className="eyebrow">CONTENTION HISTORY</p><h2>Blocking and deadlocks</h2></div><select aria-label="Contention metric" value={contention} onChange={e=>setContention(e.target.value)}><option value="blocking.sessions">Blocked sessions</option><option value="deadlocks">Deadlocks</option></select></div><OverviewChart series={allSeries.filter(s=>s.metric===contention)} previous={previous.filter(s=>s.metric===contention)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/><small>Blocking shows the peak distinct sessions per observed snapshot in each bucket; deadlocks show unique observed events. Empty buckets do not establish uninterrupted collection.</small></section></div>
      <section className="panel overview-panel"><div className="overview-panel-heading"><div><p className="eyebrow">RESOURCE PRESSURE</p><h2>Host and storage observations</h2></div><select aria-label="Resource metric" value={resourceMetric} onChange={e=>setResourceMetric(e.target.value)}>{Object.entries(labels).map(([key,label])=><option key={key} value={key}>{label}</option>)}</select></div><OverviewChart series={allSeries.filter(s=>s.metric===resourceMetric)} previous={previous.filter(s=>s.metric===resourceMetric)} fromUtc={data.fromUtc} toUtc={data.toUtc} {...chartInteraction}/><small>Separate server/resource series; host CPU is not SQL process CPU. Shared hosts are not added together.</small>{renderResources(resources.filter(r=>r.label.startsWith(resourceMetric)).sort((a,b)=>resourceMetric.includes('free')||resourceMetric.includes('available')?(a.value??Infinity)-(b.value??Infinity):(b.value??-Infinity)).slice(0,5),'analytics')}</section>
      <section className="panel overview-panel"><p className="eyebrow">OPERATIONS</p><h2>Recovery, jobs, and database resources</h2><div className="overview-grid">{['TempDB used','SQL Agent failures','Backup evidence','Availability replicas'].map(label=><div key={label}><h3>{label}</h3>{renderResources(resources.filter(r=>r.label===label).sort((a,b)=>(b.value??-1)-(a.value??-1)).slice(0,3),'operations')}</div>)}</div></section>
      <details className="panel overview-panel"><summary>Collection coverage and evidence gaps</summary>{data.evidence.map(e=><div className="overview-gap" key={e.targetId}><a href={href('health',e.targetId)}>{e.displayName}</a><span>SQL collection: {e.collectionState} · Last success: {e.lastObservedUtc?timeLabel(e.lastObservedUtc):'unavailable'}</span>{e.gaps.map((gap,i)=><small key={i}>{gap}</small>)}</div>)}<p className="muted">{overviewEvidenceFooter(data,timeLabel)}</p></details>
      {result.comparisonError&&<p role="status">{result.comparisonError}</p>}
      </>}
    </>}
  </div>;
}
