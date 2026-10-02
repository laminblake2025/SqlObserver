// Synthetic, loopback-only visual QA. Never imported by production code.
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {resolve,extname,sep} from 'node:path';
import {handleAvailabilityFixture} from './availability-evidence-fixture.mjs';
import {handleBackupFixture} from './backup-assessment-fixture.mjs';
import {overviewBackupFixture} from './overview-backup-fixture.mjs';
import {handleSavedIncidentFixture} from './saved-incident-fixture.mjs';
import {handleAgentHistoryFixture} from './agent-history-fixture.mjs';
import {handleChartReferenceFixture} from './chart-reference-fixture.mjs';
const root=resolve(import.meta.dirname,'../dist');
const targets=Array.from({length:10},(_,i)=>({instanceId:`11111111-1111-4111-8111-${String(i+1).padStart(12,'0')}`,displayName:`SYNTHETIC SQL ${String(i+1).padStart(2,'0')}`,instanceKey:`synthetic-${i+1}`,host:`sql${i+1}.invalid`,lifecycle:'active',capabilityStatus:'supported',capabilityReasons:[],configurationRevision:1,tcpPort:1433}));
const send=(res,value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));};
createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://localhost');
 if(await handleChartReferenceFixture(url,res,req,targets,send))return;
 // Opt-in dashboard interaction evidence, isolated from existing scenario contracts.
 if(req.headers.cookie?.includes('dashboardRefactor=1')) {
  if(await handleAvailabilityFixture(url,res))return;
  if(await handleBackupFixture(url,res,req))return;
  const route=/^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if(route&&targets.some(t=>t.instanceId===route[1])) {
   const instanceId=route[1],surface=route[2],now=new Date().toISOString();
   const collector={collectorId:'engine.core',manifestVersion:1,outputSchemaVersion:1,state:'current',reason:'none',lastExecutionOutcome:'succeeded',circuitState:'closed',durationMilliseconds:20,retryCount:0,sourceRows:2,outputRows:2,insertedRows:2,duplicateRows:0,rejectedRows:0,responseBytes:1024,persistedBytes:1024,hasVisibilityGap:false,sampleLossKind:null,minimumLostItems:null,minimumLostBytes:null,lossCountIsExact:null,lastSuccessAtUtc:now,lastAttemptAtUtc:now,scheduledAtUtc:now,nextDueAtUtc:now};
   const evidence={runId:'22222222-2222-4222-8222-222222222222',targetRevision:'1',collectorId:'blocking.current',freshness:'current',outcome:'succeeded',reason:'none',isPartial:false,completedAtUtc:now};
   const edge={blockedSessionId:72,blockerKind:'session',blockerSessionId:51,rootBlockerSessionId:51,waitType:'LCK_M_X',waitingTaskCount:'1',waitDurationMilliseconds:'12000',chainDepth:1,chainState:'resolved',observedAtUtc:now};
   if(surface==='health')return send(res,{instanceId,state:'current',repositoryTimeUtc:now,collectors:[collector],coreMetrics:[{sampleId:'1',metricId:'engine.user_connections',observedAtUtc:now,value:428,dimensions:[]}]});
   if(surface==='health/databases')return send(res,{instanceId,repositoryTimeUtc:now,collector,items:[{databaseId:5,name:'Orders',state:'online',recoveryModel:'full',userAccess:'multi_user',isReadOnly:false,compatibilityLevel:160,logReuseWaitDescription:'nothing',observedAtUtc:now,collector}],nextCursor:null});
   if(surface==='health/files')return send(res,{instanceId,repositoryTimeUtc:now,collector,items:[{databaseId:5,fileId:1,logicalName:'Orders_data',fileType:'rows',state:'online',sizeBytes:'8589934592',maximumSizeBytes:null,growthBytes:'67108864',growthPercent:0,readCount:'120000',writeCount:'80000',bytesRead:'12000000',bytesWritten:'8000000',ioStallMilliseconds:'1040000',readStallMilliseconds:'480000',writeStallMilliseconds:'560000',observedAtUtc:now,collector}],nextCursor:null});
   if(surface==='health/database-option-changes')return send(res,{targetId:instanceId,fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),semantics:'observed_changes_only',items:[]});
   if(surface==='health/server-configuration-changes')return send(res,{targetId:instanceId,targetRevision:1,publicationGeneration:0,fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),coverage:'never_collected',items:[],nextCursor:null});
   if(surface==='health/server-configuration-snapshot')return send(res,{targetId:instanceId,targetRevision:1,runId:null,observedAtUtc:null,serverMajorVersion:16,collectorEnabled:false,lastOutcome:null,coverage:'never_collected',items:[],nextCursor:null});

   if(surface==='activity/waits/trend') {
    const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc'),points=[];
    for(let at=Math.floor(Date.parse(fromUtc)/300000)*300000;at<Date.parse(toUtc);at+=300000)
     for(const category of ['Lock','I/O','CPU/signal','Memory','Parallelism','Log','Other'])points.push({bucketStartUtc:new Date(at).toISOString(),category,waitMilliseconds:category==='Lock'?'12000':category==='I/O'?'5000':'0',runCount:10,missingSummaryRuns:0,partialRuns:0,incomparableTypes:0,comparableTypes:2});
    return send(res,{instanceId,fromUtc,toUtc,repositoryTimeUtc:now,points});
   }
   const summary={eventId:'33333333-3333-4333-8333-333333333333',fingerprint:'a'.repeat(64),occurredAtUtc:new Date(Date.parse(url.searchParams.get('toUtc')??now)-60000).toISOString(),participantCount:2,relationCount:2,parseTruncated:false,collectedAtUtc:now};
   if(surface==='deadlocks')return send(res,{targetId:instanceId,repositoryTimeUtc:now,items:[summary],nextCursor:null});
   if(surface==='deadlocks/'+summary.eventId)return send(res,{summary,participants:[{sessionId:51,isVictim:true},{sessionId:72,isVictim:false}],relations:[{blockerSessionId:51,waiterSessionId:72,resourceCategory:'key',lockMode:'X'},{blockerSessionId:72,waiterSessionId:51,resourceCategory:'key',lockMode:'X'}]});
   if(surface==='alerts/active')return send(res,{targetId:instanceId,snapshotUtc:now,items:[{alertId:'44444444-4444-4444-8444-444444444444',ruleId:'55555555-5555-4555-8555-555555555555',ruleName:'SYNTHETIC sustained blocking',state:'firing',firstObservedUtc:now,firedUtc:now,value:12,reason:'12 distinct sessions blocked',deliverySuppressed:false}],nextCursor:null});
   if(surface==='query-performance/cache/top' || /^query-performance\/cache\/databases\/5\/history\/b{64}\/plans\/c{64}$/.test(surface)) {
    const start=url.searchParams.get('fromUtc'),end=url.searchParams.get('toUtc');
    // Ranking and history use half-open windows: a capture exactly at toUtc is excluded.
    const duration=Date.parse(end)-Date.parse(start);
    const firstObservedAtUtc=new Date(Date.parse(start)+duration/4).toISOString();
    const lastObservedAtUtc=new Date(Date.parse(start)+duration*3/4).toISOString();
    const identity={targetId:instanceId,databaseId:5,databaseIncarnation:'a'.repeat(64),queryFingerprint:'b'.repeat(64),planFingerprint:'c'.repeat(64)};
    const counters={cpuMilliseconds:'100.000',durationMilliseconds:'200.000',executions:'10',logicalReads:'1000',writes:'10',rows:'100'};
    if(surface==='query-performance/cache/top')return send(res,{targetId:instanceId,fromUtc:start,toUtc:end,metric:url.searchParams.get('metric'),semantics:'plan_cache_delta',coverage:'observed_entries_only',sourceFrames:2,incompleteFrames:0,comparableRows:1,unavailableRows:0,boundaryRows:0,hasMore:false,items:[{...identity,...counters,comparableRows:1,firstObservedAtUtc,lastObservedAtUtc}]});
    return send(res,{...identity,fromUtc:start,toUtc:end,snapshotUtc:end,revision:1,totalRows:1,nextCursor:null,semantics:'plan_cache_delta',coverage:'observed_entries_only',items:[{...counters,collectionRunId:evidence.runId,cacheEntryFingerprint:'d'.repeat(64),sourceEpoch:'e'.repeat(64),observedAtUtc:lastObservedAtUtc,committedAtUtc:lastObservedAtUtc,captureComplete:true,transition:'comparable',intervalFromUtc:firstObservedAtUtc,intervalToUtc:lastObservedAtUtc}]});
   }
   if(surface==='activity/live/history')return send(res,[]);
   if(surface==='activity/live') {
    const row=(id,blocker)=>({identity:`synthetic-session-${id}`,sessionId:id,requestId:0,databaseId:5,databaseName:'Orders',login:'synthetic_dba',clientHost:'synthetic-app.invalid',application:'SYNTHETIC Orders',status:blocker?'suspended':'running',command:'SELECT',isUser:true,waitType:blocker?'LCK_M_X':null,blocker,cpuMs:'1200',memoryBytes:'1048576',reads:'100',writes:'0',logicalReads:'1000',elapsedMs:'12000',queryId:null,queryState:'unavailable',engineStartup:'2026-09-25T00:00:00',sessionLogin:'2026-09-26T00:00:00',requestStart:'2026-09-26T08:00:00',delta:null});
    let rows=[row(51,null),row(72,51)];
    // Independent blocking-filter fixture; ordinary dashboard screenshots keep their two rows.
    if(req.headers.cookie?.includes('blockingChainFixture=1'))rows.push(row(73,72),row(81,null),row(82,81),row(83,82),row(90,null));
    if(url.searchParams.get('blockedOnly')==='true')rows=rows.filter(item=>item.blocker!==null);
    return send(res,{snapshotId:evidence.runId,observedUtc:now,repositoryTimeUtc:now,state:'current',truncated:false,rows,databases:[{id:5,name:'Orders'}],nextCursor:null});
   }
   if(surface.startsWith('activity/'))return send(res,{instanceId,repositoryTimeUtc:now,evidence,items:surface==='activity/blocking/current'?[edge]:[],nextCursor:null,...(url.searchParams.has('fromUtc')?{fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')}:{})});
  }
 }

 if(await handleSavedIncidentFixture(url,res,req,targets))return;
 if(await handleAgentHistoryFixture(url,res,targets,send))return;
 if(url.pathname==='/__fixture') {const mode=url.searchParams.get('mode');if(!['empty','partial','error','loading','ready','backup-expiry'].includes(mode))return send(res,{},400);res.writeHead(302,{'set-cookie':`overviewMode=${mode}; SameSite=Strict; Path=/`,location:'/'});res.end();return;}
 if(url.pathname==='/api/v1/observation-targets')return send(res,{items:targets,nextCursor:null});
 if(/^\/api\/v1\/observation-targets\/[^/]+\/health\/files$/.test(url.pathname)){
  const instanceId=url.pathname.split('/')[4];
  if(!targets.some(t=>t.instanceId===instanceId))return send(res,{},404);
  const cursor=url.searchParams.get('cursor');
  if(cursor!==null&&cursor!=='synthetic-file-page-2')return send(res,{},400);
  return send(res,{instanceId,repositoryTimeUtc:new Date().toISOString(),collector:{state:'current'},nextCursor:cursor===null?'synthetic-file-page-2':null,items:cursor===null?[
   {databaseId:5,fileId:1,logicalName:'Orders_data',sizeBytes:'8589934592',readCount:'120000',writeCount:'80000',readStallMilliseconds:'480000',writeStallMilliseconds:'560000',observedAtUtc:new Date().toISOString()},
   {databaseId:5,fileId:2,logicalName:'Orders_log',sizeBytes:'2147483648',readCount:'4000',writeCount:'35000',readStallMilliseconds:'8000',writeStallMilliseconds:'175000',observedAtUtc:new Date().toISOString()},
  ]:[
   {databaseId:7,fileId:1,logicalName:'Warehouse_data',sizeBytes:'17179869184',readCount:'90000',writeCount:'24000',readStallMilliseconds:'450000',writeStallMilliseconds:'96000',observedAtUtc:new Date().toISOString()},
  ]});
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
   const metric=(key,unit,base,dimension=null)=>({targetId:t.instanceId,label:t.displayName,metric:key,unit,state:gap?'partial':'observed',dimension,points:gap?[]:Array.from({length:48},(_,j)=>({timeUtc:new Date(from+(to-from)*j/48).toISOString(),value:Math.max(0,base*(1+.25*Math.sin(j/5+i))+(i===1&&j>27&&j<34?base*.7:0)),samples:10}))});
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
