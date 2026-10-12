import {createHash} from 'node:crypto';

export const cpuCaptureAlpha='11111111-1111-4111-8111-000000000001',cpuCaptureBeta='11111111-1111-4111-8111-000000000002';
export const cpuCaptureBinding={targetId:cpuCaptureAlpha,targetRevision:1,fromUtc:'2026-10-02T10:00:00.123456Z',toUtc:'2026-10-02T11:00:00.123456Z'};
const hash=value=>createHash('sha256').update(value).digest('hex');
export const cpuCaptureOpaqueId=value=>{const h=hash(value);return `${h.slice(0,8)}-${h.slice(8,12)}-8${h.slice(13,16)}-8${h.slice(17,20)}-${h.slice(20,32)}`;};
const micro=value=>{const m=/^(.*?)(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value);if(!m)throw Error('fixture clock');return BigInt(Date.parse(m[1]+'Z'))*1000n+BigInt((m[2]??'').slice(0,6).padEnd(6,'0'));};
const stamp=value=>new Date(Number(value/1000000n)*1000).toISOString().slice(0,19)+'.'+(value%1000000n).toString().padStart(6,'0')+'Z';
const context=b=>`${b.targetId.toLowerCase()}|${b.targetRevision}|${micro(b.fromUtc)}|${micro(b.toUtc)}`;
const flags={nativeQualification:'absent',targetComparisonAdmitted:false,hardwareIdentityVerified:false,sqlProcessPercent:null,operatingSystemPercent:null};
const counter=(counterType,firstValue='9223372036854775807',secondValue='9223372036854775807')=>({counterType,cStatus:0,firstValue,secondValue,collectedLocalFileTime100ns:'134000000000000001',multiCount:1,apiStatus:0,timeBase:'10000000'});
const process=()=>({creationBefore100ns:'134000000000000001',creationAfter100ns:'134000000000000001',exitAfter100ns:'-9223372036854775808',apiStatus:0,waitResultAfter:258});
export function cpuCaptureFixtureCapture(index=0,binding=cpuCaptureBinding,settings={}){
 const observed=stamp(micro(binding.fromUtc)+(micro(binding.toUtc)-micro(binding.fromUtc))/2n).replace('Z','7Z');
 const sql=ordinal=>({sourceObservedAtUtc:observed,ordinal,sourceSchemaVersion:2,processId:4321,sourceStartLocal:'2026-10-01T08:00:00.1234567',startMsTicks:'9007199254740993',currentMsTicks:'9007199254741993',processUserTimeMs:'9007199254740993',processKernelTimeMs:'100',sqlReportedLogicalProcessors:8});
 const tcp=()=>({observedAtUtc:observed,monotonic100ns:'9007199254740993',af4Status:0,af6Status:0,rowCount4:100,rowCount6:3,matchingRowCount:2,sqlProcess:process(),collectorProcess:process()});
 const cpu=after=>({observedAtUtc:observed,monotonicTicks:after?'9007199264740993':'9007199254740993',monotonicFrequency:'10000000',sqlProcessCreationFileTime100ns:'134000000000000001',logicalProcessorCount:8,processCounter:counter(0x20510500),osCounter:counter(0x21510500)});
 const frame=()=>({processCreationBefore100ns:'134000000000000001',processCreationAfter100ns:'134000000000000001',processExitAfter100ns:'-9223372036854775808',logicalProcessorsBefore:8,logicalProcessorsAfter:8,processInstances:4,processMappingAmbiguous:false,processWaitResultAfter:258});
 const value={sourceSchemaVersion:1,state:'partial',reason:'source_qualification_absent',candidateReason:'None',candidateLocalAssociation:true,sourceFrames:6,
  sqlBefore:sql(1),sqlAfter:sql(2),tcpBefore:tcp(),tcpAfter:tcp(),cpuBefore:cpu(false),cpuAfter:cpu(true),idProcessBefore:counter(0x10010400,'4321','0'),idProcessAfter:counter(0x10010400,'4321','0'),frameBefore:frame(),frameAfter:frame(),
  processStatusBefore:0,processStatusAfter:0,osStatusBefore:0,osStatusAfter:0,tcpBeforeReadReason:null,cpuPairReadReason:null,tcpAfterReadReason:null,processReadReason:'source_qualification_absent',
  numericalEligibility:{sqlProcessEligible:true,operatingSystemEligible:true,sameNativeInterval:true,sqlProcessReason:'None',operatingSystemReason:'None',sqlFromNative100ns:'9007199254740993',sqlToNative100ns:'9007199264740993',osFromNative100ns:'9007199254740993',osToNative100ns:'9007199264740993'},
  bytes:{tcpBeforeHelperBytes:4096,cpuPairHelperBytes:8192,tcpAfterHelperBytes:4096,sqlLogicalResponseUpperBoundBytes:'16384'},...flags};
 if(settings.noHost){for(const key of ['sqlBefore','sqlAfter','tcpBefore','tcpAfter','cpuBefore','cpuAfter','idProcessBefore','idProcessAfter','frameBefore','frameAfter'])value[key]=null;Object.assign(value,{state:'unavailable',reason:'host_binding_unavailable',candidateReason:'NativeCaptureIncomplete',candidateLocalAssociation:false,sourceFrames:0,numericalEligibility:null,processReadReason:null,bytes:{tcpBeforeHelperBytes:null,cpuPairHelperBytes:null,tcpAfterHelperBytes:null,sqlLogicalResponseUpperBoundBytes:'0'}});}
 if(settings.incomplete){value.tcpAfter=null;Object.assign(value,{state:'unavailable',reason:'native_capture_incomplete',candidateReason:'NativeCaptureIncomplete',candidateLocalAssociation:false,sourceFrames:5,numericalEligibility:null,tcpAfterReadReason:'native_table_unavailable'});}
 if(settings.numericGap){value.reason='numerical_evidence_incomplete';Object.assign(value.numericalEligibility,{sqlProcessEligible:false,sameNativeInterval:false,sqlProcessReason:'CounterStatusInvalid',sqlFromNative100ns:null,sqlToNative100ns:null});}
 if(settings.candidateGap){Object.assign(value,{reason:'candidate_association_rejected',candidateReason:'HostBindingMismatch',candidateLocalAssociation:false,numericalEligibility:null});}
 return value;
}
export function cpuCaptureFixtureReceipt(index=0,binding=cpuCaptureBinding,settings={}){
 const failed=Boolean(settings.failedLatest&&index===0),missing=Boolean(settings.missingLatest&&index===0),lost=Boolean(settings.lostLatest&&index===0);
 const capture=failed||missing||lost?null:cpuCaptureFixtureCapture(index,binding,settings),start=micro(binding.fromUtc),end=micro(binding.toUtc),step=(end-start)/20000n||1n;
 return{runId:cpuCaptureOpaqueId(`${binding.targetId.toLowerCase()}|${micro(binding.fromUtc)}|${micro(binding.toUtc)}|${index}`),sampleTargetRevision:settings.sampleTargetRevision??1,completedAtUtc:stamp(end-(end-start)/4n-BigInt(index)*step),outcome:failed?'permission_denied':lost?'output_invalid':'succeeded',lossKind:lost?'output_validation_failure':'none',minimumLostItems:lost?1:0,capturePresent:capture!==null,sourceFrames:capture?.sourceFrames??null,state:capture?.state??'unavailable',reason:capture?.reason??(missing?'payload_missing':'collection_failed')};
}
export function cpuCaptureHistoryBackend(options={}){
 const settings={count:3,epoch:1,failedLatest:false,missingLatest:false,lostLatest:false,...options},known=new Map(),tokens=new Map();
 const header=(binding,epoch,cutoff=stamp(micro(binding.toUtc)))=>({...binding,cutoffUtc:cutoff,readEpoch:hash(`${epoch}|${context(binding)}`),repositoryTimeUtc:stamp(micro(cutoff)+1000000n),...flags});
 function read(binding=cpuCaptureBinding,token,limit=100){
  if(settings.count>10000)throw Object.assign(Error('window overflow'),{status:422});
  let offset=0,cutoff=stamp(micro(binding.toUtc));if(token){const prior=tokens.get(token);if(!prior||prior.context!==context(binding)||prior.limit!==limit)throw Object.assign(Error('cursor'),{status:400});if(prior.epoch!==settings.epoch)throw Object.assign(Error('epoch'),{status:409});offset=prior.offset;cutoff=prior.cutoff;}
  const all=Array.from({length:settings.count},(_,i)=>cpuCaptureFixtureReceipt(i,binding,settings));for(const item of all)known.set(item.runId,{receipt:item,binding});
  const complete=all.filter(r=>r.outcome==='succeeded'&&r.lossKind==='none'&&r.capturePresent&&r.sourceFrames===6).length;
  const coverage={runCount:all.length,retainedReceiptCount:all.length,completeRunCount:complete,gapRunCount:all.length-complete,failedRunCount:all.filter(r=>r.outcome!=='succeeded').length,lostRunCount:all.filter(r=>r.lossKind!=='none').length,missingPayloadRunCount:all.filter(r=>r.outcome==='succeeded'&&!r.capturePresent).length,hasVisibilityGap:all.length!==complete||Boolean(settings.revisionBoundary)};
  let nextCursor=null;if(offset+limit<all.length){nextCursor=Buffer.from(`synthetic protected CPU cursor ${context(binding)} ${settings.epoch} ${offset+limit} ${limit}`).toString('base64');tokens.set(nextCursor,{context:context(binding),epoch:settings.epoch,offset:offset+limit,limit,cutoff});}
  return{...header(binding,settings.epoch,cutoff),state:all.length?coverage.hasVisibilityGap?'partial':'available':'unavailable',reason:all.length?settings.revisionBoundary?'revision_boundary':coverage.hasVisibilityGap?'visibility_gaps':'retained_captures':'no_completed_runs',coverage,items:all.slice(offset,offset+limit),nextCursor};
 }
 function detail(binding,selection){
  let source=known.get(selection.runId);if(!source){for(let i=0;i<Math.min(settings.count,10001);i++){const receipt=cpuCaptureFixtureReceipt(i,binding,settings);if(receipt.runId===selection.runId){source={receipt,binding};known.set(receipt.runId,source);break;}}}
  let receipt=source?.receipt;const outside=receipt&&(micro(receipt.completedAtUtc)<micro(binding.fromUtc)||micro(receipt.completedAtUtc)>=micro(binding.toUtc));
  if(settings.detailAbsent||outside||receipt?.sampleTargetRevision!==selection.sampleTargetRevision)receipt=undefined;
  const capture=receipt?.capturePresent?cpuCaptureFixtureCapture(0,source?.binding??binding,settings):null;
  return{...header(binding,`${selection.runId}|${JSON.stringify(receipt??null)}|${JSON.stringify(capture)}`),...selection,state:receipt?.state??'unavailable',reason:receipt?.reason??(outside?'capture_outside_window':'capture_not_retained'),receipt:receipt??null,capture};
 }
 return{settings,read,detail,tokens};
}
export const cpuCaptureFixtureDetail=(index=0,binding=cpuCaptureBinding,settings={})=>{const backend=cpuCaptureHistoryBackend({...settings,count:Math.max(index+1,settings.count??3)}),receipt=cpuCaptureFixtureReceipt(index,binding,settings);return backend.detail(binding,{runId:receipt.runId,sampleTargetRevision:receipt.sampleTargetRevision});};
export function cpuCaptureFixtureBinding(url){const match=/^\/api\/v1\/observation-targets\/([a-f0-9-]+)\/resources\/cpu\/captures(?:\/([a-f0-9-]+))?$/.exec(url.pathname);if(!match)return undefined;return{binding:{targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},cursor:url.searchParams.get('cursor')??undefined,limit:Number(url.searchParams.get('limit')??25),selection:match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined};}
const empty=cpuCaptureHistoryBackend({count:0}),shared=cpuCaptureHistoryBackend();
export function handleCpuCaptureWindowHistoryFixture(url,request){const parsed=cpuCaptureFixtureBinding(url);if(!parsed)return null;const populated=/(?:^|;\s*)cpuCaptureHistoryFixture=1(?:;|$)/.test(request?.headers?.cookie??'');const backend=populated?shared:empty;try{return{status:200,body:parsed.selection?backend.detail(parsed.binding,parsed.selection):backend.read(parsed.binding,parsed.cursor,parsed.limit)};}catch(error){return{status:error.status??400,body:{code:'cpu_capture_fixture_refused',message:'Synthetic CPU capture read refused.'}};}}
export const cpuCaptureHistoryHeld=()=>{let release;const wait=new Promise(resolve=>{release=resolve;});return{wait,release};};
export const cpuCaptureHistoryJson=(route,body,status=200)=>route.fulfill({status,contentType:'application/json',body:JSON.stringify(body)});
export async function installCpuCaptureHistoryFixture(page,options={}){
 const backend=cpuCaptureHistoryBackend(options),requests=[],unexpected=[],origin=options.origin??'http://127.0.0.1:4328';let handler;
 await page.route('**/api/**',async route=>{const request=route.request(),url=new URL(request.url()),parsed=cpuCaptureFixtureBinding(url);
  if(options.allowOtherApi&&url.origin===origin&&!parsed)return route.continue();
  if(url.origin!==origin||!parsed||request.method()!=='GET'){unexpected.push(`${request.method()} ${url.pathname}`);return cpuCaptureHistoryJson(route,{code:'unexpected_fixture_request'},500);}
  const call={...parsed,url,method:request.method()};requests.push(call);
  if(handler){const result=await handler(call,route);if(result!==undefined)return result;}
  try{return cpuCaptureHistoryJson(route,parsed.selection?backend.detail(parsed.binding,parsed.selection):backend.read(parsed.binding,parsed.cursor,parsed.limit));}catch(error){return cpuCaptureHistoryJson(route,{code:'cpu_capture_fixture_refused',message:'Synthetic read refused.'},error.status??400);}
 });
 return{backend,requests,unexpected,setHandler:value=>{handler=value;}};
}
