import {createHash} from 'node:crypto';

export const cpuObservationAlpha='11111111-1111-4111-8111-000000000001',cpuObservationBeta='11111111-1111-4111-8111-000000000002';
export const cpuObservationBinding={targetId:cpuObservationAlpha,targetRevision:1,fromUtc:'2026-10-02T10:00:00.123456Z',toUtc:'2026-10-02T11:00:00.123456Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
export const cpuObservationOpaqueId=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
const micro=value=>{const m=/^(.*?)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!m)throw Error('fixture clock');return BigInt(Date.parse(m[1]+'Z'))*1000n+BigInt((m[2]??'').slice(0,6).padEnd(6,'0'));};
const stamp=value=>new Date(Number(value/1000000n)*1000).toISOString().slice(0,19)+'.'+(value%1000000n).toString().padStart(6,'0')+'Z';
const context=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${micro(b.fromUtc)}|${micro(b.toUtc)}`;
const stampSource = value => stamp(value).replace('Z','0Z');
export function cpuObservationFixtureObservation(index=0,binding=cpuObservationBinding,settings={}) {
 const start=micro(binding.fromUtc)+(micro(binding.toUtc)-micro(binding.fromUtc))/2n;
 const value=(host=false)=>({percent:host?35:12,rawPercent:host?35:12,quality:'available',reason:'None',fromNative100ns:'134000000000000001',toNative100ns:host?'134000000010000002':'134000000010000001',observedFromUtc:stampSource(start),observedToUtc:stampSource(start+1000000n),normalizationLogicalProcessors:host?1:8,method:'pdh_calculate_exact_raw_pair',providerApiStatus:0,providerCStatus:0,rawFormulaEligible:true,rawFormulaReason:'None',providerRawPercent:host?35:96});
 const result={sourceSchemaVersion:1,state:'available',reason:'measured',sourceAssociationVerified:true,associationReason:'None',exactPairEligible:false,sqlProcess:value(),operatingSystem:value(true)};
 if(settings.adjusted&&index===0){Object.assign(result.operatingSystem,{percent:0,rawPercent:-0.4,quality:'provider_adjusted',reason:'ProviderAdjusted',rawFormulaEligible:false,rawFormulaReason:'CounterRangeInvalid',providerRawPercent:0});result.state='partial';result.reason='component_quality';}
 if(settings.componentGap&&index===1){Object.assign(result.sqlProcess,{percent:null,rawPercent:null,quality:'unavailable',reason:'SourceUnavailable',providerApiStatus:5,providerCStatus:null,rawFormulaEligible:false,rawFormulaReason:'SourceUnavailable',providerRawPercent:null});result.state='partial';result.reason='component_quality';}
 return result;
}
export function cpuObservationFixtureReceipt(index=0,binding=cpuObservationBinding,settings={}){
 const failed=Boolean(settings.failedLatest&&index===0),missing=Boolean(settings.missingLatest&&index===0),lost=Boolean(settings.lostLatest&&index===0);
 const observation=failed||missing||lost?null:cpuObservationFixtureObservation(index,binding,settings),start=micro(binding.fromUtc),end=micro(binding.toUtc),step=(end-start)/20000n||1n;
 return{runId:cpuObservationOpaqueId(`${binding.targetId.toLowerCase()}|${micro(binding.fromUtc)}|${micro(binding.toUtc)}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(end-(end-start)/4n-BigInt(index)*step),outcome:failed?'permission_denied':lost?'output_invalid':'succeeded',lossKind:lost?'output_validation_failure':'none',minimumLostItems:lost?1:0,observationPresent:observation!==null,observation,state:observation?.state??'unavailable',reason:observation?.reason??(missing?'payload_missing':'collection_failed')};
}
export function cpuObservationHistoryBackend(options={}){
 const settings={count:3,epoch:1,failedLatest:false,missingLatest:false,lostLatest:false,...options},known=new Map(),tokens=new Map();
 const header=(binding,epoch,cutoff=stamp(micro(binding.toUtc)))=>({...binding,cutoffUtc:cutoff,readEpoch:hash(`${epoch}|${context(binding)}`),repositoryTimeUtc:stamp(micro(cutoff)+1000000n)});
 function read(binding=cpuObservationBinding,token,limit=100){
  if(settings.count>10000)throw Object.assign(Error('window overflow'),{status:422});
  let offset=0,cutoff=stamp(micro(binding.toUtc));if(token){const prior=tokens.get(token);if(!prior||prior.context!==context(binding)||prior.limit!==limit)throw Object.assign(Error('cursor'),{status:400});if(prior.epoch!==settings.epoch)throw Object.assign(Error('epoch'),{status:409});offset=prior.offset;cutoff=prior.cutoff;}
  const all=Array.from({length:settings.count},(_,i)=>cpuObservationFixtureReceipt(i,binding,settings));for(const item of all)known.set(item.runId,{receipt:item,binding});
  const complete=all.filter(r=>r.state==='available').length;
  const coverage={runCount:all.length,retainedReceiptCount:all.length,completeRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>r.outcome!=='succeeded').length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>r.outcome==='succeeded'&&!r.observationPresent).length,hasVisibilityGap:all.length!==complete||Boolean(settings.revisionBoundary)};
  let nextCursor=null;if(offset+limit<all.length){nextCursor=Buffer.from(`synthetic protected CPU observation cursor ${context(binding)} ${settings.epoch} ${offset+limit} ${limit}`).toString('base64');tokens.set(nextCursor,{context:context(binding),epoch:settings.epoch,offset:offset+limit,limit,cutoff});}
  return{...header(binding,settings.epoch,cutoff),state:all.length?coverage.hasVisibilityGap?'partial':'available':'unavailable',reason:all.length?settings.revisionBoundary?'revision_boundary':coverage.hasVisibilityGap?'visibility_gaps':'retained_observations':'no_completed_runs',coverage,items:all.slice(offset,offset+limit),nextCursor};
 }
 function detail(binding,selection){
  let source=known.get(selection.runId);if(!source){for(let i=0;i<Math.min(settings.count,10001);i++){const receipt=cpuObservationFixtureReceipt(i,binding,settings);if(receipt.runId===selection.runId){source={receipt,binding};known.set(receipt.runId,source);break;}}}
  let receipt=source?.receipt;const outside=receipt&&(micro(receipt.completedAtUtc)<micro(binding.fromUtc)||micro(receipt.completedAtUtc)>=micro(binding.toUtc));
  if(settings.detailAbsent||outside||receipt?.sampleTargetRevision!==selection.sampleTargetRevision)receipt=undefined;
  return{...header(binding,`${selection.runId}|${JSON.stringify(receipt??null)}`),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'observation_outside_window':'observation_not_retained'),receipt:receipt??null};
 }
 return{settings,read,detail,tokens};
}
export const cpuObservationFixtureDetail=(index=0,binding=cpuObservationBinding,settings={})=>{const backend=cpuObservationHistoryBackend({...settings,count:Math.max(index+1,settings.count??3)}),receipt=cpuObservationFixtureReceipt(index,binding,settings);return backend.detail(binding,{runId:receipt.runId,sampleTargetRevision:receipt.sampleTargetRevision});};
export function cpuObservationFixtureBinding(url){const match=/^\/api\/v1\/observation-targets\/([a-f0-9-]+)\/resources\/cpu\/observations(?:\/([a-f0-9-]+))?$/.exec(url.pathname);if(!match)return undefined;return{binding:{targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??25),selection:match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined};}
const empty=cpuObservationHistoryBackend({count:0}),shared=cpuObservationHistoryBackend();
export function handleCpuObservationWindowHistoryFixture(url,request){
 const collection=/^\/api\/v1\/observation-targets\/([a-f0-9-]+)\/resources\/cpu\/collection$/.exec(url.pathname);
 if(collection)return request?.method==='GET'?{status:200,body:{targetId:collection[1],targetRevision:Number(url.searchParams.get('targetRevision')),collectorEnabled:false,scheduleRevision:1}}:{status:403,body:{code:'fixture_read_only'}};
const parsed=cpuObservationFixtureBinding(url);if(!parsed)return null;const populated=/(?:^|;\s*)cpuObservationHistoryFixture=1(?:;|$)/.test(request?.headers?.cookie??'');const backend=populated?shared:empty;try{return{status:200,body:parsed.selection?backend.detail(parsed.binding,parsed.selection):backend.read(parsed.binding,parsed.cursor,parsed.limit)};}catch(error){return{status:error.status??400,body:{code:'cpu_observation_fixture_refused',message:'Synthetic CPU observation read refused.'}};}}
export const cpuObservationHistoryHeld=()=>{let release;const wait=new Promise(resolve=>{release=resolve;});return{wait,release};};
export const cpuObservationHistoryJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
export async function installCpuObservationHistoryFixture(page,options={}){
 const backend=cpuObservationHistoryBackend(options),requests=[],unexpected=[],origin=options.origin??'http://127.0.0.1:4328';let handler; const collectionCalls=[],collectionState=new Map();
 await page.route('**/api/**',async route=>{const request=route.request(),url=new URL(request.url()),parsed=cpuObservationFixtureBinding(url);
  const collection=/^\/api\/v1\/observation-targets\/([a-f0-9-]+)\/resources\/cpu\/collection$/.exec(url.pathname);
  if(collection&&url.origin===origin){
   const body=request.method()==='PUT'?request.postDataJSON():undefined,targetId=collection[1],targetRevision=body?.expectedTargetRevision??Number(url.searchParams.get('targetRevision'));
   collectionCalls.push({method:request.method(),targetId,body});
   if(request.method()==='PUT')collectionState.set(targetId,body.enabled);
   return cpuObservationHistoryJson(route,{targetId,targetRevision,collectorEnabled:collectionState.get(targetId)??false,scheduleRevision:collectionState.has(targetId)?2:1});
  }
  if(options.allowOtherApi&&url.origin===origin&&!parsed)return route.continue();
  if(url.origin!==origin||!parsed||request.method()!=='GET'){unexpected.push(`${request.method()} ${url.pathname}`);return cpuObservationHistoryJson(route,{code:'unexpected_fixture_request'},500);}
  const call={...parsed,url,method:request.method()};requests.push(call);
  if(handler){const result=await handler(call,route);if(result!==undefined)return result;}
  try{return cpuObservationHistoryJson(route,parsed.selection?backend.detail(parsed.binding,parsed.selection):backend.read(parsed.binding,parsed.cursor,parsed.limit));}catch(error){return cpuObservationHistoryJson(route,{code:'cpu_observation_fixture_refused',message:'Synthetic read refused.'},error.status??400);}
 });
 return{backend,requests,unexpected,collectionCalls,setHandler:value=>{handler=value;}};
}
