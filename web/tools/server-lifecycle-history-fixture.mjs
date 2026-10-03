// Finite synthetic evidence. No source connection, hardware identity or physical failover is implied.
import {createHash} from 'node:crypto';
const sha=value=>createHash('sha256').update(value).digest('hex');
const uuid=(value,version=5)=>{const bytes=createHash('sha256').update(value).digest().subarray(0,16);bytes[6]=(bytes[6]&15)|(version<<4);bytes[8]=(bytes[8]&63)|128;const h=bytes.toString('hex');return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;};
export const serverLifecycleAlpha='11111111-1111-4111-8111-000000000001',serverLifecycleBeta='11111111-1111-4111-8111-000000000002';
export const serverLifecycleFromUtc='2026-10-02T10:00:00.123456Z',serverLifecycleToUtc='2026-10-02T11:00:00.123456Z',serverLifecycleCutoffUtc='2026-10-03T12:00:00.1234560+00:00';
export const serverLifecycleBinding={targetId:serverLifecycleAlpha,targetRevision:1,fromUtc:serverLifecycleFromUtc,toUtc:serverLifecycleToUtc};
const micro=value=>{const text=value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u,'$1'),m=/^(.*T\d\d:\d\d:\d\d)(?:\.(\d{1,6}))?(?:Z|\+00:00)$/u.exec(text);if(!m)throw new Error('Invalid synthetic UTC');return BigInt(Date.parse(m[1]+'Z'))*1000n+BigInt((m[2]??'').padEnd(6,'0'));};
const native=value=>{const seconds=value>=0n?value/1000000n:(value-999999n)/1000000n;return new Date(Number(seconds*1000n)).toISOString().slice(0,19)+'.'+(value-seconds*1000000n).toString().padStart(6,'0')+'0+00:00';};
const normalizedBinding=binding=>({...binding,targetId:binding.targetId.toLowerCase(),fromUtc:native(micro(binding.fromUtc)),toUtc:native(micro(binding.toUtc))});
function proof(index,node='SYNTHETIC_NODE_A') {
 const physicalNodeFingerprint=sha(node),logicalInstanceFingerprint=sha('SYNTHETIC_LOGICAL_INSTANCE'),processId=100+index;
 const sourceStartLocal=new Date(Date.parse('2026-09-01T00:00:00Z')+index*60000).toISOString().slice(0,19)+'.1234567';
 const startMsTicks=(100n+BigInt(index)).toString(),currentMsTicks='9223372036854775807';
 const localTicks=BigInt(Date.parse(sourceStartLocal.slice(0,19)+'Z'))*10000n+1234567n+621355968000000000n;
 return {physicalNodeFingerprint,logicalInstanceFingerprint,startupIncarnationFingerprint:sha(`sqlobserver-lifecycle-startup-v1\0${physicalNodeFingerprint}|${logicalInstanceFingerprint}|${processId}|${localTicks}|${startMsTicks}`),processId,sourceStartLocal,startMsTicks,currentMsTicks,coverage:'complete'};
}
export function serverLifecycleFixtureEvidence(index,binding=serverLifecycleBinding,options={}) {
 binding=normalizedBinding(binding);
 const detected=micro(binding.fromUtc)+(micro(binding.toUtc)-micro(binding.fromUtc))*BigInt(index+2)/10002n;
 const beforeEngine=proof(index*2),afterEngine=proof(index*2+1,index%2?'SYNTHETIC_NODE_B':'SYNTHETIC_NODE_A');
 const sampleTargetRevision=options.sampleTargetRevision??binding.targetRevision,kind=index%2?'responding_node_changed':'restart_detected';
 const beforeRunId=uuid(binding.targetId+'|before|'+index),afterRunId=uuid(binding.targetId+'|after|'+index);
 const affectedIdentityFingerprint=kind==='restart_detected'?afterEngine.physicalNodeFingerprint:afterEngine.logicalInstanceFingerprint;
 const eventId=uuid(['sqlobserver-lifecycle-event-v1',binding.targetId,sampleTargetRevision,1,kind==='restart_detected'?1:2,beforeRunId,afterRunId,affectedIdentityFingerprint].join('|'),8);
 const event={eventId,targetId:binding.targetId,sampleTargetRevision,beforeRunId,afterRunId,kind,affectedIdentityFingerprint,
  beforeObservedAtUtc:native(detected-2000000n),afterObservedAtUtc:native(detected-1000000n),detectedAtUtc:native(detected),occurredAtUtc:null,sourceSchemaVersion:1};
 return {event,beforeEngine,afterEngine};
}
export const serverLifecycleFixtureEvent=(index,binding=serverLifecycleBinding,options={})=>serverLifecycleFixtureEvidence(index,binding,options).event;
export function serverLifecycleHistoryBackend(options={}) {
 const settings={count:3,epoch:1,cutoffUtc:serverLifecycleCutoffUtc,hasVisibilityGap:true,...options},tokens=new Map();
 function source(binding){
  binding=normalizedBinding(binding);
  if(settings.count>10000)throw Object.assign(new Error('Narrow the synthetic window'),{status:422});
  const items=Array.from({length:settings.count},(_,i)=>serverLifecycleFixtureEvent(i,binding,settings)).filter(e=>micro(e.detectedAtUtc)<=micro(settings.cutoffUtc));
  const runCount=items.length?Math.min(10000,items.length+1):0,hasVisibilityGap=runCount===0||settings.hasVisibilityGap;
  const coverage={runCount,retainedSnapshotCount:runCount,completeRunCount:runCount,gapRunCount:0,failedRunCount:0,lostRunCount:0,retiredRunCount:0,
   comparisonBoundaryCount:runCount?1:0,eventCount:items.length,captureBoundaryUtc:runCount?binding.fromUtc:null,hasVisibilityGap,availabilityQualification:'unqualified',sourceConsistencyVerified:false};
  return {items,header:{...binding,cutoffUtc:settings.cutoffUtc,repositoryTimeUtc:settings.cutoffUtc,
   readEpoch:sha(JSON.stringify([binding,settings,items,coverage])),state:runCount===0?'unavailable':hasVisibilityGap?'partial':'available',
   reason:runCount===0?'no_source_receipts':hasVisibilityGap?'source_visibility_gap':'retained_source_coverage',coverage}};
 }
 return {settings,tokens,read(binding,cursor){binding=normalizedBinding(binding);const current=source(binding),key=JSON.stringify(binding);let offset=0;
  if(cursor){const saved=tokens.get(cursor);if(!saved||saved.key!==key||saved.epoch!==current.header.readEpoch)throw Object.assign(new Error('Changed synthetic continuation'),{status:409});offset=saved.offset;}
  const items=current.items.slice(offset,offset+100);let nextCursor=null;if(offset+items.length<current.items.length){nextCursor=Buffer.from(sha('SYNTHETIC_SERVER_LIFECYCLE_CURSOR|'+key+'|'+current.header.readEpoch+'|'+(offset+items.length)),'hex').toString('base64');tokens.set(nextCursor,{key,epoch:current.header.readEpoch,offset:offset+items.length});}
  return {...current.header,items:structuredClone(items),nextCursor};},
  detail(binding,selected){binding=normalizedBinding(binding);let evidence=null;for(let index=0;index<Math.min(settings.count,10001);index++){const candidate=serverLifecycleFixtureEvidence(index,binding,settings),e=candidate.event;
   if(e.eventId===selected.eventId&&e.sampleTargetRevision===selected.sampleTargetRevision&&e.afterRunId===selected.afterRunId&&micro(e.detectedAtUtc)===micro(selected.detectedAtUtc)&&micro(e.detectedAtUtc)<=micro(settings.cutoffUtc)){evidence=candidate;break;}}
   const fields=evidence??{event:null,beforeEngine:null,afterEngine:null};return {...binding,cutoffUtc:settings.cutoffUtc,repositoryTimeUtc:settings.cutoffUtc,
    readEpoch:sha(JSON.stringify([binding,fields])),state:evidence?'available':'unavailable',reason:evidence?'retained_lifecycle_event':'event_not_retained',...structuredClone(fields)};}};
}
export function serverLifecycleFixtureBinding(url) {
 const path=/\/observation-targets\/([^/]+)\/lifecycle\/history(?:\/([a-f0-9-]{36}))?$/u.exec(url.pathname);if(!path)throw Object.assign(new Error('Invalid synthetic path'),{status:400});
 const binding={targetId:path[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')};
 const allowed=path[2]?['targetRevision','fromUtc','toUtc','sampleTargetRevision','afterRunId','detectedAtUtc']:['targetRevision','fromUtc','toUtc','limit','cursor'];
 if(!Number.isSafeInteger(binding.targetRevision)||binding.targetRevision<1||!binding.fromUtc||!binding.toUtc||micro(binding.fromUtc)>=micro(binding.toUtc)||micro(binding.toUtc)-micro(binding.fromUtc)>31n*86400n*1000000n||
  [...url.searchParams.keys()].some(key=>!allowed.includes(key)||url.searchParams.getAll(key).length!==1)||!path[2]&&url.searchParams.get('limit')!=='100')throw Object.assign(new Error('Invalid synthetic query'),{status:400});
 const selected=path[2]?{eventId:path[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision')),afterRunId:url.searchParams.get('afterRunId'),detectedAtUtc:url.searchParams.get('detectedAtUtc')}:undefined;
 return {binding,cursor:url.searchParams.get('cursor')??undefined,selected};
}
const backends=new Map();
export function handleServerLifecycleHistoryFixture(url,response,request,targets,send) {
 if(!/^\/api\/v1\/observation-targets\/[^/]+\/lifecycle\/history(?:\/[a-f0-9-]{36})?$/u.test(url.pathname))return false;
 try{const {binding,cursor,selected}=serverLifecycleFixtureBinding(url),target=targets.find(t=>t.instanceId===binding.targetId);
  if(!target){send(response,{},404);return true;}if(request.method!=='GET'){send(response,{},405);return true;}if(target.configurationRevision!==binding.targetRevision){send(response,{},409);return true;}
  const enabled=request.headers.cookie?.split(';').some(v=>v.trim()==='serverLifecycleHistoryFixture=1'),key=binding.targetId+'|'+enabled;let backend=backends.get(key);
  if(!backend){backend=serverLifecycleHistoryBackend({count:enabled?3:0,cutoffUtc:new Date().toISOString()});backends.set(key,backend);}
  response.setHeader('Cache-Control','private, no-store');send(response,selected?backend.detail(binding,selected):backend.read(binding,cursor));
 }catch(error){send(response,{},error.status??400);}return true;
}
