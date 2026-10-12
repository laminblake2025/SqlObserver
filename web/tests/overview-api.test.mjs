import test from 'node:test';
import assert from 'node:assert/strict';
import {getOverview} from '../src/features/overview/overviewApi.ts';
import {clearApiCache} from '../src/apiClient.ts';
const window={fromUtc:'2026-09-04T00:00:00Z',toUtc:'2026-09-05T00:00:00Z'};
const id='11111111-1111-4111-8111-000000000001';
const value=()=>({refreshedAtUtc:window.toUtc,...window,targetId:id,targets:[{targetId:id,displayName:'SQL 1',lifecycle:'active',environment:null,tags:[],groupingRevision:0,canManageGrouping:false}],excludedTargets:0,evidence:[{targetId:id,displayName:'SQL 1',collectionState:'unavailable',lastObservedUtc:null,activeAlerts:{value:null,state:'unavailable'},blockedSessions:{value:null,state:'unavailable'},deadlocks:{value:null,state:'unavailable'},series:[],resources:[],issues:[],gaps:[]}]});
test('legacy grouping is explicitly unavailable and never enables editing',async()=>{
 const original=globalThis.fetch;
 try{
  const body=value();body.targets[0]={targetId:id,displayName:'SQL 1',lifecycle:'active'};
  clearApiCache();globalThis.fetch=async()=>Response.json(body);
  const result=await getOverview(id,window,new AbortController().signal);
  assert.equal(result.targets[0].groupingAvailable,false);
  assert.equal(result.targets[0].canManageGrouping,false);
  assert.equal(result.evidence[0].activeAlerts.value,null);
  assert.deepEqual(result.targets[0].tags,[]);
 }finally{globalThis.fetch=original;clearApiCache();}
});
test('complete modern grouping remains authoritative; partial or malformed grouping is rejected',async()=>{
 const original=globalThis.fetch;
 try{
  const body=value();body.targets[0].environment='production';body.targets[0].tags=['east'];body.targets[0].groupingRevision=7;body.targets[0].canManageGrouping=true;
  clearApiCache();globalThis.fetch=async()=>Response.json(body);
  const result=await getOverview(id,window,new AbortController().signal);
  assert.equal(result.targets[0].groupingAvailable,true);assert.equal(result.targets[0].groupingRevision,7);assert.equal(result.targets[0].canManageGrouping,true);
  for(const key of ['environment','tags','groupingRevision','canManageGrouping']){
   for(const mode of ['omit','invalid']){
    const invalid=value();if(mode==='omit')delete invalid.targets[0][key];else invalid.targets[0][key]=key==='environment'?42:null;
    clearApiCache();globalThis.fetch=async()=>Response.json(invalid);
    await assert.rejects(()=>getOverview(id,window,new AbortController().signal),/invalid target grouping/);
   }
  }
 }finally{globalThis.fetch=original;clearApiCache();}
});
test('one credentialed request carries the complete selected scope and window',async()=>{
 const original=globalThis.fetch;let count=0;
 try{
  globalThis.fetch=async(url,options)=>{count++;assert.equal(options.credentials,'same-origin');const query=new URL(url,'http://localhost').searchParams;assert.equal(query.get('targetId'),id);assert.equal(query.get('fromUtc'),window.fromUtc);assert.equal(query.get('useLatestStatus'),'true');return Response.json(value());};
  const result=await getOverview(id,window,new AbortController().signal);assert.equal(result.evidence[0].activeAlerts.value,null);assert.equal(count,1);
 }finally{globalThis.fetch=original;}
});
test('accepts database-dimension workload series',async()=>{
 const original=globalThis.fetch;
 try{
  const body=value();body.evidence[0].series=[{targetId:id,label:'SQL 1',metric:'activity.user_sessions',unit:'sessions',state:'observed',dimension:'Orders · database 5',points:[{timeUtc:window.fromUtc,value:3,samples:4}]}];
  globalThis.fetch=async()=>Response.json(body);
  const result=await getOverview(id,window,new AbortController().signal);
  assert.equal(result.evidence[0].series[0].dimension,'Orders · database 5');
 }finally{globalThis.fetch=original;}
});
test('rejects wrong server, mismatched window, unsafe destinations and invalid numeric points',async()=>{
 const original=globalThis.fetch;
 try{
  const cases=[v=>v.targetId='another',v=>v.fromUtc='2026-09-03T00:00:00Z',v=>v.targets[0].tags=null,v=>v.evidence[0].issues=[{targetId:id,destination:'javascript:alert(1)',title:'x',detail:'x',priority:0}],v=>v.evidence[0].series=[{targetId:id,label:'SQL 1',metric:'host.cpu.percent',unit:'%',points:[{timeUtc:window.fromUtc,value:-3,samples:1}]}]];
  for(const change of cases){const body=value();change(body);globalThis.fetch=async()=>Response.json(body);await assert.rejects(()=>getOverview(id,window,new AbortController().signal));}
 }finally{globalThis.fetch=original;}
});
test('permission failures produce a safe selection message',async()=>{
 const original=globalThis.fetch;
 try{globalThis.fetch=async()=>new Response('provider secret',{status:403});await assert.rejects(()=>getOverview(id,window,new AbortController().signal),/outside your access/);}
 finally{globalThis.fetch=original;}
});

test('native source-check clocks preserve seven fractional digits without relaxing canonical request bounds', async () => {
 const original=globalThis.fetch;
 try {
  for (const stamp of ['2026-10-01T12:00:00.1234567Z','2026-10-01T12:00:00.1234567+00:00']) {
   const body=value();body.evidence[0].resources=[{targetId:id,server:'SQL 1',label:'Collection source check',unit:'state',state:'unavailable',value:null,observedAtUtc:stamp}];
   clearApiCache();globalThis.fetch=async()=>Response.json(body);
   assert.equal((await getOverview(id,window,new AbortController().signal)).evidence[0].resources[0].observedAtUtc,stamp);
  }
  for (const stamp of ['2026-02-30T12:00:00.1234567Z','2026-10-01T12:00:00.12345678Z','2026-10-01T12:00:00.1234567-04:00']) {
   const body=value();body.evidence[0].resources=[{targetId:id,server:'SQL 1',label:'Source',unit:'state',state:'unavailable',value:null,observedAtUtc:stamp}];
   clearApiCache();globalThis.fetch=async()=>Response.json(body);
   await assert.rejects(()=>getOverview(id,window,new AbortController().signal),/Invalid Overview resource/);
  }
  const body=value();body.fromUtc='2026-10-01T11:00:00.1234567Z';clearApiCache();globalThis.fetch=async()=>Response.json(body);
  await assert.rejects(()=>getOverview(id,window,new AbortController().signal));
 } finally {globalThis.fetch=original;clearApiCache();}
});
