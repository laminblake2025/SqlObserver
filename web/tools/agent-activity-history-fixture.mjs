import {createHash} from 'node:crypto';

export const agentActivityHistoryAlpha='11111111-1111-4111-8111-000000000001',agentActivityHistoryBeta='11111111-1111-4111-8111-000000000002';
export const agentActivityHistoryBinding={targetId:agentActivityHistoryAlpha,targetRevision:1,fromUtc:'2026-10-02T10:00:00.1234560Z',toUtc:'2026-10-02T11:00:00.1234560Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
const flags={productionAdmitted:false,serviceSessionAssociationQualified:false,nativeQualification:'Absent'};
export const agentActivityHistoryTicks=value=>{const m=/^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!m)throw Error('fixture clock');return BigInt(Date.parse(m[1]+'Z'))*10000n+BigInt((m[2]??'').padEnd(7,'0'));};
const stamp=value=>new Date(Number(value/10000000n)*1000).toISOString().slice(0,19)+'.'+(value%10000000n).toString().padStart(7,'0')+'Z';
const context=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${agentActivityHistoryTicks(b.fromUtc)}|${agentActivityHistoryTicks(b.toUtc)}`;
const id=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
export function agentActivityHistoryReceipt(index=0,binding=agentActivityHistoryBinding,settings={}){
 const failed=Boolean(settings.failedLatest&&index===0),missing=Boolean(settings.missingLatest&&index===0),lost=Boolean(settings.truncatedLatest&&index===0),receiptRetained=!(settings.retiredLatest&&index===0),capturePresent=!failed&&!missing&&receiptRetained;
 const start=agentActivityHistoryTicks(binding.fromUtc),end=agentActivityHistoryTicks(binding.toUtc),step=(end-start)/20000n/10n*10n||10n;
 const completion=(end-(end-start)/4n-BigInt(index)*step)/10n*10n;
 return{runId:id(`${binding.targetId.toLowerCase()}|${start}|${end}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(completion),outcome:failed?'permission_denied':lost?'partial':'succeeded',canonicalReason:failed?'required_permission_missing':lost?'source_row_limit':'completed',lossKind:lost?'source_row_limit':'none',minimumLostItems:lost?1:0,countIsExact:!lost,receiptRetained,capturePresent,sourceRowsRead:capturePresent?(lost?513:settings.jobs??12):null,retainedJobCount:capturePresent?(lost?512:settings.jobs??12):null,truncated:capturePresent?lost:null,state:capturePresent?(lost?'partial':'available'):'unavailable',reason:capturePresent?(lost?'source_truncated':'source_complete'):failed?'collection_failed':'payload_missing'};
}
export function agentActivityHistoryCapture(receipt,binding=agentActivityHistoryBinding,settings={}){
 const at=agentActivityHistoryTicks(receipt.completedAtUtc)-20000000n+1n,local='2026-11-01T01:30:00.1234567',running=Boolean(settings.serviceRunning);
 const header=delta=>({sourceObservedAtUtc:stamp(at+delta),service:{matchingServiceCount:1,status:running?4:1,processId:running?1234:0,startupAtUtc:running?stamp(at-600000000n):null},session:{sessionId:9,agentStartLocal:'2026-11-01T01:00:00.0000000'}});
 return{sourceSchemaVersion:1,enrollmentRevision:7,before:header(0n),after:header(10000000n),jobs:Array.from({length:receipt.retainedJobCount},(_,i)=>({jobId:`22222222-2222-4222-8222-${(i+1).toString(16).padStart(12,'0')}`,sessionId:9,requestedLocal:local,queuedLocal:null,startExecutionLocal:local,stopExecutionLocal:settings.localReversal?'2026-11-01T00:30:00.1234567':null,lastExecutedStepId:2,lastExecutedStepLocal:local,candidateState:receipt.truncated||!running?'Unknown':settings.localReversal?'Stopped':'Running'})),sourceRowsRead:receipt.sourceRowsRead,truncated:receipt.truncated,gap:receipt.truncated?'SourceTruncated':running?'None':'ServiceNotRunning',...flags};
}
export function agentActivityHistoryBackend(options={}){
 const settings={count:3,epoch:1,jobs:12,...options},tokens=new Map(),known=new Map();
 const header=(binding,epoch,cutoff=stamp(agentActivityHistoryTicks(binding.toUtc)))=>({...binding,cutoffUtc:cutoff,readEpoch:hash(`${epoch}|${context(binding)}`),repositoryTimeUtc:stamp(agentActivityHistoryTicks(cutoff)+10000000n),...flags});
 function read(binding=agentActivityHistoryBinding,cursor,limit=100){
  if(settings.count>10000)throw Object.assign(Error('window overflow'),{status:422});
  let offset=0,cutoff=stamp(agentActivityHistoryTicks(binding.toUtc));if(cursor){const prior=tokens.get(cursor);if(!prior||prior.context!==context(binding)||prior.limit!==limit)throw Object.assign(Error('cursor'),{status:400});if(prior.epoch!==settings.epoch)throw Object.assign(Error('epoch'),{status:409});offset=prior.offset;cutoff=prior.cutoff;}
  const all=Array.from({length:settings.count},(_,i)=>agentActivityHistoryReceipt(i,binding,settings));for(const receipt of all)known.set(receipt.runId,{receipt,binding});
  const complete=all.filter(r=>r.state==='available').length,coverage={runCount:all.length,retainedReceiptCount:all.filter(r=>r.receiptRetained).length,retainedCaptureCount:all.filter(r=>r.capturePresent).length,completeSourceRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>!['succeeded','partial'].includes(r.outcome)).length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>['succeeded','partial'].includes(r.outcome)&&!r.capturePresent).length,hasVisibilityGap:all.length!==complete||Boolean(settings.revisionBoundary)};
  let nextCursor=null;if(offset+limit<all.length){nextCursor=Buffer.from(`synthetic protected Agent activity cursor ${context(binding)} ${settings.epoch} ${offset+limit} ${limit}`).toString('base64');tokens.set(nextCursor,{context:context(binding),epoch:settings.epoch,offset:offset+limit,limit,cutoff});}
  return{...header(binding,settings.epoch,cutoff),state:all.length?coverage.hasVisibilityGap?'partial':'available':'unavailable',reason:all.length?settings.revisionBoundary?'revision_boundary':coverage.hasVisibilityGap?'visibility_gaps':'retained_observations':'no_completed_runs',coverage,items:all.slice(offset,offset+limit),nextCursor};
 }
 function detail(binding,selection){
  let source=known.get(selection.runId);if(source&&source.binding.targetId.toLowerCase()!==binding.targetId.toLowerCase())source=undefined;
  if(!source)for(let i=0;i<Math.min(settings.count,10001);i++){const receipt=agentActivityHistoryReceipt(i,binding,settings);if(receipt.runId===selection.runId){source={receipt,binding};known.set(receipt.runId,source);break;}}
  let receipt=source?.receipt;const outside=receipt&&(agentActivityHistoryTicks(receipt.completedAtUtc)<agentActivityHistoryTicks(binding.fromUtc)||agentActivityHistoryTicks(receipt.completedAtUtc)>=agentActivityHistoryTicks(binding.toUtc));
  if(settings.detailAbsent||outside||receipt?.sampleTargetRevision!==selection.sampleTargetRevision)receipt=undefined;
  return{...header(binding,`${selection.runId}|${JSON.stringify(receipt??null)}`),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'observation_outside_window':'observation_not_retained'),receipt:receipt??null,capture:receipt?.capturePresent?agentActivityHistoryCapture(receipt,binding,settings):null};
 }
 return{settings,tokens,read,detail};
}
export function agentActivityHistoryReadBinding(url){const match=/^\/api\/v1\/observation-targets\/([a-f0-9-]+)\/sql-agent\/activity\/observations(?:\/([a-f0-9-]+))?$/.exec(url.pathname);if(!match)return undefined;return{binding:{targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??100),selection:match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined};}
export const agentActivityHistoryJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
export const agentActivityHistoryHeld=()=>{let start,finish;const started=new Promise(resolve=>start=resolve),done=new Promise(resolve=>finish=resolve);return{started,finish,async handle(route,body,status=200){start(route.request());await done;try{await agentActivityHistoryJson(route,body,status);}catch{}return true;}};};
const closedQuery=(url,allowed)=>[...url.searchParams.keys()].every(key=>allowed.includes(key)&&url.searchParams.getAll(key).length===1);
function appReadAllowed(url){
 const targets=[agentActivityHistoryAlpha,agentActivityHistoryBeta];
 if(url.pathname==='/api/v1/me')return closedQuery(url,['targetId'])&&(!url.searchParams.has('targetId')||targets.includes(url.searchParams.get('targetId')));
 if(url.pathname==='/api/v1/observation-targets')return closedQuery(url,['limit','cursor'])&&url.searchParams.get('limit')==='50';
 if(url.pathname==='/api/v1/overview'){try{return closedQuery(url,['targetId','fromUtc','toUtc','useLatestStatus'])&&url.searchParams.get('useLatestStatus')==='true'&&(!url.searchParams.has('targetId')||targets.includes(url.searchParams.get('targetId')))&&agentActivityHistoryTicks(url.searchParams.get('toUtc'))>agentActivityHistoryTicks(url.searchParams.get('fromUtc'));}catch{return false;}}
 const m=/^\/api\/v1\/observation-targets\/([^/]+)(?:\/(.+))?$/.exec(url.pathname);if(!m||!targets.includes(m[1]))return false;
 if(!m[2])return closedQuery(url,[]);
 const limits={'backups':'50','availability-groups/replicas':'50','availability-groups/databases':'50','health/databases':'25'};
 if(Object.hasOwn(limits,m[2]))return closedQuery(url,['limit','cursor'])&&url.searchParams.get('limit')===limits[m[2]];
 if(m[2]==='sql-agent/failures'){try{return closedQuery(url,['limit','cursor','fromUtc','toUtc'])&&url.searchParams.get('limit')==='50'&&agentActivityHistoryTicks(url.searchParams.get('toUtc'))>agentActivityHistoryTicks(url.searchParams.get('fromUtc'))&&agentActivityHistoryTicks(url.searchParams.get('toUtc'))-agentActivityHistoryTicks(url.searchParams.get('fromUtc'))<=7n*86400n*10000000n;}catch{return false;}}
 return false;
}
function legacyNoData(url){
 const m=/^\/api\/v1\/observation-targets\/([^/]+)\/(backups|availability-groups\/(?:replicas|databases))$/.exec(url.pathname);
 if(!m)return undefined;
 return{targetId:m[1],targetRevision:1,runId:null,evidence:null,observedAtUtc:'2026-10-04T12:00:00.000000Z',state:'NoData',items:[],hasMore:false,nextCursor:null,truncated:false,...(m[2].startsWith('availability-groups/')?{visibilityScope:'PrimaryAllKnown'}:{})};
}
export async function installAgentActivityHistoryFixture(page,options={}){
 const backend=agentActivityHistoryBackend(options),reads=[],errors=[],origin=options.origin??'http://127.0.0.1:4328';let handler;
 const session={backend,reads,errors,get handler(){return handler;},set handler(value){handler=value;}};
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url()),parsed=agentActivityHistoryReadBinding(url);
  if(url.origin===origin&&!url.pathname.startsWith('/api/')){await route.continue();return;}
  if(url.origin===origin&&parsed&&request.method()==='GET'&&[agentActivityHistoryAlpha,agentActivityHistoryBeta].includes(parsed.binding.targetId)&&[1,2].includes(parsed.binding.targetRevision)){
   try{const allowed=parsed.selection?['targetRevision','fromUtc','toUtc','sampleTargetRevision']:['targetRevision','fromUtc','toUtc','limit','cursor'];const from=agentActivityHistoryTicks(parsed.binding.fromUtc),to=agentActivityHistoryTicks(parsed.binding.toUtc);if(!closedQuery(url,allowed)||from%10n!==0n||to%10n!==0n||to<=from||to-from>31n*86400n*10000000n||(!parsed.selection&&parsed.limit!==100)||parsed.selection&&(!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(parsed.selection.runId)||!Number.isSafeInteger(parsed.selection.sampleTargetRevision)||parsed.selection.sampleTargetRevision<1||parsed.selection.sampleTargetRevision>parsed.binding.targetRevision))throw Error('invalid request');}catch{errors.push(`${request.method()} ${url.pathname} invalid query`);await route.abort();return;}
   reads.push(parsed);if(handler&&await handler(route,parsed))return;
   try{await agentActivityHistoryJson(route,parsed.selection?backend.detail(parsed.binding,parsed.selection):backend.read(parsed.binding,parsed.cursor,parsed.limit));}catch(error){await agentActivityHistoryJson(route,{code:'synthetic_agent_history_refused'},error.status??400);}return;
  }
  if(options.allowOtherApi&&url.origin===origin&&!parsed&&request.method()==='GET'&&appReadAllowed(url)){const body=legacyNoData(url);if(body)await agentActivityHistoryJson(route,body);else await route.continue();return;}
  errors.push(`${request.method()} ${url.pathname}`);await route.abort();
 });
 page.on('request',r=>{if(!r.url().startsWith(origin+'/')&&!r.url().startsWith('data:'))errors.push('external '+r.url());});
 page.on('pageerror',error=>errors.push(error.message));return session;
}
