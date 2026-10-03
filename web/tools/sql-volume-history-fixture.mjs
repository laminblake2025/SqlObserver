// Finite synthetic retained observations. No production collectors or connected server data.
import {createHash} from 'node:crypto';
const digest=value=>createHash('sha256').update(value).digest('hex');
export const historyAlpha='11111111-1111-4111-8111-000000000001',historyBeta='11111111-1111-4111-8111-000000000002';
export const historyKeyA=digest('SYNTHETIC_VOLUME_A'),historyKeyB=digest('SYNTHETIC_VOLUME_B');
export const historyFromUtc='2026-10-02T10:00:00.123456Z',historyToUtc='2026-10-02T11:00:00.123456Z',historyCutoffUtc='2026-10-03T12:00:00.1234560+00:00';
export const historyUuid=value=>{const chars=digest(value).slice(0,32).split('');chars[12]='5';chars[16]=((parseInt(chars[16],16)&3)|8).toString(16);const h=chars.join('');return[h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');};
export const historyIdentity=(index=0,sampleTargetRevision=1)=>({sampleTargetRevision,volumeKey:index===0?historyKeyA:index===1?historyKeyB:digest('SYNTHETIC_VOLUME_'+index)});
const nativeUtc=ms=>new Date(ms).toISOString().replace('Z','+00:00');
export function volumeHistoryBackend(options={}) {
 const settings={catalogCount:2,runCount:4,sampleTargetRevision:1,identityKind:'volume_id',mode:'available',epoch:1,cutoffUtc:historyCutoffUtc,...options},cursors=new Map();
 function materialize(binding) {
  if(settings.catalogCount>1000||settings.runCount>10000||settings.catalogCount*settings.runCount>250000)throw Object.assign(new Error('Narrow window'),{status:422});
  const from=Date.parse(binding.fromUtc),to=Date.parse(binding.toUtc),cutoff=Date.parse(settings.cutoffUtc),end=Math.min(to-1,cutoff-1),unavailable=settings.mode==='unavailable'||settings.runCount===0||end<=from;
  const count=unavailable?0:settings.runCount, duration=Math.min(180000,end-from),start=end-duration;
  const identities=Array.from({length:unavailable||settings.mode==='absent'?0:settings.catalogCount},(_,index)=>historyIdentity(index,settings.sampleTargetRevision)).sort((a,b)=>a.sampleTargetRevision-b.sampleTargetRevision||a.volumeKey.localeCompare(b.volumeKey));
  const sampleRevision=binding.selected?.sampleTargetRevision??settings.sampleTargetRevision;
  const identityKind=settings.mode==='absent'?null:settings.identityKind;
  const rows=Array.from({length:count},(_,index)=>{
   const completed=start+(count===1?duration:index*duration/(count-1)),point=nativeUtc(Math.floor(completed)),unknown=settings.mode==='unknown',absent=settings.mode==='absent',gap=settings.mode==='gap'&&index===1,loss=settings.mode==='loss'&&index===1,skew=settings.mode==='skew'&&index===1;
   const total=settings.mode==='capacity_changed'&&index>=Math.floor(count/2)?'214748364800':'107374182400';
   return {runId:historyUuid(binding.instanceId+'|'+sampleRevision+'|'+binding.fromUtc+'|'+index),completedAtUtc:point,
    observedAtUtc:absent?null:nativeUtc(Math.floor(completed)-(skew?-600000:1000)),collectedAtUtc:absent?null:point,outcome:loss?'partial':'succeeded',lossKind:loss?'source_row_limit':'none',minimumLostItems:loss?1:0,
    state:unknown||gap||loss||skew?'partial':absent?'unavailable':'available',reason:unknown?'capacity_unknown':absent?'volume_absent':gap?'collection_gap':loss?'collection_loss':skew?'source_clock_skew':'observed_capacity',mappedFileCount:absent?null:2,
    totalBytes:unknown||absent?null:total,availableBytes:unknown||absent?null:(BigInt(total)/2n-BigInt(index)*1000000n).toString()};
  }).sort((a,b)=>Date.parse(a.completedAtUtc)-Date.parse(b.completedAtUtc)||a.runId.localeCompare(b.runId));
  const gaps=rows.filter(row=>row.state!=='available').length,hasVisibilityGap=gaps>0||settings.mode==='revision_boundary';
  const comparable=identityKind==='volume_id'&&rows.length>=2&&!hasVisibilityGap&&new Set(rows.map(row=>row.totalBytes)).size===1&&rows.every(row=>row.totalBytes!==null);
  const comparisonState=comparable?'eligible':identityKind!=='volume_id'?'identity_not_comparable':hasVisibilityGap?'visibility_gaps':settings.mode==='capacity_changed'?'capacity_changed':rows.length<2?'insufficient_observations':'capacity_unknown';
  const first=rows[0],last=rows.at(-1),catalog=identities.map(identity=>({...identity,identityKind:settings.identityKind,observationCount:rows.length,firstCompletedAtUtc:first.completedAtUtc,lastCompletedAtUtc:last.completedAtUtc,
   firstTotalBytes:first.totalBytes,firstAvailableBytes:first.availableBytes,lastTotalBytes:last.totalBytes,lastAvailableBytes:last.availableBytes,comparisonState,
   availableBytesDelta:comparable?(BigInt(last.availableBytes)-BigInt(first.availableBytes)).toString():null,totalBytesDelta:comparable?'0':null}));
  const retained=binding.selected?settings.mode==='absent'?0:rows.length:catalog.length*rows.length;
  const header={instanceId:binding.instanceId,targetRevision:binding.targetRevision,fromUtc:binding.fromUtc,toUtc:binding.toUtc,cutoffUtc:settings.cutoffUtc,
   readEpoch:digest(JSON.stringify([binding,settings,rows,catalog])),repositoryTimeUtc:settings.cutoffUtc,state:rows.length===0||retained===0?'unavailable':hasVisibilityGap?'partial':'available',reason:settings.mode==='revision_boundary'?'revision_boundary':rows.length===0?'no_completed_runs':retained===0?'retained_evidence_unavailable':hasVisibilityGap?'visibility_gaps':'retained_window',
   coverage:{runCount:rows.length,completeRunCount:rows.length-gaps,gapRunCount:gaps,retainedObservationCount:retained,retentionDays:30,hasVisibilityGap},nextCursor:null};
  return {header,rows,catalog,identityKind};
 }
 return {settings,cursors,read(binding,cursor){
  const full=materialize(binding),limit=binding.selected?250:25,items=binding.selected?full.rows:full.catalog,key=JSON.stringify(binding);let index=0;
  if(cursor){const saved=cursors.get(cursor);if(!saved||saved.key!==key||saved.epoch!==full.header.readEpoch)throw Object.assign(new Error('Changed protected continuation'),{status:409});index=saved.index;}
  const selected=items.slice(index,index+limit);let nextCursor=null;
  if(index+selected.length<items.length){nextCursor=Buffer.from(digest('SYNTHETIC_PROTECTED_VOLUME_HISTORY|'+key+'|'+full.header.readEpoch+'|'+(index+selected.length)),'hex').toString('base64');cursors.set(nextCursor,{key,epoch:full.header.readEpoch,index:index+selected.length});}
  return {...full.header,...(binding.selected?{...binding.selected,identityKind:full.identityKind}:{}),items:structuredClone(selected),nextCursor};
 }};
}
export function historyBinding(url) {
 const selected=/\/volumes\/([a-f0-9]{64})\/history$/u.exec(url.pathname),allowed=selected?['targetRevision','sampleTargetRevision','fromUtc','toUtc','limit','cursor']:['targetRevision','fromUtc','toUtc','limit','cursor'];
 if([...url.searchParams.keys()].some(key=>!allowed.includes(key)||url.searchParams.getAll(key).length!==1)||url.searchParams.get('limit')!==(selected?'250':'25'))throw Object.assign(new Error('Invalid request'),{status:400});
 const target=/\/observation-targets\/([^/]+)\//u.exec(url.pathname)?.[1],targetRevision=Number(url.searchParams.get('targetRevision')),sampleTargetRevision=Number(url.searchParams.get('sampleTargetRevision'));
 if(!target||!Number.isSafeInteger(targetRevision)||targetRevision<1||selected&&(!Number.isSafeInteger(sampleTargetRevision)||sampleTargetRevision<1)||!url.searchParams.get('fromUtc')||!url.searchParams.get('toUtc'))throw Object.assign(new Error('Invalid request'),{status:400});
 return {instanceId:target,targetRevision,fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),...(selected?{selected:{sampleTargetRevision,volumeKey:selected[1]}}:{})};
}
const fixtureBackends=new Map();
export function handleSqlVolumeHistoryFixture(url,response,request,targets,send) {
 if(!/^\/api\/v1\/observation-targets\/[^/]+\/resources\/volumes\/(?:history|[a-f0-9]{64}\/history)$/u.test(url.pathname))return false;
 try{const binding=historyBinding(url),target=targets.find(item=>item.instanceId===binding.instanceId);if(!target){send(response,{},404);return true;}if(request.method!=='GET'){send(response,{},405);return true;}
  if(binding.targetRevision!==target.configurationRevision){send(response,{},409);return true;}
  const enabled=request.headers.cookie?.split(';').some(item=>item.trim()==='sqlVolumeHistoryFixture=1'),mode=enabled?'available':'unavailable';
  const key=binding.instanceId+'|'+mode;let backend=fixtureBackends.get(key);if(!backend){backend=volumeHistoryBackend({mode,cutoffUtc:new Date().toISOString()});fixtureBackends.set(key,backend);}
  response.setHeader('Cache-Control','private, no-store');send(response,backend.read(binding,url.searchParams.get('cursor')??undefined));
 }catch(error){send(response,{},error.status??400);}return true;
}
