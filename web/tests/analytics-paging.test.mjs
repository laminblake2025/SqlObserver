import test from 'node:test';
import assert from 'node:assert/strict';
import { analyticsInventory, analyticsRetainedRowCount, maximumAnalyticsPages, maximumAnalyticsRows,
  validateAnalyticsReceipt } from '../src/features/analytics/analyticsSurfacePaging.ts';

const target='11111111-1111-4111-8111-000000000001';
const from='2026-09-26T08:00:00.1234567Z', to='2026-09-26T12:00:00.6543210Z';
const snapshot='2026-09-30T12:00:00.0000000Z';
const id=index=>`22222222-2222-4222-8222-${String(index).padStart(12,'0')}`;
const row=index=>({jobId:id(index),jobKind:'rollup',status:'succeeded',requestedAtUtc:new Date(Date.parse('2026-09-25T08:00:00Z')+index*1000).toISOString(),startedAtUtc:null,completedAtUtc:null,attempt:1,targetRevision:3});
const encode=value=>Buffer.from(JSON.stringify(value)).toString('base64url');
function cursorFor(page) {
  const last=page.items.at(-1);
  return encode({kind:'m10.surface.v1',surface:page.surface,targetId:target,targetRevision:3,generation:8,
    snapshotUtc:snapshot,sourceCutoffUtc:snapshot,fromUtc:from,toUtc:to,filters:{},
    tieAt:last.requestedAtUtc,tieId:last.jobId,tieMetric:null,tieDimensions:null,tieFingerprint:null});
}
function firstPage() {
  const page={targetId:target,surface:'jobs',items:Array.from({length:100},(_,index)=>row(index+1)),state:'complete',
    targetRevision:3,generation:8,snapshotUtc:snapshot,cutoffUtc:snapshot,fromUtc:from,toUtc:to,nextCursor:null};
  page.nextCursor=cursorFor(page); return page;
}
const receipt=page=>({page,receivedAtUtc:snapshot});
function secondPage(first) { return {...first,items:[row(101)],nextCursor:null}; }
function validate(page,prior=[],cursor,window={fromUtc:from,toUtc:to}) {
  return validateAnalyticsReceipt(page,'jobs',target,3,window,prior,cursor);
}

test('inventory window does not filter job timestamps; native continuation retains its effective window',()=>{
  const first=firstPage();validate(first);
  const prior=[receipt(first)];validate(secondPage(first),prior,first.nextCursor);
  assert.equal(analyticsRetainedRowCount([...prior,receipt(secondPage(first))]),101);
  assert.equal(analyticsInventory('jobs'),true);assert.equal(analyticsInventory('backfill'),true);
  assert.equal(analyticsInventory('diagnostics/search'),false);
});
test('equivalent seven-digit UTC offsets bind while a changed100ns boundary does not',()=>{
  const first=firstPage();validate({...first,fromUtc:'2026-09-26T08:00:00.1234567+00:00'});
  assert.throws(()=>validate({...first,fromUtc:'2026-09-26T08:00:00.1234568Z'}),/requested window/);
});
test('continuations reject changed generation, revision, snapshot, cutoff and window without altering receipts',()=>{
  const first=firstPage(),prior=[receipt(first)],next=secondPage(first);
  for(const patch of [{generation:9},{targetRevision:4},{snapshotUtc:'2026-09-30T12:00:01Z'},
    {cutoffUtc:'2026-09-30T11:59:59Z'},{toUtc:'2026-09-26T12:00:00.6543211Z'}]) {
    assert.throws(()=>validate({...next,...patch},prior,first.nextCursor));
    assert.equal(prior.length,1);assert.equal(prior[0].page.items.length,100);
  }
});
test('a continuation request must be the previous page native cursor',()=>{
  const first=firstPage(),prior=[receipt(first)];
  const rebound=encode({...JSON.parse(Buffer.from(first.nextCursor,'base64url').toString()),surface:'backfill'});
  assert.throws(()=>validate(secondPage(first),prior,rebound),/continuation changed/);
  assert.throws(()=>validate(secondPage(first),[],first.nextCursor),/continuation changed/);
});
test('reused cursors and duplicate job identities are rejected',()=>{
  const first=firstPage(),prior=[receipt(first)],next=secondPage(first);
  assert.throws(()=>validate({...next,nextCursor:first.nextCursor},prior,first.nextCursor),/repeated.*cursor/);
  assert.throws(()=>validate({...next,items:[{...row(101),jobId:id(50)}]},prior,first.nextCursor),/repeated received evidence/);
});
test('cross-page native time ordering is enforced for job inventory',()=>{
  const first=firstPage(),next=secondPage(first);
  assert.throws(()=>validate({...next,items:[{...row(101),requestedAtUtc:'2026-09-25T07:59:00Z'}]},[receipt(first)],first.nextCursor),/native ordering boundary/);
});
test('legacy incident list has no synthesized continuation or revision receipt',()=>{
  const page={targetId:target,surface:'incidents',items:[],state:'no_data',nextCursor:null,fromUtc:from,toUtc:to};
  validateAnalyticsReceipt(page,'incidents',target,3,{fromUtc:from,toUtc:to},[]);
  assert.throws(()=>validateAnalyticsReceipt({...page,nextCursor:'opaque'},'incidents',target,3,{fromUtc:from,toUtc:to},[]),/does not provide continuation/);
});
test('retained pages and rows have finite independent bounds',()=>{
  assert.equal(maximumAnalyticsPages,20);assert.equal(maximumAnalyticsRows,4000);
  const first=firstPage(),prior=Array.from({length:20},()=>receipt({...first,items:[]}));
  assert.throws(()=>validate({...first,nextCursor:null},prior),/limit reached/);
  const full=[receipt({...first,items:Array.from({length:4000},(_,index)=>row(index+1))})];
  assert.throws(()=>validate({...first,items:[row(4001)],nextCursor:null},full),/limit reached/);
});
test('replication visible ties are not treated as invented topology identities',()=>{
  const observedAtUtc='2026-09-26T09:00:00Z';
  const item={observedAtUtc,runId:id(1),role:'publisher',targetRevision:3};
  const page={targetId:target,surface:'replication/status',items:[item,item],state:'complete',nextCursor:null,
    fromUtc:from,toUtc:to,snapshotUtc:snapshot,cutoffUtc:snapshot,generation:8,targetRevision:3};
  validateAnalyticsReceipt(page,'replication/status',target,3,{fromUtc:from,toUtc:to},[]);
});
