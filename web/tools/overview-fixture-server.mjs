// Synthetic, loopback-only visual QA. Never imported by production code.
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {resolve,extname,sep} from 'node:path';
import {randomUUID} from 'node:crypto';
import {handleAvailabilityFixture} from './availability-evidence-fixture.mjs';
import {handleBackupFixture} from './backup-assessment-fixture.mjs';
import {overviewBackupFixture} from './overview-backup-fixture.mjs';
import {handleSavedIncidentFixture} from './saved-incident-fixture.mjs';
import {handleAgentHistoryFixture} from './agent-history-fixture.mjs';
import {handleChartReferenceFixture} from './chart-reference-fixture.mjs';
import {handleDashboardSummaryFixture,dashboardSummaryCpuPoints} from './dashboard-summary-fixture.mjs';
import {handleQueryRowTrendFixture} from './query-row-trend-fixture.mjs';
import {syntheticCollector,syntheticFile} from './health-contract-fixture.mjs';
import {handleWorkspaceSelectionFixture} from './workspace-selection-fixture.mjs';
import {handleMaintenanceCalendarFixture} from './maintenance-calendar-fixture.mjs';
import {handleSqlVolumeFixture} from './sql-volume-fixture.mjs';
import {handleMaintenanceEditFixture} from './maintenance-edit-fixture.mjs';
import {handleWaitCapturedSessionFixture} from './wait-captured-session-fixture.mjs';
import {handleDashboardNativeInsightsFixture} from './dashboard-native-insights-fixture.mjs';
import {handleDashboardRetainedEvidenceFixture} from './dashboard-retained-evidence-fixture.mjs';
import {handleReportWorkflowFixture} from './report-workflow-fixture.mjs';
import {nativeAlertSelectionPage} from './alert-selection-fixture.mjs';
import {handleAnalyticsWorkflowFixture} from './analytics-workflow-fixture.mjs';
const root=resolve(import.meta.dirname,'../dist');
const targets=Array.from({length:10},(_,i)=>({instanceId:`11111111-1111-4111-8111-${String(i+1).padStart(12,'0')}`,displayName:`SYNTHETIC SQL ${String(i+1).padStart(2,'0')}`,instanceKey:`synthetic-${i+1}`,host:`sql${i+1}.invalid`,lifecycle:'active',capabilityStatus:'supported',capabilityReasons:[],configurationRevision:1,tcpPort:1433,authenticationMode:'windows_integrated_service_identity',encryptionMode:'mandatory_validated',discoveryRequestedAtUtc:'2026-09-25T12:00:00Z',lastDiscoveryAtUtc:'2026-09-25T12:01:00Z'}));
// The ordinary live fixture exposes one synthetic capture. Filter/page reads
// must retain that GUID's observation time even as repository read time moves.
const ordinaryLiveCapturedUtc=new Date().toISOString();
// A retained deadlock's occurrence belongs to the event, not its read time.
const ordinaryDeadlockOccurredUtc='2026-09-26T10:00:00.000Z';
const ordinaryDeadlocks=[
 {eventId:'33333333-3333-4333-8333-333333333333',occurredAtUtc:ordinaryDeadlockOccurredUtc},
 {eventId:'33333333-3333-4333-8333-333333333334',occurredAtUtc:new Date(Date.parse(ordinaryLiveCapturedUtc)-60000).toISOString()},
].map(event=>({...event,fingerprint:'a'.repeat(64),participantCount:2,relationCount:2,parseTruncated:false,collectedAtUtc:ordinaryLiveCapturedUtc}));
const timelineDeadlockOccurrences=new Map();
const workspaceIncidents=new Map();
for(const target of targets.slice(0,2)) {
 const tail=target.instanceId.slice(-12),operationId=`bbbbbbbb-bbbb-4bbb-8bbb-${tail}`;
 workspaceIncidents.set(operationId,{body:null,result:{targetId:target.instanceId,targetRevision:target.configurationRevision,threadId:`aaaaaaaa-aaaa-4aaa-8aaa-${tail}`,operationId,fromUtc:'2026-09-26T08:00:00.000Z',toUtc:'2026-09-26T12:00:00.000Z',savedAtUtc:'2026-09-30T12:00:00.000Z',title:`SYNTHETIC retained investigation ${tail.slice(-2)}`,notes:'Synthetic retained notes; no connected server evidence.',evidenceTruncated:false,evidence:[]}});
}
const send=(res,value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));};
// Preserve the same microsecond precision as the native equal-width comparison contract.
const utcTicks=value=>{
 const match=/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?Z$/.exec(value??'');
 if(!match)throw new Error('Synthetic UTC bounds must retain at most six fractional digits.');
 return BigInt(Date.parse(match[1]+'Z'))*1000n+BigInt((match[2]??'').padEnd(6,'0'));
};
const ticksUtc=value=>new Date(Number(value/1000n)).toISOString().slice(0,19)+'.'+String(value%1000000n).padStart(6,'0')+'Z';
createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://localhost');
 if(handleAnalyticsWorkflowFixture(url,res,req,targets,send))return;
 if(await handleReportWorkflowFixture(url,res,req,targets,send))return;
 if(handleDashboardRetainedEvidenceFixture(url,res,req,targets,send))return;
 if(handleDashboardNativeInsightsFixture(url,res,req,targets,send))return;
 if(await handleMaintenanceEditFixture(url,res,req,targets,send))return;
 if(handleWaitCapturedSessionFixture(url,res,req,targets,send))return;
 if(handleSqlVolumeFixture(url,res,req,targets,send))return;
 if(handleMaintenanceCalendarFixture(url,res,req,targets,send))return;
 if(handleWorkspaceSelectionFixture(url,res,req,targets,send))return;
 const incidentWorkspace=req.headers.cookie?.includes('incidentWorkspaceFixture=1');
 const queryTextReader=req.headers.cookie?.includes('activityQueryReaderFixture=1');
 if((incidentWorkspace||queryTextReader)&&url.pathname==='/api/v1/me') {
  const targetId=url.searchParams.get('targetId'),roles=['Viewer',...(queryTextReader?['QueryTextReader']:[]),...(incidentWorkspace&&!req.headers.cookie?.includes('incidentWorkspaceRole=viewer')?['Operator']:[])];
  return send(res,{active:true,targetId,grantedRoles:roles,allTargetRoles:roles,targetRoles:targetId?roles:[]});
 }
 const incidentRoute=/^\/api\/v1\/observation-targets\/([^/]+)\/incidents\/saved(?:\/([^/]+))?$/.exec(url.pathname);
 if(incidentWorkspace&&incidentRoute) {
  const target=targets.find(item=>item.instanceId===incidentRoute[1]),threadId=incidentRoute[2];
  if(!target)return send(res,{},404);
  if(req.method==='GET'&&threadId) {
   const saved=[...workspaceIncidents.values()].find(item=>item.result.targetId===target.instanceId&&item.result.threadId===threadId);
   return send(res,saved?.result??{},saved?200:404);
  }
  if(req.method==='GET') {
   const snapshotUtc=new Date().toISOString();
   const records=[...workspaceIncidents.values()].map(item=>item.result).filter(item=>item.targetId===target.instanceId).sort((a,b)=>b.savedAtUtc.localeCompare(a.savedAtUtc)||b.threadId.localeCompare(a.threadId));
   return send(res,{targetId:target.instanceId,snapshotUtc,items:records.slice(0,20).map(({targetId,targetRevision,threadId,fromUtc,toUtc,savedAtUtc,title,evidenceTruncated,evidence})=>({targetId,targetRevision,threadId,fromUtc,toUtc,savedAtUtc,title,evidenceTruncated,evidenceCount:evidence.length})),hasMore:false,nextCursor:null});
  }
  if(req.method!=='POST'||threadId)return send(res,{},405);
  if(req.headers.cookie?.includes('incidentWorkspaceRole=viewer'))return send(res,{},403);
  let body='';for await(const chunk of req){body+=chunk.toString('utf8');if(Buffer.byteLength(body)>16384)return send(res,{},413);}
  const command=JSON.parse(body);
  if(command.expectedTargetRevision!==target.configurationRevision)return send(res,{},409);
  const existing=workspaceIncidents.get(command.operationId);
  if(existing)return send(res,existing.result,existing.body===body&&existing.result.targetId===target.instanceId?200:409);
  const result={targetId:target.instanceId,targetRevision:target.configurationRevision,threadId:randomUUID(),operationId:command.operationId,fromUtc:command.fromUtc,toUtc:command.toUtc,savedAtUtc:new Date().toISOString(),title:command.title,notes:command.notes,evidenceTruncated:false,evidence:[]};
  workspaceIncidents.set(command.operationId,{body,result});
  // The first uncertain response retains the synthetic committed record so the
  // same immutable command can verify its outcome on retry.
  return send(res,req.headers.cookie?.includes('incidentWorkspaceRetry=1')?{}:result,req.headers.cookie?.includes('incidentWorkspaceRetry=1')?500:200);
 }
 if(req.headers.cookie?.includes('alertOperatorFixture=1')&&url.pathname==='/api/v1/me') {
  const targetId=url.searchParams.get('targetId'),roles=['Viewer','Operator'];
  return send(res,{active:true,targetId,grantedRoles:roles,allTargetRoles:roles,targetRoles:targetId?roles:[]});
 }
 if(req.headers.cookie?.includes('alertSelectionFixture=1')&&url.pathname==='/api/v1/alerts/active') {
  const page=nativeAlertSelectionPage(targets,undefined,url.searchParams.get('cursor'));
  return send(res,page??{},page?200:400);
 }
 if(handleQueryRowTrendFixture(url,res,req,targets,send))return;
 if(handleDashboardSummaryFixture(url,res,req,targets,send))return;
 if(await handleChartReferenceFixture(url,res,req,targets,send))return;
 // Opt-in dashboard interaction evidence, isolated from existing scenario contracts.
 if(req.headers.cookie?.includes('dashboardRefactor=1')) {
  if(await handleAvailabilityFixture(url,res))return;
  if(await handleBackupFixture(url,res,req))return;
  const route=/^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if(route&&targets.some(t=>t.instanceId===route[1])) {
   const instanceId=route[1],surface=route[2],now=new Date().toISOString();
   const collector={collectorId:'engine.core',manifestVersion:1,outputSchemaVersion:1,state:'current',reason:'none',lastExecutionOutcome:'succeeded',circuitState:'closed',durationMilliseconds:20,retryCount:0,sourceRows:2,outputRows:2,insertedRows:2,duplicateRows:0,rejectedRows:0,responseBytes:1024,persistedBytes:1024,hasVisibilityGap:false,sampleLossKind:null,minimumLostItems:null,minimumLostBytes:null,lossCountIsExact:null,lastSuccessAtUtc:now,lastAttemptAtUtc:now,scheduledAtUtc:now,nextDueAtUtc:now};
   const evidence={runId:'22222222-2222-4222-8222-222222222222',targetRevision:'1',collectorId:'blocking.current',freshness:'current',outcome:'succeeded',reason:'completed',isPartial:false,completedAtUtc:now};
   const edge={blockedSessionId:72,blockerKind:'session',blockerSessionId:51,rootBlockerSessionId:51,waitType:'LCK_M_X',waitingTaskCount:'1',waitDurationMilliseconds:'12000',chainDepth:1,chainState:'resolved',observedAtUtc:now};
   if(surface==='health')return send(res,{instanceId,state:'current',repositoryTimeUtc:now,collectors:[collector],coreMetrics:[{sampleId:'33333333-3333-4333-8333-000000000001',metricId:'engine.user_connections',observedAtUtc:now,value:428,dimensions:[]}]});
   if(surface==='health/databases') {
    const scoped=req.headers.cookie?.includes('databaseScopeFixture=1');
    const database=(id,name)=>({databaseId:id,name:scoped&&instanceId===targets[1].instanceId?`${name} 02`:name,state:'online',recoveryModel:'full',userAccess:'multi_user',isReadOnly:false,compatibilityLevel:160,logReuseWaitDescription:'nothing',observedAtUtc:now,collector});
    return send(res,{instanceId,repositoryTimeUtc:now,collector,items:scoped?[database(5,'Orders'),database(6,'Warehouse')]:[database(5,'Orders')],nextCursor:null});
   }
   if(surface==='health/files') {
    const fileCollector={...collector,collectorId:'database.files'};
    const file={databaseId:5,fileId:1,logicalName:'Orders_data',fileType:'rows',state:'online',sizeBytes:'8589934592',maximumSizeBytes:null,growthBytes:'67108864',growthPercent:0,readCount:'120000',writeCount:'80000',bytesRead:'12000000',bytesWritten:'8000000',ioStallMilliseconds:'1040000',readStallMilliseconds:'480000',writeStallMilliseconds:'560000',observedAtUtc:now,collector:fileCollector};
    return send(res,{instanceId,repositoryTimeUtc:now,collector:fileCollector,items:req.headers.cookie?.includes('databaseScopeFixture=1')?[file,{...file,databaseId:6,logicalName:'Warehouse_data',sizeBytes:'17179869184'}]:[file],nextCursor:null});
   }
   if(surface==='health/database-option-changes'||surface==='health/server-configuration-changes') {
    const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc'),start=Date.parse(fromUtc),end=Date.parse(toUtc);
    const populated=req.headers.cookie?.includes('timelineFixture=1')&&Number.isFinite(start)&&Number.isFinite(end)&&end>start;
    const times={previousObservedAtUtc:new Date((populated?start:Date.parse(now))-60000).toISOString(),observedAtUtc:new Date(populated?start+(end-start)/4:Date.parse(now)).toISOString(),detectedAtUtc:new Date(populated?start+(end-start)/4+1000:Date.parse(now)).toISOString()};
    const context=req.headers.cookie?.includes('investigationContextFixture=1')&&populated;
    const nearby=(fraction,minutes)=>({...times,detectedAtUtc:new Date(start+(end-start)*fraction-minutes*60000).toISOString()});
    if(surface==='health/database-option-changes')return send(res,{targetId:instanceId,fromUtc,toUtc,semantics:'detected_between_consecutive_complete_inventory_runs',items:populated?[{...(context?nearby(0.5,8):times),databaseId:5,databaseName:'Orders',optionKey:'compatibility_level',previousValue:'150',currentValue:'160',collectionRunId:evidence.runId},{...(context?nearby(0.75,8):times),databaseId:6,databaseName:'Warehouse',optionKey:'is_read_only',previousValue:'false',currentValue:'true',collectionRunId:evidence.runId},...(context?[{...nearby(0.75,7),databaseId:7,databaseName:'Reporting',optionKey:'is_read_only',previousValue:'false',currentValue:'true',collectionRunId:evidence.runId}]:[])]:[]});
    return send(res,{targetId:instanceId,targetRevision:1,publicationGeneration:populated?1:0,fromUtc,toUtc,coverage:populated?'observed_changes_only':'never_collected',items:populated?[{...(context?nearby(0.75,7):times),configurationId:1539,configurationName:'cost threshold for parallelism',sourceType:'int',previousConfiguredValue:'5',configuredValue:'50',previousInUseValue:'5',inUseValue:'50',configuredChanged:true,inUseChanged:true,runId:evidence.runId}]:[],nextCursor:null});
   }
   if(surface==='health/server-configuration-snapshot')return send(res,{targetId:instanceId,targetRevision:1,runId:null,observedAtUtc:null,serverMajorVersion:16,collectorEnabled:false,lastOutcome:null,coverage:'never_collected',items:[],nextCursor:null});

   if(surface==='query-performance/regressions' && req.headers.cookie?.includes('timelineFixture=1')) {
    const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc');
    const start=Date.parse(fromUtc),end=Date.parse(toUtc),duration=end-start;
    if(!Number.isFinite(start)||!Number.isFinite(end)||duration<=0||duration>7*86400000||url.searchParams.get('limit')!=='25')return send(res,{},400);
    const comparison=(databaseId,query,previous,current,ratio,fraction)=>({databaseId,queryFingerprint:query.repeat(64),previousPlanFingerprint:previous.repeat(64),currentPlanFingerprint:current.repeat(64),previousAverageMilliseconds:'100.000',currentAverageMilliseconds:`${ratio*100}.000`,slowdownRatio:`${ratio}.000`,previousExecutions:'20',currentExecutions:'25',previousComparableRows:2,currentComparableRows:3,previousSourceRows:2,currentSourceRows:3,lastObservedAtUtc:new Date(start+duration*fraction).toISOString()});
    return send(res,{targetId:instanceId,fromUtc,toUtc,comparisonFromUtc:ticksUtc(2n*utcTicks(fromUtc)-utcTicks(toUtc)),semantics:'query_store_group_plan_comparison',coverage:'observed_groups_only',items:[comparison(5,instanceId===targets[0].instanceId?'b':'7','a','c',4,0.5),comparison(6,instanceId===targets[0].instanceId?'d':'8','f','e',3,0.75)]});
   }

   if(surface==='activity/waits/trend') {
    const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc'),points=[];
    for(let at=Math.floor(Date.parse(fromUtc)/300000)*300000;at<Date.parse(toUtc);at+=300000)
     for(const category of ['Lock','I/O','CPU/signal','Memory','Parallelism','Log','Other'])points.push({bucketStartUtc:new Date(at).toISOString(),category,waitMilliseconds:category==='Lock'?'12000':category==='I/O'?'5000':'0',runCount:10,missingSummaryRuns:0,partialRuns:0,incomparableTypes:0,comparableTypes:2});
    return send(res,{instanceId,fromUtc,toUtc,repositoryTimeUtc:now,points});
   }
   const timelineStart=Date.parse(url.searchParams.get('fromUtc')),timelineEnd=Date.parse(url.searchParams.get('toUtc'));
   const timelineDeadlock=req.headers.cookie?.includes('timelineFixture=1');
   if(surface==='deadlocks'&&timelineDeadlock&&Number.isFinite(timelineStart)&&Number.isFinite(timelineEnd))timelineDeadlockOccurrences.set(instanceId,new Date(timelineStart+(timelineEnd-timelineStart)/3).toISOString());
   const deadlockTime=timelineDeadlock?(timelineDeadlockOccurrences.get(instanceId)??ordinaryDeadlockOccurredUtc):ordinaryDeadlockOccurredUtc;
   const summary={eventId:'33333333-3333-4333-8333-333333333333',fingerprint:'a'.repeat(64),occurredAtUtc:deadlockTime,participantCount:2,relationCount:2,parseTruncated:false,collectedAtUtc:now};
   const retainedDeadlocks=timelineDeadlock?[summary]:ordinaryDeadlocks;
   if(surface==='deadlocks')return send(res,{targetId:instanceId,repositoryTimeUtc:now,items:retainedDeadlocks.filter(event=>Date.parse(event.occurredAtUtc)>=timelineStart&&Date.parse(event.occurredAtUtc)<timelineEnd),nextCursor:null});
   const retainedDeadlock=retainedDeadlocks.find(event=>surface==='deadlocks/'+event.eventId);
   if(retainedDeadlock)return send(res,{summary:retainedDeadlock,participants:[{sessionId:51,isVictim:true},{sessionId:72,isVictim:false}],relations:[{blockerSessionId:51,waiterSessionId:72,resourceCategory:'key',lockMode:'X'},{blockerSessionId:72,waiterSessionId:51,resourceCategory:'key',lockMode:'X'}]});
   if(surface==='alerts/active') {
    if(req.headers.cookie?.includes('alertSelectionFixture=1')) {
     const page=nativeAlertSelectionPage(targets,instanceId,url.searchParams.get('cursor'));
     return send(res,page??{},page?200:400);
    }
    return send(res,{targetId:instanceId,snapshotUtc:now,items:[{alertId:'44444444-4444-4444-8444-444444444444',ruleId:'55555555-5555-4555-8555-555555555555',ruleName:'SYNTHETIC sustained blocking',state:'firing',firstObservedUtc:now,firedUtc:now,value:12,reason:'12 distinct sessions blocked',deliverySuppressed:false}],nextCursor:null});
   }
   if(/^alerts\/[^/]+\/acknowledge$/.test(surface)&&req.method==='POST'&&req.headers.cookie?.includes('alertSelectionFixture=1')) {res.writeHead(204);res.end();return;}
   if(surface==='query-performance/status'&&req.headers.cookie?.includes('paletteCatalogFixture=1'))return send(res,{targetId:instanceId,snapshotUtc:now,source:'query_store',sourceState:'read_write',coverage:'complete',fresh:true,truncated:false,contentAvailable:false,databaseStatuses:[],databaseCatalog:[{databaseId:5,databaseName:instanceId===targets[1].instanceId?'Orders 02':'Orders'},{databaseId:6,databaseName:instanceId===targets[1].instanceId?'Warehouse 02':'Warehouse'}]});
   if(req.headers.cookie?.includes('resourceInspectionFixture=1')&&(surface==='resources/buffer-nodes/ple'||/^resources\/files\/(5|6)\/1\/trend$/.test(surface))) {
    const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc'),start=Date.parse(fromUtc),end=Date.parse(toUtc);
    if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start||end-start>7*86400000)return send(res,{},400);
    const buckets=[];
    for(let at=Math.floor(start/3600000)*3600000;at<end;at+=3600000)buckets.push(new Date(at).toISOString());
    if(surface==='resources/buffer-nodes/ple') {
     const selectedNodeId=Number(url.searchParams.get('nodeId')??0);
     if(!Number.isSafeInteger(selectedNodeId)||selectedNodeId<0||selectedNodeId>1023)return send(res,{},400);
     const gauges=offset=>buckets.map((bucketStartUtc,index)=>({bucketStartUtc,sampleCount:10,minimumSeconds:200+offset+index*10,averageSeconds:300+offset+index*10,maximumSeconds:400+offset+index*10}));
     return send(res,{targetId:instanceId,fromUtc,toUtc,semantics:'numa_node_page_life_expectancy_gauge',unit:'seconds',revisionScope:'current',bucketSeconds:3600,selectedNodeId,visibilityGapCount:0,nodes:[0,1].map(nodeId=>({nodeId,sampleCount:buckets.length*10,lastObservedAtUtc:new Date(end-1).toISOString()})),points:[0,1].includes(selectedNodeId)?gauges(selectedNodeId*100):[],aggregatePoints:gauges(200)});
    }
    return send(res,{targetId:instanceId,databaseId:Number(surface.split('/')[2]),fileId:1,fromUtc,toUtc,semantics:'file_counter_delta',revisionScope:'current',bucketSeconds:3600,points:buckets.map((bucketStartUtc,index)=>({bucketStartUtc,sourceRows:10,comparableRows:9,baselineRows:1,resetRows:0,gapRows:0,legacyRows:0,readOperations:'100',readStallMilliseconds:String(400+index*10),writeOperations:'200',writeStallMilliseconds:String(1600+index*20)}))});
   }
   if(surface==='query-performance/cache/top' || /^query-performance\/cache\/databases\/(?:5\/history\/b{64}\/plans\/c{64}|6\/history\/d{64}\/plans\/e{64})$/.test(surface)) {
    const start=url.searchParams.get('fromUtc'),end=url.searchParams.get('toUtc');
    // Ranking and history use half-open windows: a capture exactly at toUtc is excluded.
    const duration=Date.parse(end)-Date.parse(start);
    const firstObservedAtUtc=new Date(Date.parse(start)+duration/4).toISOString();
    const lastObservedAtUtc=new Date(Date.parse(start)+duration*3/4).toISOString();
    const warehouse=surface.includes('/databases/6/');
    const identity={targetId:instanceId,databaseId:warehouse?6:5,databaseIncarnation:(warehouse?'f':'a').repeat(64),queryFingerprint:(warehouse?'d':'b').repeat(64),planFingerprint:(warehouse?'e':'c').repeat(64)};
    const counters=warehouse?{cpuMilliseconds:'200.000',durationMilliseconds:'400.000',executions:'20',logicalReads:'2000',writes:'20',rows:'200'}:{cpuMilliseconds:'100.000',durationMilliseconds:'200.000',executions:'10',logicalReads:'1000',writes:'10',rows:'100'};
    if(surface==='query-performance/cache/top') {
     const items=[{...identity,...counters,comparableRows:1,firstObservedAtUtc,lastObservedAtUtc}];
     if(req.headers.cookie?.includes('databaseScopeFixture=1'))items.unshift({...items[0],databaseId:6,databaseIncarnation:'f'.repeat(64),queryFingerprint:'d'.repeat(64),planFingerprint:'e'.repeat(64),cpuMilliseconds:'200.000',durationMilliseconds:'400.000',executions:'20',logicalReads:'2000',writes:'20',rows:'200'});
     return send(res,{targetId:instanceId,fromUtc:start,toUtc:end,metric:url.searchParams.get('metric'),semantics:'plan_cache_delta',coverage:'observed_entries_only',sourceFrames:2,incompleteFrames:0,comparableRows:items.length,unavailableRows:0,boundaryRows:0,hasMore:false,items});
    }
    return send(res,{...identity,fromUtc:start,toUtc:end,snapshotUtc:end,revision:1,totalRows:1,nextCursor:null,semantics:'plan_cache_delta',coverage:'observed_entries_only',items:[{...counters,collectionRunId:evidence.runId,cacheEntryFingerprint:'d'.repeat(64),sourceEpoch:'e'.repeat(64),observedAtUtc:lastObservedAtUtc,committedAtUtc:lastObservedAtUtc,captureComplete:true,transition:'comparable',intervalFromUtc:firstObservedAtUtc,intervalToUtc:lastObservedAtUtc}]});
   }
   if(req.headers.cookie?.includes('sessionSelectionFixture=1')&&surface.startsWith('activity/live')) {
    const snapshotId='88888888-8888-4888-8888-888888888888',observedUtc='2026-09-26T11:30:00.123456Z';
    const newerId='99999999-9999-4999-8999-999999999999',newerObserved='2026-09-26T11:31:00.123456Z';
    if(surface==='activity/live/history')return send(res,[{id:snapshotId,observedUtc,truncated:false},{id:newerId,observedUtc:newerObserved,truncated:false}]);
    if(surface==='activity/live/query')return send(res,{state:'available',text:'SELECT 1 AS synthetic_fixture_only;'});
    if(surface==='activity/live') {
     const requested=url.searchParams.get('snapshot'),cursor=url.searchParams.get('cursor');
     if(requested&&![snapshotId,newerId].includes(requested))return send(res,{snapshotId:null,observedUtc:null,repositoryTimeUtc:now,state:'unavailable',truncated:false,rows:[],databases:[],nextCursor:null});
     if(cursor&&cursor!=='cGFnZTI=')return send(res,{},400);
     const row=(id,identity,requestId,blocker)=>({identity:identity.repeat(64),sessionId:id,requestId,databaseId:5,databaseName:'Orders',login:'synthetic_dba',clientHost:'synthetic-app.invalid',application:'SYNTHETIC Orders',status:blocker?'suspended':'running',command:'SELECT',isUser:true,waitType:blocker?'LCK_M_X':null,blocker,cpuMs:'1200',memoryBytes:'1048576',reads:'100',writes:'0',logicalReads:'1000',elapsedMs:'12000',queryId:'d'.repeat(64),queryState:'available',engineStartup:'2026-09-25T00:00:00',sessionLogin:'2026-09-26T00:00:00',requestStart:'2026-09-26T11:00:00',delta:null});
     const newer=requested===newerId;
     return send(res,{snapshotId:newer?newerId:snapshotId,observedUtc:newer?newerObserved:observedUtc,repositoryTimeUtc:now,state:'current',truncated:false,rows:cursor?[row(51,'c',1,null)]:[row(51,newer?'e':'a',0,null),row(72,'b',0,51)],databases:[{id:5,name:'Orders'}],nextCursor:cursor?null:'cGFnZTI='});
    }
   }
   if(surface==='activity/live/history')return send(res,[]);
   if(surface==='activity/live') {
    const row=(id,blocker)=>({identity:id.toString(16).padStart(64,'0'),sessionId:id,requestId:0,databaseId:5,databaseName:'Orders',login:'synthetic_dba',clientHost:'synthetic-app.invalid',application:'SYNTHETIC Orders',status:blocker?'suspended':'running',command:'SELECT',isUser:true,waitType:blocker?'LCK_M_X':null,blocker,cpuMs:'1200',memoryBytes:'1048576',reads:'100',writes:'0',logicalReads:'1000',elapsedMs:'12000',queryId:null,queryState:'unavailable',engineStartup:'2026-09-25T00:00:00',sessionLogin:'2026-09-26T00:00:00',requestStart:'2026-09-26T08:00:00',delta:null});
    let rows=[row(51,null),row(72,51)];
    // Independent blocking-filter fixture; ordinary dashboard screenshots keep their two rows.
    if(req.headers.cookie?.includes('blockingChainFixture=1'))rows.push(row(73,72),row(81,null),row(82,81),row(83,82),row(90,null));
    const databaseScoped=req.headers.cookie?.includes('databaseScopeFixture=1');
    const name=base=>databaseScoped&&instanceId===targets[1].instanceId?`${base} 02`:base;
    if(databaseScoped)rows.push({...row(81,null),databaseId:6,databaseName:name('Warehouse')});
    if(databaseScoped&&url.searchParams.has('databaseId'))rows=rows.filter(item=>item.databaseId===Number(url.searchParams.get('databaseId')));
    if(url.searchParams.get('blockedOnly')==='true')rows=rows.filter(item=>item.blocker!==null);
    return send(res,{snapshotId:evidence.runId,observedUtc:ordinaryLiveCapturedUtc,repositoryTimeUtc:now,state:'current',truncated:false,rows,databases:databaseScoped?[{id:5,name:name('Orders')},{id:6,name:name('Warehouse')}]:[{id:5,name:'Orders'}],nextCursor:null});
   }
   if(surface.startsWith('activity/'))return send(res,{instanceId,repositoryTimeUtc:now,evidence,items:surface==='activity/blocking/current'?[edge]:[],nextCursor:null,...(url.searchParams.has('fromUtc')?{fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')}:{})});
  }
 }

 if(await handleSavedIncidentFixture(url,res,req,targets))return;
 if(await handleAgentHistoryFixture(url,res,targets,send))return;
 if(url.pathname==='/__fixture') {const mode=url.searchParams.get('mode');if(!['empty','partial','error','loading','ready','backup-expiry'].includes(mode))return send(res,{},400);res.writeHead(302,{'set-cookie':`overviewMode=${mode}; SameSite=Strict; Path=/`,location:'/'});res.end();return;}
 if(url.pathname==='/api/v1/observation-targets')return send(res,{items:targets,nextCursor:null});
 const directTarget=/^\/api\/v1\/observation-targets\/([^/]+)$/.exec(url.pathname);
 if(req.method==='GET'&&directTarget){
  const target=targets.find(item=>item.instanceId===directTarget[1]);
  return send(res,target??{},target?200:404);
 }
 if(/^\/api\/v1\/observation-targets\/[^/]+\/health\/files$/.test(url.pathname)){
  const instanceId=url.pathname.split('/')[4];
  if(!targets.some(t=>t.instanceId===instanceId))return send(res,{},404);
  const cursor=url.searchParams.get('cursor');
  if(cursor!==null&&cursor!=='synthetic-file-page-2')return send(res,{},400);
  const observedAtUtc=new Date().toISOString(),collector=syntheticCollector(observedAtUtc);
  const items=cursor===null?[
   {databaseId:5,fileId:1,logicalName:'Orders_data',sizeBytes:'8589934592',readCount:'120000',writeCount:'80000',readStallMilliseconds:'480000',writeStallMilliseconds:'560000'},
   {databaseId:5,fileId:2,logicalName:'Orders_log',sizeBytes:'2147483648',readCount:'4000',writeCount:'35000',readStallMilliseconds:'8000',writeStallMilliseconds:'175000'},
  ]:[
   {databaseId:7,fileId:1,logicalName:'Warehouse_data',sizeBytes:'17179869184',readCount:'90000',writeCount:'24000',readStallMilliseconds:'450000',writeStallMilliseconds:'96000'},
  ];
  return send(res,{instanceId,repositoryTimeUtc:observedAtUtc,collector,nextCursor:cursor===null?'synthetic-file-page-2':null,items:items.map(value=>syntheticFile(value,observedAtUtc,collector))});
 }
 if(url.pathname==='/api/v1/overview'){
  const mode=req.headers.cookie?.match(/overviewMode=([a-z-]+)/)?.[1] ?? process.env.SQLOBSERVER_OVERVIEW_FIXTURE_MODE;
  if(mode==='error')return send(res,{},503);
  if(mode==='loading')await new Promise(r=>setTimeout(r,2500));
  const targetId=url.searchParams.get('targetId');const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc');
  const from=Date.parse(fromUtc),to=Date.parse(toUtc),at=new Date(to-30000).toISOString();
  const selected=mode==='empty'?[]:targets.filter(t=>!targetId||t.instanceId===targetId);
  const refreshedAtUtc=new Date().toISOString();
  const evidence=selected.map((t,index)=>{
   const i=targets.indexOf(t),gap=mode==='partial'&&index===0;
   const value=n=>({value:gap?null:n,state:gap?'unavailable':'current',observedAtUtc:gap?null:at});
   const issues=i===1?[{targetId:t.instanceId,server:t.displayName,title:'Sessions blocked',detail:'12 distinct sessions waiting on SQL 02',destination:'activity',priority:1,observedAtUtc:at}]:i===4?[{targetId:t.instanceId,server:t.displayName,title:'SQL Agent failures',detail:'2 jobs affected in selected window',destination:'operations',priority:2,observedAtUtc:at}]:[];
   const metric=(key,unit,base,dimension=null)=>({targetId:t.instanceId,label:t.displayName,metric:key,unit,state:gap||key==='engine.sql_scheduler_cpu_percent'&&req.headers.cookie?.includes('dashboardObservationState=partial')?'partial':'observed',dimension,points:gap?[]:(key==='engine.sql_scheduler_cpu_percent'?dashboardSummaryCpuPoints(req,from,to,i):undefined)??Array.from({length:48},(_,j)=>({timeUtc:new Date(from+(to-from)*j/48).toISOString(),value:Math.max(0,base*(1+.25*Math.sin(j/5+i))+(i===1&&j>27&&j<34?base*.7:0)),samples:10}))});
   const resource=(label,n,unit)=>({targetId:t.instanceId,server:t.displayName,label,value:gap?null:n,unit,state:gap?'unavailable':'observed',observedAtUtc:gap?null:at});
   const databaseSeries=targetId?[metric('activity.user_sessions','sessions',14+i*4,'Orders'),metric('activity.user_sessions','sessions',8+i*3,'Reporting')]:[];
   const backupSummary=gap?null:overviewBackupFixture(t.instanceId,refreshedAtUtc,i,mode==='backup-expiry');
   if(backupSummary?.observedFaultDatabases)issues.push({targetId:t.instanceId,server:t.displayName,title:'Backup faults observed',detail:'Synthetic backup damage or missing checksums',destination:'operations',priority:0,observedAtUtc:refreshedAtUtc});
   if(backupSummary?.status==='policy_violated')issues.push({targetId:t.instanceId,server:t.displayName,title:'Backup policy checks failed',detail:'Synthetic full check failed; log evidence unknown',destination:'operations',priority:1,observedAtUtc:refreshedAtUtc});
   return {targetId:t.instanceId,displayName:t.displayName,backupSummary,collectionState:gap?'stale':'current',lastObservedUtc:at,activeAlerts:value(i===1?9:i===4?2:0),blockedSessions:value(i===1?12:0),deadlocks:{...value(i===1?3:0),state:gap?'unavailable':'observed'},issues,resources:[resource('Wait Ã‚Â· LCK_M_X',i===1?18:2,'seconds since prior sample'),resource('host.cpu.percent',i===1?89:15+i*5,'%'),resource('SQL physical memory',8+i*2,'GiB'),resource('TempDB used',13+i*4,'%'),resource('Backup evidence',8,'records'),resource('SQL Agent failures',i===4?2:0,'observed events')],series:[metric('engine.batch_requests_per_second','batches/sec',100+i*60),metric('engine.user_connections','connections',30+i*12),...databaseSeries,metric('engine.sql_scheduler_cpu_percent','%',25+i*4),metric('engine.scheduler_runnable_tasks','tasks',2+i),metric('engine.memory_grants_pending','grants',1+i),metric('engine.process_physical_memory_bytes','GiB',8+i*2),metric('engine.os_available_memory_bytes','GiB',12+i),metric('host.cpu.percent','%',10+i*5),metric('host.memory.available_bytes','GiB',12+i),metric('blocking.sessions','blocked sessions',i===1?7:1),metric('deadlocks','events',i===1?2:0)],gaps:gap?['Synthetic collection timeout']:[]};
  });
  return send(res,{refreshedAtUtc,fromUtc,toUtc,targetId,targets:(mode==='empty'?[]:targets).map((t,i)=>({targetId:t.instanceId,displayName:t.displayName,lifecycle:t.lifecycle,environment:i<5?'production':'staging',tags:i%2===0?['east','critical']:['west'],groupingRevision:1,canManageGrouping:false})),excludedTargets:0,evidence});
 }
 if(url.pathname.startsWith('/api/'))return send(res,{},404);
 const path=resolve(root,url.pathname==='/'?'index.html':url.pathname.slice(1));if(!path.startsWith(root+sep))return send(res,{},404);
 const body=await readFile(path);res.writeHead(200,{'content-type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[extname(path)]??'application/octet-stream'});res.end(body);
}catch{send(res,{},500);}}).listen(Number(process.env.SQLOBSERVER_OVERVIEW_FIXTURE_PORT??4185),'127.0.0.1',()=>console.log('Synthetic Overview QA (loopback only)'));
