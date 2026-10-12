import test from 'node:test';
import assert from 'node:assert/strict';
import {cpuCaptureWindow,cpuCaptureUtc,cpuCaptureSourceUtc,cpuCaptureInt64,cpuCaptureCursor,cpuCaptureContextIdentity,parseCpuCaptureReceiptPage,parseCpuCaptureDetailPage} from '../src/features/resources/cpuCaptureWindowHistoryModel.ts';
import {getCpuCaptureReceipts,getCpuCaptureDetail} from '../src/features/resources/cpuCaptureWindowHistoryApi.ts';
import {cpuCaptureBinding as binding,cpuCaptureBeta,cpuCaptureHistoryBackend,cpuCaptureFixtureDetail,cpuCaptureFixtureReceipt} from '../tools/cpu-capture-window-history-fixture.mjs';
const clone=structuredClone,pin=receipt=>({runId:receipt.runId,sampleTargetRevision:receipt.sampleTargetRevision});
const exact=(options={})=>{const page=cpuCaptureFixtureDetail(0,binding,options);return{page,selection:pin(page)};};
const parse=(page,selection)=>parseCpuCaptureDetailPage(page,binding,selection);

test('completion window accepts 31 days and preserves exact microseconds across equivalent UTC spellings',()=>{
 assert.deepEqual(cpuCaptureWindow(binding.fromUtc,binding.toUtc),{fromUtc:binding.fromUtc,toUtc:binding.toUtc});
 cpuCaptureWindow('2026-10-01T00:00:00.000001Z','2026-11-01T00:00:00.000001Z');
 for(const pair of [[binding.fromUtc,binding.fromUtc],[binding.toUtc,binding.fromUtc],['2026-10-01T00:00:00Z','2026-11-01T00:00:00.000001Z'],['2026-02-30T00:00:00Z',binding.toUtc]])assert.throws(()=>cpuCaptureWindow(...pair));
 const equivalent={...binding,fromUtc:binding.fromUtc.replace('Z','0+00:00'),toUtc:binding.toUtc.replace('Z','0+00:00')};assert.equal(cpuCaptureContextIdentity(binding),cpuCaptureContextIdentity(equivalent));assert.notEqual(cpuCaptureContextIdentity(binding),cpuCaptureContextIdentity({...binding,toUtc:'2026-10-02T11:00:00.123457Z'}));
});
test('seven-digit source UTC and local startup do not silently round provider clocks',()=>{
 assert.equal(cpuCaptureSourceUtc('2026-10-02T10:00:00.1234567Z'),'2026-10-02T10:00:00.1234567Z');
 assert.equal(cpuCaptureUtc('2026-10-02T10:00:00.1234560+00:00'),'2026-10-02T10:00:00.123456Z');
 for(const clock of ['2026-10-02T10:00:00.123456Z','2026-10-02T10:00:00.1234567+00:00','2026-02-30T10:00:00.1234567Z','2026-10-02T10:00:00.12345678Z'])assert.throws(()=>cpuCaptureSourceUtc(clock));
 const {page,selection}=exact();assert.equal(parse(page,selection).capture.sqlBefore.sourceStartLocal,'2026-10-01T08:00:00.1234567');const bad=clone(page);bad.capture.sqlBefore.sourceStartLocal+='Z';assert.throws(()=>parse(bad,selection));
});
test('exact signed Int64 counter and undefined exit bits survive beyond Javascript precision',()=>{
 const {page,selection}=exact(),value=parse(page,selection);assert.equal(value.capture.cpuBefore.processCounter.firstValue,'9223372036854775807');assert.equal(value.capture.tcpBefore.sqlProcess.exitAfter100ns,'-9223372036854775808');
 assert.equal(cpuCaptureInt64('-9223372036854775808',true),'-9223372036854775808');
 for(const value of [1,'01','+1','-0','1.0','9223372036854775808','-9223372036854775809'])assert.throws(()=>cpuCaptureInt64(value,true));assert.throws(()=>cpuCaptureInt64('-1'));
});
test('a complete source capture is retained as unqualified partial diagnostics with no public percentages',()=>{
 const backend=cpuCaptureHistoryBackend(),page=parseCpuCaptureReceiptPage(backend.read(binding),binding),selection=pin(page.items[0]),detail=parse(backend.detail(binding,selection),selection);
 assert.equal(page.state,'available');assert.equal(page.coverage.completeRunCount,3);assert.equal(detail.state,'partial');assert.equal(detail.capture.candidateLocalAssociation,true);assert.equal(detail.capture.numericalEligibility.sameNativeInterval,true);
 for(const receipt of [page,detail,detail.capture]){assert.equal(receipt.nativeQualification,'absent');assert.equal(receipt.targetComparisonAdmitted,false);assert.equal(receipt.hardwareIdentityVerified,false);assert.equal(receipt.sqlProcessPercent,null);assert.equal(receipt.operatingSystemPercent,null);}
});
for(const field of ['nativeQualification','targetComparisonAdmitted','hardwareIdentityVerified','sqlProcessPercent','operatingSystemPercent'])test('refuses source admission injected through '+field,()=>{
 const {page,selection}=exact(),value=field.endsWith('Percent')?'0':field==='nativeQualification'?'qualified':true;
 assert.throws(()=>parse({...page,[field]:value},selection));const bad=clone(page);bad.capture[field]=value;assert.throws(()=>parse(bad,selection));
});
test('capture shape refuses source-local routing, identities, process names and undeclared fields',()=>{
 const {page,selection}=exact();for(const [path,key] of [['capture','expectedHostFingerprint'],['sqlBefore','serverSessionId'],['tcpBefore','rows'],['cpuBefore','processName'],['bytes','tdsWireBytes']]){
  const bad=clone(page),object=path==='capture'?bad.capture:bad.capture[path];object[key]='PRIVATE_SOURCE';assert.throws(()=>parse(bad,selection));
 }
 const bad=clone(page);bad.secret='PRIVATE';assert.throws(()=>parse(bad,selection));
});
test('failed, lost and missing latest attempts remain first without inventing a capture',()=>{
 for(const mode of ['failedLatest','lostLatest','missingLatest']){const backend=cpuCaptureHistoryBackend({[mode]:true}),page=parseCpuCaptureReceiptPage(backend.read(binding),binding),selection=pin(page.items[0]),detail=parse(backend.detail(binding,selection),selection);assert.equal(detail.capture,null);assert.equal(detail.receipt.capturePresent,false);assert.equal(detail.receipt.reason,mode==='missingLatest'?'payload_missing':'collection_failed');assert.equal(page.coverage.gapRunCount,1);assert.equal(page.coverage.failedRunCount,mode==='missingLatest'?0:1);}
});
test('zero-frame host refusal is a present unavailable receipt, distinct from missing payload',()=>{
 const {page,selection}=exact({noHost:true}),value=parse(page,selection);assert.equal(value.capture.sourceFrames,0);assert.equal(value.receipt.capturePresent,true);assert.equal(value.capture.bytes.sqlLogicalResponseUpperBoundBytes,'0');assert.equal(value.capture.numericalEligibility,null);
 const backend=cpuCaptureHistoryBackend({noHost:true}),list=parseCpuCaptureReceiptPage(backend.read(binding),binding);assert.equal(list.coverage.missingPayloadRunCount,0);assert.equal(list.coverage.gapRunCount,3);
});
test('partial capture and independent numerical refusal remain exact rather than admitted zero CPU',()=>{
 for(const options of [{incomplete:true},{numericGap:true},{candidateGap:true}]){const {page,selection}=exact(options),value=parse(page,selection);assert.equal(value.capture.sqlProcessPercent,null);assert.equal(value.capture.operatingSystemPercent,null);assert.equal(value.capture.targetComparisonAdmitted,false);}
});
test('closed current target and exact selected tuple prevent old-revision substitution',()=>{
 const {page,selection}=exact();for(const patch of [{targetId:cpuCaptureBeta},{targetRevision:2},{runId:cpuCaptureBeta},{sampleTargetRevision:2},{fromUtc:'2026-10-02T10:00:00.123457Z'}])assert.throws(()=>parse({...page,...patch},selection));
 const bad=clone(page);bad.receipt.runId=cpuCaptureBeta;assert.throws(()=>parse(bad,selection));
});
test('absent exact receipt has no payload or invented healthy state',()=>{
 const backend=cpuCaptureHistoryBackend({detailAbsent:true}),selection=pin(cpuCaptureFixtureReceipt()),value=parse(backend.detail(binding,selection),selection);assert.equal(value.receipt,null);assert.equal(value.capture,null);assert.equal(value.reason,'capture_not_retained');
 for(const patch of [{state:'available'},{reason:'source_qualification_absent'},{capture:cpuCaptureFixtureDetail().capture}])assert.throws(()=>parse({...backend.detail(binding,selection),...patch},selection));
});
test('whole-window overflow cannot prevent independent exact run diagnostics',()=>{
 const backend=cpuCaptureHistoryBackend({count:10001}),selection=pin(cpuCaptureFixtureReceipt());assert.throws(()=>backend.read(binding),error=>error.status===422);assert.equal(parse(backend.detail(binding,selection),selection).receipt.runId,selection.runId);
});
test('full native pages retain fixed context and finish all receipt counts without duplicates or gaps',()=>{
 const backend=cpuCaptureHistoryBackend({count:201}),first=parseCpuCaptureReceiptPage(backend.read(binding),binding),second=parseCpuCaptureReceiptPage(backend.read(binding,first.nextCursor),binding,[first],first.nextCursor),third=parseCpuCaptureReceiptPage(backend.read(binding,second.nextCursor),binding,[first,second],second.nextCursor);
 assert.deepEqual([first.items.length,second.items.length,third.items.length],[100,100,1]);assert.equal(new Set([...first.items,...second.items,...third.items].map(r=>r.runId)).size,201);assert.equal(third.nextCursor,null);
 const duplicate=backend.read(binding,first.nextCursor);duplicate.items[0]=clone(first.items[0]);assert.throws(()=>parseCpuCaptureReceiptPage(duplicate,binding,[first],first.nextCursor));
 for(const patch of [{readEpoch:'b'.repeat(64)},{cutoffUtc:'2026-10-02T10:59:00Z'},{items:second.items.slice(1)},{nextCursor:first.nextCursor}])assert.throws(()=>parseCpuCaptureReceiptPage({...backend.read(binding,first.nextCursor),...patch},binding,[first],first.nextCursor));
});
test('descending native tie ordering and terminal accounting refuse loss or contradiction',()=>{
 const backend=cpuCaptureHistoryBackend(),page=backend.read(binding);assert.throws(()=>parseCpuCaptureReceiptPage({...page,items:page.items.toReversed()},binding));assert.throws(()=>parseCpuCaptureReceiptPage({...page,items:page.items.slice(1)},binding));
 for(const patch of [{runCount:4,gapRunCount:1,hasVisibilityGap:true},{completeRunCount:2,gapRunCount:1,hasVisibilityGap:true},{failedRunCount:1},{missingPayloadRunCount:1},{retainedReceiptCount:2}])assert.throws(()=>parseCpuCaptureReceiptPage({...page,coverage:{...page.coverage,...patch}},binding));
});
test('retained receipt count is independently bounded rather than guessed from public items',()=>{
 const page=cpuCaptureHistoryBackend({failedLatest:true}).read(binding);page.coverage.retainedReceiptCount=2;assert.equal(parseCpuCaptureReceiptPage(page,binding).coverage.retainedReceiptCount,2);
 page.coverage.retainedReceiptCount=4;assert.throws(()=>parseCpuCaptureReceiptPage(page,binding));
});
test('nonterminal pages cannot release gaps or captured receipts beyond declared coverage',()=>{
 const page=cpuCaptureHistoryBackend({count:200,noHost:true}).read(binding);
 assert.throws(()=>parseCpuCaptureReceiptPage({...page,state:'available',reason:'retained_captures',coverage:{...page.coverage,completeRunCount:200,gapRunCount:0,hasVisibilityGap:false}},binding));
 assert.throws(()=>parseCpuCaptureReceiptPage({...page,coverage:{...page.coverage,retainedReceiptCount:99}},binding));
});
test('closed source frames and numerical anchor contradictions are rejected',()=>{
 const {page,selection}=exact();for(const patch of [{sourceFrames:5},{state:'available'},{candidateReason:'None',candidateLocalAssociation:false},{candidateReason:'Invented'}]){const bad=clone(page);Object.assign(bad.capture,patch);assert.throws(()=>parse(bad,selection));}
 for(const patch of [{sqlProcessReason:'CounterReset'},{sqlFromNative100ns:null},{sqlToNative100ns:'9007199254740993'},{sameNativeInterval:false},{osFromNative100ns:'9007199254740994'}]){const bad=clone(page);Object.assign(bad.capture.numericalEligibility,patch);assert.throws(()=>parse(bad,selection));}
});
test('actual native statuses, raw negatives, absent counters and topology retain source truth',()=>{
 const {page,selection}=exact(),bad=clone(page);Object.assign(bad.capture.cpuBefore.processCounter,{firstValue:'-1',apiStatus:5,cStatus:4294967295});bad.capture.cpuAfter.osCounter=null;bad.capture.frameBefore.logicalProcessorsBefore=0;assert.equal(parse(bad,selection).capture.cpuBefore.processCounter.firstValue,'-1');
 for(const patch of [{rowCount4:4097},{matchingRowCount:17},{matchingRowCount:104}]){const changed=clone(page);Object.assign(changed.capture.tcpBefore,patch);assert.throws(()=>parse(changed,selection));}
});
test('helper parsed bytes and SQL logical upper bound keep independent exact accounting',()=>{
 const {page,selection}=exact();for(const patch of [{tcpBeforeHelperBytes:65537},{cpuPairHelperBytes:-1},{sqlLogicalResponseUpperBoundBytes:'16385'},{sqlLogicalResponseUpperBoundBytes:16384}]){const bad=clone(page);Object.assign(bad.capture.bytes,patch);assert.throws(()=>parse(bad,selection));}
});
test('protected cursor canonical Base64 bounds include 4096 and reject alternate padding and cycles',()=>{
 assert.equal(cpuCaptureCursor(Buffer.alloc(3072,7).toString('base64')),true);assert.equal(cpuCaptureCursor(Buffer.alloc(3073,7).toString('base64')),false);for(const value of ['', 'YQ=','YR==','a\nAAA',4])assert.equal(cpuCaptureCursor(value),false);
});
test('typed GET-only no-store transport sends exact context and independent detail tuple',async()=>{
 const previous=globalThis.fetch,backend=cpuCaptureHistoryBackend(),selection=pin(cpuCaptureFixtureReceipt()),calls=[];globalThis.fetch=async(url,init)=>{calls.push({url:new URL(String(url),'http://fixture'),init});return new Response(JSON.stringify(String(url).includes('/'+selection.runId+'?')?backend.detail(binding,selection):backend.read(binding)),{headers:{'content-type':'application/json'}});};
 try{await getCpuCaptureReceipts(binding,new AbortController().signal);await getCpuCaptureDetail(binding,selection,new AbortController().signal);assert.equal(calls.length,2);for(const call of calls){assert.equal(call.init.cache,'no-store');assert.equal(call.url.searchParams.get('targetRevision'),'1');assert.equal(call.url.searchParams.has('cutoffUtc'),false);assert.equal(call.url.searchParams.has('readEpoch'),false);}assert.equal(calls[0].url.searchParams.get('limit'),'100');assert.equal(calls[1].url.searchParams.get('sampleTargetRevision'),'1');assert.equal(calls[1].url.searchParams.has('limit'),false);assert.equal(calls[1].url.searchParams.has('cursor'),false);}finally{globalThis.fetch=previous;}
});
test('transport rejects malformed content, identity mismatch and bounded response overflow',async()=>{
 const previous=globalThis.fetch,{page,selection}=exact();try{for(const response of [new Response(JSON.stringify(page),{headers:{'content-type':'text/plain'}}),new Response(JSON.stringify({...page,runId:cpuCaptureBeta}),{headers:{'content-type':'application/json'}}),new Response('{}',{headers:{'content-type':'application/json','content-length':String(512*1024+1)}})]){globalThis.fetch=async()=>response;await assert.rejects(()=>getCpuCaptureDetail(binding,selection,new AbortController().signal));}}finally{globalThis.fetch=previous;}
});
test('pre-cancellation opens no fetch and does not manufacture an empty receipt',async()=>{
 const previous=globalThis.fetch,controller=new AbortController();let calls=0;globalThis.fetch=async()=>{calls++;return new Response('{}');};controller.abort();try{await assert.rejects(()=>getCpuCaptureReceipts(binding,controller.signal),error=>error.name==='AbortError');assert.equal(calls,0);}finally{globalThis.fetch=previous;}
});
for(const status of [401,403,404,409,422,503])test('HTTP '+status+' remains typed safe refusal without provider secret',async()=>{
 const previous=globalThis.fetch;globalThis.fetch=async()=>new Response(JSON.stringify({title:'PRIVATE_PROVIDER',detail:'PRIVATE_PROVIDER',code:'refused',status}),{status,headers:{'content-type':'application/problem+json'}});try{await assert.rejects(()=>getCpuCaptureReceipts(binding,new AbortController().signal),error=>error.status===status&&!error.message.includes('PRIVATE'));}finally{globalThis.fetch=previous;}
});
