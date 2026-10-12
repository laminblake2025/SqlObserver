import test from 'node:test';
import assert from 'node:assert/strict';
import {parseCpuObservationReceiptPage as list,parseCpuObservationDetailPage as detail,previousCpuObservationWindow,cpuObservationPoints} from '../src/features/resources/cpuObservationHistoryModel.ts';
import {getCpuObservationReceipts,getCpuObservationDetail} from '../src/features/resources/cpuObservationHistoryApi.ts';
import {cpuObservationHistoryBackend,cpuObservationFixtureReceipt,cpuObservationBinding as binding} from '../tools/cpu-observation-history-fixture.mjs';
import {getCpuCollectionStatus,setCpuCollectionStatus,parseCpuCollectionStatus} from '../src/features/resources/cpuCollectionApi.ts';
const pin=r=>({runId:r.runId,sampleTargetRevision:r.sampleTargetRevision});
test('independent valid components keep distinct native intervals without invented pair eligibility',()=>{
 const backend=cpuObservationHistoryBackend(),page=list(backend.read(binding),binding),selection=pin(page.items[0]),exact=detail(backend.detail(binding,selection),binding,selection);
 assert.equal(page.state,'available');assert.equal(exact.receipt.observation.exactPairEligible,false);
 assert.notEqual(exact.receipt.observation.sqlProcess.toNative100ns,exact.receipt.observation.operatingSystem.toNative100ns);
 assert.equal(exact.receipt.observation.sqlProcess.fromNative100ns,'134000000000000001');
});
test('ProviderAdjusted and independent missing components preserve raw reason and gap instead of zero',()=>{
 const page=list(cpuObservationHistoryBackend({adjusted:true,componentGap:true}).read(binding),binding);
 assert.equal(page.items[0].observation.operatingSystem.quality,'provider_adjusted');assert.equal(page.items[0].observation.operatingSystem.rawPercent,-0.4);
 assert.equal(page.coverage.completeRunCount,1);assert.equal(page.coverage.gapRunCount,2);
 const points=cpuObservationPoints([page],'sqlProcess');assert.equal(points[1].value.percent,null);assert.equal(points[1].value.providerApiStatus,5);
 assert.equal(cpuObservationPoints([page],'operatingSystem')[1].value.percent,35);
});
test('unavailable components preserve reversed clocks and signed invalid denominators as gap evidence',()=>{
 for(const [from,to] of [['0','-1'],['10','9'],['-2','-3']]){
  const page=cpuObservationHistoryBackend({componentGap:true}).read(binding),source=page.items[1].observation.sqlProcess;
  Object.assign(source,{fromNative100ns:from,toNative100ns:to,observedToUtc:source.observedFromUtc,reason:'ClockBoundary',rawFormulaReason:'DenominatorInvalid'});
  const result=list(page,binding),point=cpuObservationPoints([result],'sqlProcess')[1];
  assert.equal(point.value.percent,null);assert.equal(point.value.quality,'unavailable');assert.equal(point.value.fromNative100ns,from);assert.equal(point.value.toNative100ns,to);
  source.observedToUtc='2026-10-02T09:00:00.1234560Z';assert.equal(list(page,binding).items[1].observation.sqlProcess.observedToUtc,source.observedToUtc);
  source.fromNative100ns='-01';assert.throws(()=>list(page,binding));
 }
});
test('closed public shapes reject private fields, mismatched targets, altered windows and inconsistent quality',()=>{
 const page=cpuObservationHistoryBackend().read(binding);
 for(const mutate of [p=>p.private='SECRET',p=>p.targetRevision=2,p=>p.fromUtc='2026-10-02T10:00:00.123457Z',p=>p.items[0].observation.hostname='PRIVATE',p=>p.items[0].observation.sqlProcess.percent=-1,p=>p.items[0].observation.sqlProcess.quality='unavailable',p=>p.items[0].observation.sqlProcess.providerApiStatus=5,p=>p.items[0].observation.sourceAssociationVerified=false,p=>p.items[0].observation.exactPairEligible=true,p=>p.items[0].observation.sqlProcess.rawFormulaEligible=false,p=>p.items[0].observation.sqlProcess.fromNative100ns='134000000010000001',p=>p.items[0].observation.sqlProcess.observedToUtc=p.items[0].observation.sqlProcess.observedFromUtc,p=>p.items[0].observation.sqlProcess.providerRawPercent=Infinity]){const bad=structuredClone(page);mutate(bad);assert.throws(()=>list(bad,binding));}
});
test('failed latest and absent exact runs never substitute older successful observations',()=>{
 for(const mode of ['failedLatest','missingLatest','lostLatest']){const backend=cpuObservationHistoryBackend({[mode]:true}),page=list(backend.read(binding),binding),selected=pin(page.items[0]),exact=detail(backend.detail(binding,selected),binding,selected);assert.equal(exact.receipt.observation,null);assert.equal(exact.state,'unavailable');}
 const backend=cpuObservationHistoryBackend({detailAbsent:true}),selected=pin(cpuObservationFixtureReceipt()),exact=detail(backend.detail(binding,selected),binding,selected);assert.equal(exact.receipt,null);assert.equal(exact.reason,'observation_not_retained');
});
test('opaque continuation validates ordering, epoch, counts, duplicate receipts and terminal accounting',()=>{
 const backend=cpuObservationHistoryBackend({count:101}),first=list(backend.read(binding),binding),second=list(backend.read(binding,first.nextCursor),binding,[first],first.nextCursor);
 assert.equal(second.items.length,1);assert.equal(second.nextCursor,null);
 for(const mutate of [p=>p.readEpoch='a'.repeat(64),p=>p.items[0]=first.items[0],p=>p.items=[],p=>p.nextCursor=first.nextCursor,p=>p.coverage.completeRunCount=100]){const bad=structuredClone(backend.read(binding,first.nextCursor));mutate(bad);assert.throws(()=>list(bad,binding,[first],first.nextCursor));}
});
test('comparison window preserves microsecond duration independently of selected run',()=>assert.deepEqual(previousCpuObservationWindow(binding.fromUtc,binding.toUtc),{fromUtc:'2026-10-02T09:00:00.123456Z',toUtc:binding.fromUtc}));
test('list and exact requests are bounded no-store reads with separate run revision binding',async()=>{
 const old=globalThis.fetch,backend=cpuObservationHistoryBackend(),selected=pin(cpuObservationFixtureReceipt()),calls=[];
 globalThis.fetch=async(url,init)=>{const u=new URL(url,'http://fixture');calls.push({u,init});return new Response(JSON.stringify(u.pathname.endsWith(selected.runId)?backend.detail(binding,selected):backend.read(binding)),{headers:{'content-type':'application/json'}});};
 try{await getCpuObservationReceipts(binding,new AbortController().signal);await getCpuObservationDetail(binding,selected,new AbortController().signal);assert.equal(calls.length,2);assert.equal(calls[0].u.searchParams.get('limit'),'100');assert.equal(calls[1].u.searchParams.get('sampleTargetRevision'),'1');assert.equal(calls[1].u.searchParams.has('limit'),false);assert.ok(calls.every(c=>c.init.cache==='no-store'));}finally{globalThis.fetch=old;}
});
test('collection status and writes require exact context and read expected enabled state',async()=>{
 const status={targetId:binding.targetId,targetRevision:1,collectorEnabled:false,scheduleRevision:1};assert.deepEqual(parseCpuCollectionStatus(status,binding.targetId,1),status);
 for(const patch of [{scheduleRevision:0},{targetRevision:2},{private:'SECRET'},{collectorEnabled:'false'}])assert.throws(()=>parseCpuCollectionStatus({...status,...patch},binding.targetId,1));
 const old=globalThis.fetch,calls=[];globalThis.fetch=async(url,init)=>{calls.push({url,init});return new Response(JSON.stringify({...status,collectorEnabled:init.method==='PUT'}),{headers:{'content-type':'application/json'}});};
 try{await getCpuCollectionStatus(binding.targetId,1,new AbortController().signal);await setCpuCollectionStatus(binding.targetId,1,false,true,new AbortController().signal);assert.equal(calls[0].init.method,'GET');assert.deepEqual(JSON.parse(calls[1].init.body),{expectedTargetRevision:1,expectedEnabled:false,enabled:true});}finally{globalThis.fetch=old;}
});
test('unknown write outcomes are explicit and never automatically retried',async()=>{
 const old=globalThis.fetch;let calls=0;globalThis.fetch=async()=>{calls++;throw Error('network');};try{await assert.rejects(setCpuCollectionStatus(binding.targetId,1,false,true,new AbortController().signal),e=>e.outcomeUnknown===true);assert.equal(calls,1);}finally{globalThis.fetch=old;}
});
