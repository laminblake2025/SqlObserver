import test from 'node:test';
import assert from 'node:assert/strict';
import {parseAgentActivityReceiptPage as list,parseAgentActivityDetailPage as detail,agentActivityTicks,agentActivityWindow,agentActivityLocal,agentActivityContextIdentity} from '../src/features/operations/agentActivityHistoryModel.ts';
import {getAgentActivityReceipts,getAgentActivityDetail} from '../src/features/operations/agentActivityHistoryApi.ts';
import {agentActivityHistoryBackend,agentActivityHistoryReceipt,agentActivityHistoryBinding as binding,agentActivityHistoryBeta as beta} from '../tools/agent-activity-history-fixture.mjs';
const pin=row=>({runId:row.runId,sampleTargetRevision:row.sampleTargetRevision}),clone=value=>structuredClone(value);
test('summary receipts omit jobs; exact stopped capture retains Unknown candidates and all seven local digits',()=>{
 const backend=agentActivityHistoryBackend({localReversal:true}),page=list(backend.read(binding),binding),exact=detail(backend.detail(binding,pin(page.items[0])),binding,pin(page.items[0]));
 assert.equal('jobs' in page.items[0],false);assert.equal('capture' in page.items[0],false);assert.equal(exact.capture.gap,'ServiceNotRunning');assert.equal(exact.capture.jobs[0].candidateState,'Unknown');
 assert.equal(exact.capture.jobs[0].startExecutionLocal,'2026-11-01T01:30:00.1234567');assert.equal(exact.capture.jobs[0].stopExecutionLocal,'2026-11-01T00:30:00.1234567');assert.equal('elapsedSeconds' in exact.capture.jobs[0],false);assert.equal(exact.productionAdmitted,false);
});
test('source UTC comparisons retain 100 ns while public completion windows require microsecond alignment',()=>{
 assert.equal(agentActivityTicks('2026-10-02T10:00:00.1234568Z')-agentActivityTicks('2026-10-02T10:00:00.1234567Z'),1n);
 assert.throws(()=>agentActivityWindow('2026-10-02T10:00:00.1234567Z',binding.toUtc));
 assert.equal(agentActivityContextIdentity(binding),agentActivityContextIdentity({...binding,fromUtc:binding.fromUtc.replace('0Z','+00:00')}));
 assert.throws(()=>agentActivityWindow('2026-02-30T00:00:00Z',binding.toUtc));assert.throws(()=>agentActivityLocal('2026-02-30T01:00:00.0000000'));assert.throws(()=>agentActivityLocal('2026-11-01T01:30:00.1234567Z'));
 const shifted={...binding,toUtc:'2026-10-02T11:00:00.123457Z'},backend=agentActivityHistoryBackend({count:101}),page=list(backend.read(shifted),shifted),selected=pin(page.items[0]);
 assert.equal(agentActivityTicks(page.items[0].completedAtUtc)%10n,0n);assert.equal(agentActivityTicks(detail(backend.detail(shifted,selected),shifted,selected).capture.before.sourceObservedAtUtc)%10n,1n);
});
test('31-day activity window remains independent of 7-day failure history',()=>{
 assert.doesNotThrow(()=>agentActivityWindow('2026-09-01T00:00:00Z','2026-10-02T00:00:00Z'));assert.throws(()=>agentActivityWindow('2026-09-01T00:00:00Z','2026-10-02T00:00:00.000001Z'));
});
test('latest failed and missing captures remain explicit instead of substituting older success',()=>{
 for(const option of ['failedLatest','missingLatest','retiredLatest']){const backend=agentActivityHistoryBackend({[option]:true}),page=list(backend.read(binding),binding),selected=pin(page.items[0]),exact=detail(backend.detail(binding,selected),binding,selected);assert.equal(exact.capture,null);assert.equal(exact.runId,page.items[0].runId);assert.equal(exact.reason,option==='failedLatest'?'collection_failed':'payload_missing');assert.equal(page.coverage.gapRunCount,1);}
});
test('513-row sentinel retains 512 jobs with source loss and no qualified Running candidate',()=>{
 const backend=agentActivityHistoryBackend({truncatedLatest:true,serviceRunning:true}),page=list(backend.read(binding),binding),selected=pin(page.items[0]),exact=detail(backend.detail(binding,selected),binding,selected);
 assert.equal(exact.capture.jobs.length,512);assert.equal(exact.capture.sourceRowsRead,513);assert.equal(exact.capture.gap,'SourceTruncated');assert.equal(exact.capture.jobs.at(-1).candidateState,'Unknown');assert.equal(exact.receipt.countIsExact,false);
});
test('ordered summary pagination preserves epoch and totals without bulk captures',()=>{
 const backend=agentActivityHistoryBackend({count:101}),first=list(backend.read(binding),binding),second=list(backend.read(binding,first.nextCursor),binding,[first],first.nextCursor);
 assert.equal(first.items.length,100);assert.equal(second.items.length,1);assert.equal(second.nextCursor,null);assert.equal(first.coverage.runCount,101);
 const wrong=backend.read(binding,first.nextCursor);wrong.items[0]=clone(first.items[0]);assert.throws(()=>list(wrong,binding,[first],first.nextCursor));
 const changed=backend.read(binding,first.nextCursor);changed.readEpoch='f'.repeat(64);assert.throws(()=>list(changed,binding,[first],first.nextCursor));
});
test('exact off-page selection reads independently even when list unavailable',()=>{
 const backend=agentActivityHistoryBackend({count:101}),selected=pin(agentActivityHistoryReceipt(100));assert.equal(detail(backend.detail(binding,selected),binding,selected).runId,selected.runId);
 const foreignBinding={...binding,targetId:beta},foreign=detail(backend.detail(foreignBinding,selected),foreignBinding,selected);assert.equal(foreign.reason,'observation_not_retained');assert.equal(foreign.receipt,null);assert.equal(foreign.capture,null);
 backend.settings.count=10001;assert.throws(()=>backend.read(binding));assert.equal(detail(backend.detail(binding,selected),binding,selected).runId,selected.runId);
 backend.settings.detailAbsent=true;assert.equal(detail(backend.detail(binding,selected),binding,selected).reason,'observation_not_retained');
});
test('closed shapes reject private authority, fabricated metrics and jobs in summary',()=>{
 const backend=agentActivityHistoryBackend(),page=backend.read(binding),selected=pin(page.items[0]),exact=backend.detail(binding,selected);
 for(const mutation of [p=>p.productionAdmitted=true,p=>p.items[0].jobs=[],p=>p.items[0].enrollmentId='private',p=>p.items[0].capturePresent=false,p=>p.coverage.completeSourceRunCount=0]){const copy=clone(page);mutation(copy);assert.throws(()=>list(copy,binding));}
 for(const mutation of [p=>p.capture.expectedWindowsSid='private',p=>p.capture.enrollmentId='private',p=>p.capture.jobs[0].elapsedSeconds=60,p=>p.capture.jobs[0].candidateState='Running',p=>p.capture.gap='None',p=>p.capture.after.sourceObservedAtUtc=p.capture.before.sourceObservedAtUtc.replace(/\dZ$/,'0Z'),p=>p.capture.jobs[1].jobId=p.capture.jobs[0].jobId,p=>p.capture.serviceSessionAssociationQualified=true]){const copy=clone(exact);mutation(copy);assert.throws(()=>detail(copy,binding,selected));}
});
test('exact result must bind run/sample revision/window and missing result cannot carry source',()=>{
 const backend=agentActivityHistoryBackend(),selected=pin(agentActivityHistoryReceipt()),value=backend.detail(binding,selected);
 assert.throws(()=>detail(value,{...binding,targetRevision:2},selected));assert.throws(()=>detail(value,binding,{...selected,sampleTargetRevision:2}));
 const missing={...clone(value),receipt:null,state:'unavailable',reason:'observation_not_retained'};assert.throws(()=>detail(missing,binding,selected));
 const corrupt=clone(value);corrupt.receipt.completedAtUtc=binding.toUtc;assert.throws(()=>detail(corrupt,binding,selected));
});
test('API requests keep independent exact path, no-store, bounded query and never expose provider text',async()=>{
 const backend=agentActivityHistoryBackend(),selected=pin(agentActivityHistoryReceipt()),calls=[],original=globalThis.fetch;
 try{globalThis.fetch=async(url,options)=>{calls.push({url:String(url),options});return new Response(JSON.stringify(String(url).includes(selected.runId)?backend.detail(binding,selected):backend.read(binding)),{headers:{'content-type':'application/json'}});};
 await getAgentActivityReceipts(binding,new AbortController().signal);await getAgentActivityDetail(binding,selected,new AbortController().signal);assert.equal(calls[0].options.cache,'no-store');assert.match(calls[0].url,/limit=100/);assert.match(calls[1].url,new RegExp('/'+selected.runId+'\\?'));assert.equal(calls[1].url.includes('limit='),false);
 globalThis.fetch=async()=>new Response(JSON.stringify({detail:'SECRET_PROVIDER'}),{status:403,headers:{'content-type':'application/json'}});await assert.rejects(getAgentActivityDetail(binding,selected,new AbortController().signal),e=>e.status===403&&!e.message.includes('SECRET_PROVIDER'));
 }finally{globalThis.fetch=original;}
});
