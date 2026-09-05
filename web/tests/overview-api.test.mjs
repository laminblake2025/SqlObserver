import test from 'node:test';
import assert from 'node:assert/strict';
import {getOverview} from '../src/features/overview/overviewApi.ts';
const window={fromUtc:'2026-09-04T00:00:00Z',toUtc:'2026-09-05T00:00:00Z'};
const id='11111111-1111-4111-8111-000000000001';
const value=()=>({refreshedAtUtc:window.toUtc,...window,targetId:id,targets:[{targetId:id,displayName:'SQL 1',lifecycle:'active'}],excludedTargets:0,evidence:[{targetId:id,displayName:'SQL 1',collectionState:'unavailable',lastObservedUtc:null,activeAlerts:{value:null,state:'unavailable'},blockedSessions:{value:null,state:'unavailable'},deadlocks:{value:null,state:'unavailable'},series:[],resources:[],issues:[],gaps:[]}]});
test('one credentialed request carries the complete selected scope and window',async()=>{
 const original=globalThis.fetch;let count=0;
 try{
  globalThis.fetch=async(url,options)=>{count++;assert.equal(options.credentials,'same-origin');const query=new URL(url,'http://localhost').searchParams;assert.equal(query.get('targetId'),id);assert.equal(query.get('fromUtc'),window.fromUtc);return Response.json(value());};
  const result=await getOverview(id,window,new AbortController().signal);assert.equal(result.evidence[0].activeAlerts.value,null);assert.equal(count,1);
 }finally{globalThis.fetch=original;}
});
test('rejects wrong server, mismatched window, unsafe destinations and invalid numeric points',async()=>{
 const original=globalThis.fetch;
 try{
  const cases=[v=>v.targetId='another',v=>v.fromUtc='2026-09-03T00:00:00Z',v=>v.evidence[0].issues=[{targetId:id,destination:'javascript:alert(1)',title:'x',detail:'x',priority:0}],v=>v.evidence[0].series=[{targetId:id,label:'SQL 1',metric:'host.cpu.percent',unit:'%',points:[{timeUtc:window.fromUtc,value:-3,samples:1}]}]];
  for(const change of cases){const body=value();change(body);globalThis.fetch=async()=>Response.json(body);await assert.rejects(()=>getOverview(id,window,new AbortController().signal));}
 }finally{globalThis.fetch=original;}
});
test('permission failures produce a safe selection message',async()=>{
 const original=globalThis.fetch;
 try{globalThis.fetch=async()=>new Response('provider secret',{status:403});await assert.rejects(()=>getOverview(id,window,new AbortController().signal),/outside your access/);}
 finally{globalThis.fetch=original;}
});
