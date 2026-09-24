// Isolated loopback-only synthetic visual fixture. Never imported by the application.
import { createServer } from 'node:http';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve, extname, sep } from 'node:path';
const root=resolve(import.meta.dirname,'../dist');
const timestamp='2026-09-05T02:00:00.000Z';
const targets=Array.from({length:6},(_,i)=>({instanceId:`11111111-1111-4111-8111-${String(i+1).padStart(12,'0')}`,instanceKey:`synthetic-${i+1}`,displayName:`SYNTHETIC · SQL ${['Production','Analytics','Reporting','Recovery','Test','Development'][i]}`,host:`synthetic-${i+1}.invalid`,namedInstance:null,tcpPort:1433,lifecycle:'active',capabilityStatus:i===3?'unreachable':i===2?'degraded':'supported',capabilityReasons:i===3?['synthetic_visibility_gap']:[],configurationRevision:1,authenticationMode:'windows_integrated_service_identity',encryptionMode:'mandatory_validated',discoveryRequestedAtUtc:timestamp,lastDiscoveryAtUtc:timestamp}));
const json=(res,value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));};
const registrationAttempts=[];
createServer(async(req,res)=>{try {
 const url=new URL(req.url,'http://127.0.0.1');
 if(url.pathname==='/api/v1/observation-targets' && req.method==='POST') {
   let body=''; for await (const chunk of req) { body+=chunk; if(body.length>16384)return json(res,{},413); }
   const request=JSON.parse(body); registrationAttempts.push(request.instanceId);
   await writeFile(resolve(import.meta.dirname,'../.artifacts/registration-observations.json'),JSON.stringify(registrationAttempts));
   await new Promise(r=>setTimeout(r,700));
   if(registrationAttempts.length===1)return json(res,{},503);
   return json(res,{...targets[0],...request,lifecycle:'pending_discovery',capabilityStatus:'pending'});
 }
 if(url.pathname==='/api/v1/observation-targets') { let scenario='populated';try{scenario=(await readFile(resolve(import.meta.dirname,'../.artifacts/dashboard-scenario.txt'),'utf8')).trim();}catch{} if(scenario==='error') return json(res,{},503);if(scenario==='loading') await new Promise(r=>setTimeout(r,4000));return json(res,{items:scenario==='empty'?[]:targets,nextCursor:null}); }
 if(url.pathname==='/api/v1/me') { const targetId=url.searchParams.get('targetId');return json(res,{active:true,grantedRoles:['Viewer','Operator'],allTargetRoles:['Viewer','Operator'],targetId,targetRoles:targetId?['Viewer','Operator']:[]}); }
 if(url.pathname==='/api/v1/alerts/active') return json(res,{snapshotUtc:timestamp,nextCursor:null,items:targets.slice(0,3).map((target,i)=>({targetId:target.instanceId,targetName:target.displayName,alertId:`22222222-2222-4222-8222-${String(i+1).padStart(12,'0')}`,ruleId:`33333333-3333-4333-8333-${String(i+1).padStart(12,'0')}`,ruleName:['CPU above threshold','Backup overdue','Collector unavailable'][i],state:i===1?'acknowledged':'firing',firstObservedUtc:'2026-09-05T01:20:00.000Z',firedUtc:'2026-09-05T01:25:00.000Z',acknowledgedUtc:i===1?'2026-09-05T01:30:00.000Z':null,value:i===1?null:87+i,reason:i===1?'No recent full backup':'Threshold exceeded',deliverySuppressed:false}))});
 const targetId=url.pathname.split('/')[4];
 if(url.pathname.endsWith('/health')) return json(res,{instanceId:targetId,state:targetId?.endsWith('4')?'stale':'current',repositoryTimeUtc:timestamp,collectors:[],coreMetrics:[{sampleId:'1',metricId:'engine.user_connections',observedAtUtc:timestamp,value:428,dimensions:[]},{sampleId:'2',metricId:'engine.process_physical_memory_bytes',observedAtUtc:timestamp,value:45741401702,dimensions:[]}]});
 if(url.pathname.endsWith('/alerts/active')) return json(res,{targetId,items:[],snapshotUtc:timestamp,nextCursor:null});
 if(url.pathname.endsWith('/analytics/series')) return json(res,{targetId,metricKey:'host.cpu.percent',state:'partial',fromUtc:'2026-09-05T01:00:00Z',toUtc:timestamp,items:Array.from({length:12},(_,i)=>({observedAtUtc:new Date(Date.parse('2026-09-05T01:00:00Z')+i*240000).toISOString(),value:[14,20,18,26,30,67,24,20,25,33,25,29][i],dimensions:{scope:'aggregate'}}))});
 if(url.pathname.endsWith('/query-performance/status')) return json(res,{targetId,snapshotUtc:timestamp,source:'query_store',sourceState:'read_write',coverage:'truncated',fresh:false,truncated:true,contentAvailable:false,databaseStatuses:[]});
 const fromUtc=url.searchParams.get('fromUtc')??'2026-09-05T01:00:00.000Z',toUtc=url.searchParams.get('toUtc')??timestamp;
 const observation=(i,metric='cpu')=>({databaseId:7,queryFingerprint:(i?'b':'a').repeat(64),planFingerprint:i?null:'c'.repeat(64),source:'query_store',sourceState:'read_write',semantics:'query_store_interval',metric,value:i?12400:42180,intervalStartUtc:fromUtc,intervalEndUtc:toUtc,coverage:'truncated',fresh:false,truncated:true,contentAvailable:false,collectionRunId:'22222222-2222-4222-8222-222222222222',observationKey:(i?'b':'a').repeat(32)});
 if(url.pathname.endsWith('/query-performance/top')) return json(res,{targetId,metric:url.searchParams.get('metric'),snapshotUtc:timestamp,fromUtc,toUtc,nextCursor:null,items:[observation(0,url.searchParams.get('metric')),observation(1,url.searchParams.get('metric'))]});
 if(url.pathname.includes('/history/')) { const queryFingerprint=url.pathname.split('/').at(-1);return json(res,{targetId,databaseId:7,queryFingerprint,snapshotUtc:timestamp,fromUtc,toUtc,nextCursor:null,items:Array.from({length:8},(_,i)=>({...observation(queryFingerprint?.startsWith('b')?1:0),intervalStartUtc:new Date(Date.parse(fromUtc)+(Date.parse(toUtc)-Date.parse(fromUtc))*i/8).toISOString(),intervalEndUtc:new Date(Date.parse(fromUtc)+(Date.parse(toUtc)-Date.parse(fromUtc))*(i+1)/8).toISOString(),observationKey:i.toString(16).padStart(32,'0'),cpuMilliseconds:120+i*8,durationMilliseconds:[150,210,180,240,1700,230,290,190][i],executions:42,logicalReads:100,writes:null,rows:null}))}); }
 if(url.pathname.includes('/plans/')) { await new Promise(r=>setTimeout(r,700)); return json(res,{targetId,databaseId:7,queryFingerprint:'a'.repeat(64),planFingerprint:'c'.repeat(64),source:'query_store',observedAtUtc:timestamp,coverage:'truncated',contentAvailable:false}); }
 if(url.pathname.startsWith('/api/')) return json(res,{},404);
 const path=resolve(root,url.pathname==='/'?'index.html':url.pathname.slice(1));if(!path.startsWith(root+sep))return json(res,{},404);
 const body=await readFile(path);res.writeHead(200,{'content-type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[extname(path)]??'application/octet-stream'});res.end(body);
}catch{json(res,{},500);}}).listen(4174,'127.0.0.1',()=>console.log('Synthetic dashboard fixture: http://127.0.0.1:4174 (no live API access)'));
