import {createHash} from 'node:crypto';
export const replicationHistoryAlpha='11111111-1111-4111-8111-000000000001',replicationHistoryBeta='11111111-1111-4111-8111-000000000002';
export const replicationHistoryBinding={targetId:replicationHistoryAlpha,targetRevision:1,fromUtc:'2026-10-04T10:00:00.123456Z',toUtc:'2026-10-04T11:00:00.123456Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
const uuid=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
export const replicationHistoryMicro=value=>{const m=/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value??'');if(!m||m[2]?.[6]&&m[2][6]!=='0'||new Date(Date.parse(m[1]+'Z')).toISOString().slice(0,19)!==m[1])throw Error('Synthetic UTC boundary invalid');return BigInt(Date.parse(m[1]+'Z'))*1000n+BigInt((m[2]??'').slice(0,6).padEnd(6,'0'));};
const stamp=value=>new Date(Number(value/1000000n)*1000).toISOString().slice(0,19)+'.'+(value%1000000n).toString().padStart(6,'0')+'Z';
const context=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${replicationHistoryMicro(b.fromUtc)}|${replicationHistoryMicro(b.toUtc)}`;
const refused=status=>Object.assign(Error('Synthetic retained replication refusal'),{status});
export function replicationHistoryReceipt(index=0,binding=replicationHistoryBinding,settings={}){
 const from=replicationHistoryMicro(binding.fromUtc),to=replicationHistoryMicro(binding.toUtc),at=to-(to-from)/4n-BigInt(index)*((to-from)/40000n||1n),first=index===0;
 const failed=first&&settings.failedLatest,missing=first&&settings.missingLatest,empty=first&&settings.emptyLatest,partial=first&&settings.partial,sourcePartial=first&&settings.sourcePartial,skew=first&&settings.clockSkew;
 const stored=failed||missing||empty?0:settings.rowCount??3;
 return {runId:uuid(`synthetic-replication|${binding.targetId.toLowerCase()}|${from}|${to}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(at),outcome:failed?'permission_denied':partial?'partial':'succeeded',lossKind:partial?'source_row_limit':'none',minimumLostItems:partial?1:0,expectedRowCount:missing?3:stored,storedRowCount:stored,monitorObservedAtUtc:stored?stamp(at-(skew?360000000n:2000000n)):null,state:failed||missing?'unavailable':partial||sourcePartial||skew?'partial':'available',reason:failed?'collection_failed':missing?'payload_missing':partial?'collection_loss':sourcePartial?'source_partial':skew?'source_clock_skew':empty?'observed_empty':'observed_rows'};
}
export function replicationHistoryRows(receipt,settings={}){
 const partial=receipt.reason==='source_partial';
 return Array.from({length:receipt.storedRowCount},(_,i)=>({rowIdentity:hash(`synthetic-replication-row|${String(i).padStart(5,'0')}`),subscriptionFingerprint:i===1?null:hash(`synthetic-subscription|${i}`),monitorObservedAtUtc:receipt.monitorObservedAtUtc,collectedAtUtc:stamp(replicationHistoryMicro(receipt.completedAtUtc)-1000000n),role:['distributor','publisher','subscriber'][i%3],reportedStatus:settings.reportedStatus??['healthy','warning','disabled'][i%3],pendingCommands:i===1?null:'2000000000',latencySeconds:i===1?null:12.5,sourceCoverage:partial?'local_summary':'complete',stateAvailable:!partial,topology:null,ratePerSecond:null,lastSuccessAgeSeconds:null})).sort((a,b)=>a.rowIdentity.localeCompare(b.rowIdentity));
}
export function replicationHistoryBackend(options={}){
 const settings={count:3,rowCount:3,epoch:1,unboundRows:0,...options},known=new Map(),tokens=new Map();
 function receipts(binding){const all=Array.from({length:settings.count},(_,i)=>replicationHistoryReceipt(i,binding,settings));for(const receipt of all)known.set(receipt.runId,{receipt,binding});return all;}
 const header=(b,epoch,cutoffUtc=stamp(replicationHistoryMicro(b.toUtc)))=>({...b,cutoffUtc,readEpoch:epoch,repositoryTimeUtc:stamp(replicationHistoryMicro(cutoffUtc)+1000000n),statusQualification:'legacy_unqualified',observationTimeBasis:'monitor_capture'});
 function issue(kind,b,selection,limit,offset,cutoff,epoch){const value=Buffer.from(`Synthetic protected replication ${kind}|${context(b)}|${JSON.stringify(selection??null)}|${limit}|${offset}|${epoch}`).toString('base64');tokens.set(value,{kind,context:context(b),selection,limit,offset,cutoff,epoch});return value;}
 function resume(cursor,kind,b,selection,limit,epoch){const old=tokens.get(cursor);if(!old||old.kind!==kind||old.context!==context(b)||old.limit!==limit||JSON.stringify(old.selection)!==JSON.stringify(selection))throw refused(400);if(settings.expired||old.epoch!==epoch)throw refused(409);return old;}
 function read(binding=replicationHistoryBinding,cursor,limit=100){
  if(settings.count>20480||settings.count*settings.rowCount+settings.unboundRows>250000)throw refused(422);
  const all=receipts(binding),epoch=hash(`${settings.epoch}|${context(binding)}|${JSON.stringify(all)}|${settings.unboundRows}`),old=cursor?resume(cursor,'list',binding,undefined,limit,epoch):undefined,offset=old?.offset??0,cutoff=old?.cutoff??stamp(replicationHistoryMicro(binding.toUtc)),complete=all.filter(r=>r.state==='available').length,boundary=all.some(r=>r.sampleTargetRevision!==binding.targetRevision);
  const coverage={runCount:all.length,completeRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>!['succeeded','partial'].includes(r.outcome)).length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>r.expectedRowCount>r.storedRowCount).length,emptyRunCount:all.filter(r=>r.reason==='observed_empty').length,retainedRowCount:all.reduce((n,r)=>n+r.storedRowCount,0),unboundSourceRowCount:settings.unboundRows,hasVisibilityGap:complete!==all.length||boundary||settings.unboundRows>0};
  return {...header(binding,epoch,cutoff),state:!all.length?'unavailable':coverage.hasVisibilityGap?'partial':'available',reason:settings.unboundRows?'source_unbound':boundary?'revision_boundary':!all.length?'no_completed_runs':coverage.hasVisibilityGap?'visibility_gaps':'retained_window',coverage,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('list',binding,undefined,limit,offset+limit,cutoff,epoch):null};
 }
 function snapshot(binding,selection,cursor,limit=100){
  if(!known.has(selection.runId))receipts(binding);const stored=known.get(selection.runId)?.receipt,outside=stored&&(replicationHistoryMicro(stored.completedAtUtc)<replicationHistoryMicro(binding.fromUtc)||replicationHistoryMicro(stored.completedAtUtc)>=replicationHistoryMicro(binding.toUtc)),receipt=!settings.detailAbsent&&!outside&&stored?.sampleTargetRevision===selection.sampleTargetRevision?stored:null;
  const all=receipt?replicationHistoryRows(receipt,settings):[];if(all.length>2048)throw refused(422);const epoch=hash(`${settings.epoch}|${context(binding)}|${JSON.stringify(selection)}|${JSON.stringify(receipt)}|${JSON.stringify(all)}`),old=cursor?resume(cursor,'detail',binding,selection,limit,epoch):undefined,offset=old?.offset??0,cutoff=old?.cutoff??stamp(replicationHistoryMicro(binding.toUtc));
  return {...header(binding,epoch,cutoff),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'snapshot_outside_window':'snapshot_not_retained'),receipt,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('detail',binding,selection,limit,offset+limit,cutoff,epoch):null};
 }
 return {settings,read,snapshot,tokens};
}
export function replicationHistoryReadBinding(url){const m=/^\/api\/v1\/observation-targets\/([^/]+)\/replication\/history(?:\/([^/]+))?$/.exec(url.pathname);if(!m)return undefined;return {binding:{targetId:m[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},selection:m[2]?{runId:m[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined,cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??25),url};}
export const replicationHistoryJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(body)});
export function replicationHistoryHeld(){let start,release,end;const started=new Promise(r=>start=r),gate=new Promise(r=>release=r),done=new Promise(r=>end=r);return {started,async finish(){release();await done;},async handle(route,body,status=200){start(route.request());await gate;try{await replicationHistoryJson(route,body,status);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{end();}return true;}};}
function closedQuery(url,allowed){return [...url.searchParams.keys()].every(k=>allowed.includes(k)&&url.searchParams.getAll(k).length===1);}
function appReadAllowed(url){
 if(url.pathname==='/api/v1/me')return closedQuery(url,['targetId'])&&(!url.searchParams.has('targetId')||[replicationHistoryAlpha,replicationHistoryBeta].includes(url.searchParams.get('targetId')));
 if(url.pathname==='/api/v1/observation-targets')return closedQuery(url,['limit','cursor'])&&url.searchParams.get('limit')==='50';
 if(url.pathname==='/api/v1/overview'){try{return closedQuery(url,['targetId','fromUtc','toUtc','useLatestStatus'])&&url.searchParams.get('useLatestStatus')==='true'&&(!url.searchParams.has('targetId')||[replicationHistoryAlpha,replicationHistoryBeta].includes(url.searchParams.get('targetId')))&&replicationHistoryMicro(url.searchParams.get('toUtc'))>replicationHistoryMicro(url.searchParams.get('fromUtc'));}catch{return false;}}
 const m=/^\/api\/v1\/observation-targets\/([^/]+)(?:\/(.+))?$/.exec(url.pathname);if(!m||![replicationHistoryAlpha,replicationHistoryBeta].includes(m[1]))return false;
 if(!m[2])return closedQuery(url,[]);
 const limits={'backups':'50','availability-groups/replicas':'50','availability-groups/databases':'50','health/databases':'25'};
 return Object.hasOwn(limits,m[2])&&closedQuery(url,['limit','cursor'])&&url.searchParams.get('limit')===limits[m[2]];
}
function legacyNoData(url){
 const m=/^\/api\/v1\/observation-targets\/([^/]+)\/(backups|availability-groups\/(?:replicas|databases))$/.exec(url.pathname);
 if(!m)return undefined;
 // These three independently mounted legacy projections have no source in this
 // scenario. Register native NoData explicitly; a generic server404 is not data.
 if(![replicationHistoryAlpha,replicationHistoryBeta].includes(m[1])||!closedQuery(url,['limit'])||url.searchParams.get('limit')!=='50')throw refused(400);
 return {targetId:m[1],targetRevision:1,runId:null,evidence:null,observedAtUtc:'2026-10-04T12:00:00.000000Z',state:'NoData',items:[],hasMore:false,nextCursor:null,truncated:false,...(m[2].startsWith('availability-groups/')?{visibilityScope:'PrimaryAllKnown'}:{})};
}
export async function installReplicationHistoryFixture(page,{origin='http://127.0.0.1:4328',allowOtherApi=false,backend=replicationHistoryBackend()}={}){
 const session={backend,reads:[],errors:[],handler:undefined};
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url()),reject=()=>{session.errors.push(`${request.method()} ${url.pathname}`);return route.abort();};if(url.origin!==origin)return reject();if(!url.pathname.startsWith('/api/'))return route.continue();const read=replicationHistoryReadBinding(url);
  if(!read){
   if(!allowOtherApi||request.method()!=='GET'||!appReadAllowed(url))return reject();
   try{const body=legacyNoData(url);return body?replicationHistoryJson(route,body):route.continue();}catch{return reject();}
  }
  if(request.method()!=='GET'||![replicationHistoryAlpha,replicationHistoryBeta].includes(read.binding.targetId)||read.limit!==100||!Number.isSafeInteger(read.binding.targetRevision)||read.binding.targetRevision<1||read.selection&&(!/^[a-f0-9-]{36}$/u.test(read.selection.runId)||!Number.isSafeInteger(read.selection.sampleTargetRevision)||read.selection.sampleTargetRevision<1)||[...url.searchParams.keys()].some(k=>!['targetRevision','sampleTargetRevision','fromUtc','toUtc','limit','cursor'].includes(k)||url.searchParams.getAll(k).length!==1)||replicationHistoryMicro(read.binding.toUtc)<=replicationHistoryMicro(read.binding.fromUtc)||replicationHistoryMicro(read.binding.toUtc)-replicationHistoryMicro(read.binding.fromUtc)>7n*86400000000n)return reject();
  session.reads.push(read);if(session.handler&&await session.handler(route,read,session))return;try{return replicationHistoryJson(route,read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit));}catch(error){return replicationHistoryJson(route,{code:'synthetic_replication_refusal'},error.status??400);}
 });page.on('pageerror',error=>session.errors.push(error.message));page.on('response',response=>{const url=new URL(response.url());if(url.origin===origin&&url.pathname.startsWith('/api/')&&response.status()===404)session.errors.push('Unregistered API '+url.pathname);});return session;
}
const populated=replicationHistoryBackend(),empty=replicationHistoryBackend({count:0});
export function handleReplicationWindowHistoryFixture(url,request){const read=replicationHistoryReadBinding(url);if(!read)return null;const backend=/(?:^|;\s*)replicationHistoryFixture=1(?:;|$)/u.test(request?.headers?.cookie??'')?populated:empty;try{return{status:200,body:read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit)}}catch(error){return{status:error.status??400,body:{code:'synthetic_replication_refusal'}};}}
