import {createHash} from 'node:crypto';

export const sqlVolumePlanningAlpha='11111111-1111-4111-8111-000000000001',sqlVolumePlanningBeta='11111111-1111-4111-8111-000000000002';
export const sqlVolumePlanningBinding={targetId:sqlVolumePlanningAlpha,targetRevision:1,fromUtc:'2026-10-04T10:00:00.123456Z',toUtc:'2026-10-04T11:00:00.123456Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
const uuid=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
const micro=value=>{const match=/^(.*?)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!match||match[2]?.[6]&&match[2][6]!=='0')throw Error('Invalid fixture UTC');return BigInt(Date.parse(match[1]+'Z'))*1000n+BigInt((match[2]??'').slice(0,6).padEnd(6,'0'));};
const stamp=value=>new Date(Number(value/1000000n)*1000).toISOString().slice(0,19)+'.'+(value%1000000n).toString().padStart(6,'0')+'Z';
const context=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${micro(b.fromUtc)}|${micro(b.toUtc)}`;
const refused=status=>Object.assign(Error('Synthetic bounded volume planning refusal'),{status});
export function sqlVolumePlanningReceipt(index=0,binding=sqlVolumePlanningBinding,settings={}){
 const from=micro(binding.fromUtc),to=micro(binding.toUtc),at=to-(to-from)/4n-BigInt(index)*((to-from)/20000n||1n);
 const latest=index===0,failed=latest&&settings.failedLatest,missing=latest&&settings.missingLatest,empty=latest&&settings.emptyLatest,partial=latest&&settings.partial,unknown=latest&&settings.unknownCapacity;
 const stored=failed||missing||empty?0:settings.volumeCount??3;
 return {runId:uuid(`${binding.targetId.toLowerCase()}|${from}|${to}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(at),outcome:failed?'permission_denied':partial?'partial':'succeeded',lossKind:partial?'source_row_limit':'none',minimumLostItems:partial?1:0,expectedVolumeCount:missing?3:stored,storedVolumeCount:stored,state:failed||missing?'unavailable':partial||unknown?'partial':'available',reason:failed?'collection_failed':missing?'payload_missing':partial?'collection_loss':unknown?'capacity_unknown':empty?'observed_empty':'observed_volumes'};
}
export function sqlVolumePlanningVolumes(receipt,settings={}){
 const end=micro(receipt.completedAtUtc),total='9223372036854775807',available=9007199254740993n,slope=3002399751580331n;
 return Array.from({length:receipt.storedVolumeCount},(_,index)=>{
  let state=settings.forecastState??(index===1?'not_depleting':index===2?'insufficient_history':'estimated');
  const unknown=settings.unknownCapacity&&index===0,identityKind=settings.mountIdentity&&index===0?'mount_point':'volume_id';
  if(unknown)state='capacity_unknown';else if(identityKind!=='volume_id')state='identity_not_comparable';
  const sampled=['estimated','full','not_depleting','unstable_trend','beyond_horizon'].includes(state);
  const current=state==='full'?0n:available;
  const dailySamples=sampled?Array.from({length:5},(_,day)=>({runId:day===4?receipt.runId:uuid(`${receipt.runId}|daily|${day}`),completedAtUtc:stamp(end-BigInt(4-day)*86400000000n),totalBytes:total,availableBytes:(state==='estimated'?current+BigInt(4-day)*slope:state==='beyond_horizon'?current+BigInt(4-day):state==='unstable_trend'?current+BigInt([4,0,3,2,0][day])*slope:current).toString()})):[];
  return {volumeKey:hash(`synthetic-volume-key|${String(index).padStart(4,'0')}`),identityKind,mappedFileCount:1,observedAtUtc:stamp(end-2000000n),collectedAtUtc:stamp(end-1000000n),totalBytes:unknown?null:total,availableBytes:unknown?null:current.toString(),forecast:{state,sampleDays:dailySamples.length,daysUntilFull:state==='estimated'?3:state==='full'?0:null,dailySamples}};
 }).sort((a,b)=>a.volumeKey.localeCompare(b.volumeKey));
}
export function sqlVolumePlanningBackend(options={}){
 const settings={count:3,volumeCount:3,epoch:1,...options},known=new Map(),tokens=new Map();
 const header=(b,epoch,cutoffUtc=stamp(micro(b.toUtc)))=>({...b,cutoffUtc,readEpoch:epoch,repositoryTimeUtc:stamp(micro(cutoffUtc)+1000000n)});
 function allReceipts(binding){const all=Array.from({length:settings.count},(_,index)=>sqlVolumePlanningReceipt(index,binding,settings));for(const receipt of all)known.set(receipt.runId,{receipt,binding});return all;}
 function issue(kind,binding,selection,limit,offset,cutoff,epoch){const token=Buffer.from(`Synthetic protected planning ${kind}|${context(binding)}|${JSON.stringify(selection??null)}|${limit}|${offset}|${epoch}`).toString('base64');tokens.set(token,{kind,context:context(binding),selection,limit,offset,cutoff,epoch});return token;}
 function continuation(token,kind,binding,selection,limit,epoch){const old=tokens.get(token);if(!old||old.kind!==kind||old.context!==context(binding)||old.limit!==limit||JSON.stringify(old.selection)!==JSON.stringify(selection))throw refused(400);if(settings.expired||old.epoch!==epoch)throw refused(409);return old;}
 function coverage(all){const complete=all.filter(r=>r.state==='available').length;return {runCount:all.length,completeRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>!['succeeded','partial'].includes(r.outcome)).length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>r.expectedVolumeCount>r.storedVolumeCount).length,emptyRunCount:all.filter(r=>r.reason==='observed_empty').length,retainedVolumeCount:all.reduce((n,r)=>n+r.storedVolumeCount,0),hasVisibilityGap:complete!==all.length};}
 function read(binding=sqlVolumePlanningBinding,cursor,limit=100){
  if(settings.count>10000||settings.count*settings.volumeCount>250000)throw refused(422);
  const all=allReceipts(binding),epoch=hash(`${settings.epoch}|${context(binding)}|${JSON.stringify(all)}`),old=cursor?continuation(cursor,'list',binding,undefined,limit,epoch):undefined,offset=old?.offset??0,cutoff=old?.cutoff??stamp(micro(binding.toUtc)),cov=coverage(all);
  const boundary=settings.revisionBoundary||all.some(r=>r.sampleTargetRevision!==binding.targetRevision);cov.hasVisibilityGap ||= Boolean(boundary);
  return {...header(binding,epoch,cutoff),state:!all.length?'unavailable':cov.hasVisibilityGap?'partial':'available',reason:!all.length?'no_completed_runs':boundary?'revision_boundary':cov.hasVisibilityGap?'visibility_gaps':'retained_window',coverage:cov,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('list',binding,undefined,limit,offset+limit,cutoff,epoch):null};
 }
 function snapshot(binding,selection,cursor,limit=100){
  if(!known.has(selection.runId))allReceipts(binding);
  const source=known.get(selection.runId)?.receipt,outside=source&&(micro(source.completedAtUtc)<micro(binding.fromUtc)||micro(source.completedAtUtc)>=micro(binding.toUtc));
  const receipt=!settings.detailAbsent&&!outside&&source?.sampleTargetRevision===selection.sampleTargetRevision?source:null;
  const all=receipt?sqlVolumePlanningVolumes(receipt,settings):[];if(all.length>1000)throw refused(422);
  const qualified=receipt?.state==='available',lookbackGap=['visibility_gaps','revision_boundary'].includes(settings.forecastState);
  const lookback=receipt?{fromUtc:stamp(micro(receipt.completedAtUtc)-14n*86400000000n),asOfUtc:receipt.completedAtUtc,state:settings.lookbackOverflow?'unavailable':!qualified||lookbackGap?'partial':'available',reason:settings.lookbackOverflow?'lookback_overflow':!qualified?'selected_receipt_unavailable':lookbackGap?settings.forecastState:'retained_lookback',coverage:settings.lookbackOverflow?null:{runCount:5,completeRunCount:qualified&&!lookbackGap?5:4,gapRunCount:qualified&&!lookbackGap?0:1,failedRunCount:receipt.reason==='collection_failed'?1:0,lostRunCount:receipt.lossKind!=='none'?1:0,missingPayloadRunCount:receipt.expectedVolumeCount>receipt.storedVolumeCount?1:0,emptyRunCount:receipt.reason==='observed_empty'?1:0,retainedVolumeCount:4*settings.volumeCount+receipt.storedVolumeCount,hasVisibilityGap:!qualified||lookbackGap}}:null;
  if(!qualified)for(const volume of all)volume.forecast={state:'selected_receipt_unavailable',sampleDays:0,daysUntilFull:null,dailySamples:[]};
  if(settings.lookbackOverflow)for(const volume of all)volume.forecast={state:'lookback_overflow',sampleDays:0,daysUntilFull:null,dailySamples:[]};
  const epoch=hash(`${settings.epoch}|${context(binding)}|${JSON.stringify(selection)}|${JSON.stringify(receipt)}|${JSON.stringify(all)}|${JSON.stringify(lookback)}`),old=cursor?continuation(cursor,'snapshot',binding,selection,limit,epoch):undefined,offset=old?.offset??0,cutoff=old?.cutoff??stamp(micro(binding.toUtc));
  return {...header(binding,epoch,cutoff),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'snapshot_outside_window':'snapshot_not_retained'),receipt,lookback,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('snapshot',binding,selection,limit,offset+limit,cutoff,epoch):null};
 }
 return {settings,read,snapshot,tokens};
}
export function sqlVolumePlanningReadBinding(url){
 const match=/^\/api\/v1\/observation-targets\/([^/]+)\/resources\/volume-planning\/history(?:\/([^/]+))?$/.exec(url.pathname);if(!match)return undefined;
 return {binding:{targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},selection:match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined,cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??25),url};
}
export const sqlVolumePlanningJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(body)});
export function sqlVolumePlanningHeld(){let start,release,end;const started=new Promise(resolve=>start=resolve),gate=new Promise(resolve=>release=resolve),done=new Promise(resolve=>end=resolve);return {started,async finish(){release();await done;},async handle(route,body,status=200){start(route.request());await gate;try{await sqlVolumePlanningJson(route,body,status);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{end();}return true;}};}
export async function installSqlVolumePlanningFixture(page,{origin='http://127.0.0.1:4328',allowOtherApi=false,backend=sqlVolumePlanningBackend()}={}){
 const session={backend,reads:[],errors:[],handler:undefined};
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url());const reject=()=>{session.errors.push(`${request.method()} ${url.pathname}`);return route.abort();};if(url.origin!==origin)return reject();if(!url.pathname.startsWith('/api/'))return route.continue();const read=sqlVolumePlanningReadBinding(url);if(!read){const global=['/api/v1/me','/api/v1/overview','/api/v1/observation-targets'].includes(url.pathname),match=/^\/api\/v1\/observation-targets\/([^/]+)(?:\/(.+))?$/.exec(url.pathname),source=match?.[2],allowed=match&&[sqlVolumePlanningAlpha,sqlVolumePlanningBeta].includes(match[1])&&(!source||['health/databases','health/files','analytics/baselines','analytics/sql-baselines','resources/buffer-nodes/ple'].includes(source)||/^resources\/(?:files\/history(?:\/[a-f0-9-]+)?|cpu\/captures(?:\/[a-f0-9-]+)?|volumes\/(?:history|[a-f0-9]{64}\/history))$/.test(source));return allowOtherApi&&request.method()==='GET'&&(global||allowed)?route.continue():reject();}
  if(request.method()!=='GET'||![sqlVolumePlanningAlpha,sqlVolumePlanningBeta].includes(read.binding.targetId)||read.limit!==100||[...url.searchParams.keys()].some(key=>!['targetRevision','sampleTargetRevision','fromUtc','toUtc','limit','cursor'].includes(key)||url.searchParams.getAll(key).length!==1)||!Number.isSafeInteger(read.binding.targetRevision)||read.binding.targetRevision<1||micro(read.binding.toUtc)<=micro(read.binding.fromUtc)||micro(read.binding.toUtc)-micro(read.binding.fromUtc)>31n*86400000000n)return reject();
  session.reads.push(read);if(session.handler&&await session.handler(route,read,session))return;try{return sqlVolumePlanningJson(route,read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit));}catch(error){return sqlVolumePlanningJson(route,{code:'synthetic_planning_refused'},error.status??400);}
 });page.on('pageerror',error=>session.errors.push(error.message));page.on('response',response=>{const url=new URL(response.url());if(url.origin===origin&&url.pathname.startsWith('/api/')&&response.status()===404)session.errors.push('Unregistered API '+url.pathname);});return session;
}
const populated=sqlVolumePlanningBackend(),empty=sqlVolumePlanningBackend({count:0});
export function handleSqlVolumePlanningFixture(url,request){const read=sqlVolumePlanningReadBinding(url);if(!read)return null;const backend=/(?:^|;\s*)sqlVolumePlanningFixture=1(?:;|$)/.test(request?.headers?.cookie??'')?populated:empty;try{return {status:200,body:read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit)};}catch(error){return {status:error.status??400,body:{code:'synthetic_planning_refused'}};}}
