import {createHash} from 'node:crypto';

export const tempDbHistoryAlpha='11111111-1111-4111-8111-000000000001';
export const tempDbHistoryBeta='11111111-1111-4111-8111-000000000002';
export const tempDbHistoryBinding={targetId:tempDbHistoryAlpha,targetRevision:1,fromUtc:'2026-10-04T10:00:00.123456Z',toUtc:'2026-10-04T11:00:00.123456Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
const id=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
const micro=value=>{const m=/^(.*?)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!m)throw Error('fixture clock');return BigInt(Date.parse(m[1]+'Z'))*1000n+BigInt((m[2]??'').slice(0,6).padEnd(6,'0'));};
const stamp=value=>new Date(Number(value/1000000n)*1000).toISOString().slice(0,19)+'.'+(value%1000000n).toString().padStart(6,'0')+'Z';
const key=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${micro(b.fromUtc)}|${micro(b.toUtc)}`;
const refused=status=>Object.assign(Error('Synthetic bounded TempDB read refused'),{status});

export function tempDbHistoryFiles(count=3,observedAtUtc=tempDbHistoryBinding.fromUtc){
 return Array.from({length:count},(_,index)=>({fileId:index+1,sizeBytes:index===0?'9007199254741999':'1024',usedBytes:index===0?'9007199254740993':'256',freeBytes:index===0?'1006':'768',sourceState:1,observedAtUtc}));
}
export function tempDbHistoryReceipt(index=0,binding=tempDbHistoryBinding,settings={}){
 const from=micro(binding.fromUtc),to=micro(binding.toUtc),completed=to-(to-from)/4n-BigInt(index)*((to-from)/20000n||1n);
 const failed=Boolean(settings.failedLatest&&index===0),missing=Boolean(settings.missingLatest&&index===0),empty=Boolean(settings.emptyLatest&&index===0),partial=Boolean(settings.partial&&index===0),count=failed||missing||empty?0:settings.fileCount??3;
 return {runId:id(`${binding.targetId.toLowerCase()}|${from}|${to}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(completed),outcome:failed?'permission_denied':partial?'partial':'succeeded',lossKind:partial?'source_row_limit':'none',minimumLostItems:partial?1:0,expectedFileCount:missing?3:count,storedFileCount:count,summaryPresent:!failed&&!missing,observedAtUtc:failed||missing?null:stamp(completed-1000000n),state:failed||missing?'unavailable':partial?'partial':'available',reason:failed?'collection_failed':missing?'payload_missing':partial?'collection_loss':empty?'observed_empty':'observed_files'};
}
export function tempDbHistoryBackend(options={}){
 const settings={count:3,fileCount:3,epoch:1,failedLatest:false,missingLatest:false,emptyLatest:false,partial:false,...options},known=new Map(),tokens=new Map();
 const header=(binding,epoch,cutoffUtc=stamp(micro(binding.toUtc)))=>({...binding,cutoffUtc,readEpoch:hash(String(epoch)),repositoryTimeUtc:stamp(micro(cutoffUtc)+1000000n)});
 function receipts(binding){return Array.from({length:settings.count},(_,i)=>tempDbHistoryReceipt(i,binding,settings));}
 function remember(all,binding){for(const receipt of all)known.set(receipt.runId,{receipt,binding});}
 function issue(kind,binding,selection,limit,offset,cutoff,epoch){const token=Buffer.from(`Synthetic protected TempDB ${kind} ${key(binding)} ${JSON.stringify(selection??null)} ${limit} ${offset} ${epoch}`).toString('base64');tokens.set(token,{kind,context:key(binding),selection,limit,offset,cutoff,epoch});return token;}
 function continuation(token,kind,binding,selection,limit,epoch){const prior=tokens.get(token);if(!prior||prior.kind!==kind||prior.context!==key(binding)||prior.limit!==limit||JSON.stringify(prior.selection)!==JSON.stringify(selection))throw refused(400);if(settings.expired||prior.epoch!==epoch)throw refused(409);return prior;}
 function read(binding=tempDbHistoryBinding,cursor,limit=100){
  if(settings.count>10000||settings.fileCount*settings.count>250000)throw refused(422);
  const all=receipts(binding);remember(all,binding);const epoch=hash(`${settings.epoch}|${key(binding)}|${JSON.stringify(all)}`);
  const prior=cursor?continuation(cursor,'list',binding,undefined,limit,epoch):undefined,offset=prior?.offset??0,cutoff=prior?.cutoff??stamp(micro(binding.toUtc));
  const complete=all.filter(r=>r.state==='available').length,boundary=settings.revisionBoundary||all.some(r=>r.sampleTargetRevision!==binding.targetRevision);
  const coverage={runCount:all.length,completeRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>!['succeeded','partial'].includes(r.outcome)).length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>r.outcome==='succeeded'&&(!r.summaryPresent||r.expectedFileCount>r.storedFileCount)).length,emptyRunCount:all.filter(r=>r.reason==='observed_empty').length,retainedFileCount:all.reduce((n,r)=>n+r.storedFileCount,0),hasVisibilityGap:complete!==all.length||Boolean(boundary)};
  return {...header(binding,epoch,cutoff),state:!all.length?'unavailable':coverage.hasVisibilityGap?'partial':'available',reason:!all.length?'no_completed_runs':boundary?'revision_boundary':coverage.hasVisibilityGap?'visibility_gaps':'retained_window',coverage,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('list',binding,undefined,limit,offset+limit,cutoff,epoch):null};
 }
 function snapshot(binding,selection,cursor,limit=100){
  let source=known.get(selection.runId);if(!source){const all=receipts(binding);remember(all,binding);source=known.get(selection.runId);}
  const original=source?.receipt,outside=original&&(micro(original.completedAtUtc)<micro(binding.fromUtc)||micro(original.completedAtUtc)>=micro(binding.toUtc));
  const receipt=(!settings.detailAbsent&&!outside&&original?.sampleTargetRevision===selection.sampleTargetRevision)?original:null;
  const all=receipt?tempDbHistoryFiles(receipt.storedFileCount,receipt.observedAtUtc):[];
  const summary=receipt?.summaryPresent?{observedAtUtc:receipt.observedAtUtc,sourceState:receipt.reason==='collection_loss'?2:1,totalBytes:all.reduce((n,f)=>n+BigInt(f.sizeBytes),0n).toString(),usedBytes:all.reduce((n,f)=>n+BigInt(f.usedBytes),0n).toString(),logTotalBytes:'1024',logUsedBytes:'0',truncated:receipt.reason==='collection_loss'}:null;
  const epoch=hash(`${settings.epoch}|${key(binding)}|${JSON.stringify(selection)}|${JSON.stringify(receipt)}|${JSON.stringify(summary)}|${JSON.stringify(all)}`);
  const prior=cursor?continuation(cursor,'snapshot',binding,selection,limit,epoch):undefined,offset=prior?.offset??0,cutoff=prior?.cutoff??stamp(micro(binding.toUtc));
  if(all.length>10000)throw refused(422);
  return {...header(binding,epoch,cutoff),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'snapshot_outside_window':'snapshot_not_retained'),receipt,summary,items:all.slice(offset,offset+limit),nextCursor:offset+limit<all.length?issue('snapshot',binding,selection,limit,offset+limit,cutoff,epoch):null};
 }
 return {settings,read,snapshot,tokens};
}
export function tempDbHistoryReadBinding(url){
 const match=/^\/api\/v1\/observation-targets\/([^/]+)\/tempdb\/history(?:\/([^/]+))?$/.exec(url.pathname);if(!match)return undefined;
 return {binding:{targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},selection:match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined,cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??25),url};
}
export const tempDbHistoryJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(body)});
export function tempDbHistoryHeld(){let start,release,end;const started=new Promise(r=>start=r),gate=new Promise(r=>release=r),done=new Promise(r=>end=r);return {started,async finish(){release();await done;},async handle(route,body,status=200){start(route.request());await gate;try{await tempDbHistoryJson(route,body,status);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{end();}return true;}};}
export async function installTempDbHistoryFixture(page,{origin='http://127.0.0.1:4328',allowOtherApi=false,backend=tempDbHistoryBackend()}={}){
 const session={backend,reads:[],errors:[],handler:undefined};
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url());
  const reject=()=>{session.errors.push(`${request.method()} ${url.pathname}`);return route.abort();};if(url.origin!==origin)return reject();if(!url.pathname.startsWith('/api/'))return route.continue();
  const read=tempDbHistoryReadBinding(url);if(!read)return allowOtherApi?route.continue():reject();
  if(request.method()!=='GET'||![tempDbHistoryAlpha,tempDbHistoryBeta].includes(read.binding.targetId)||read.limit!==100||[...url.searchParams.keys()].some(k=>!['targetRevision','sampleTargetRevision','fromUtc','toUtc','limit','cursor'].includes(k)))return reject();
  session.reads.push(read);if(session.handler&&await session.handler(route,read))return;
  try{return tempDbHistoryJson(route,read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit));}catch(error){return tempDbHistoryJson(route,{code:'synthetic_tempdb_refused'},error.status??400);}
 });page.on('pageerror',error=>session.errors.push(error.message));return session;
}
const populated=tempDbHistoryBackend(),empty=tempDbHistoryBackend({count:0});
export function handleTempDbWindowHistoryFixture(url,request){const read=tempDbHistoryReadBinding(url);if(!read)return null;const backend=/(?:^|;\s*)tempDbHistoryFixture=1(?:;|$)/u.test(request?.headers?.cookie??'')?populated:empty;try{return{status:200,body:read.selection?backend.snapshot(read.binding,read.selection,read.cursor,read.limit):backend.read(read.binding,read.cursor,read.limit)}}catch(error){return{status:error.status??400,body:{code:'synthetic_tempdb_refused'}};}}
