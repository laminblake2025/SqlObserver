import test from 'node:test';
import assert from 'node:assert/strict';
import { getLifecycleCollectionStatus as read, setLifecycleCollectionStatus as write, parseLifecycleCollectionStatus as parse, LifecycleCollectionWriteError } from '../src/features/health/lifecycleCollectionApi.ts';
const targetId='11111111-1111-4111-8111-000000000001', value=()=>({targetId,targetRevision:2,scheduleRevision:7,collectorEnabled:false});
const json=(body,status=200)=>new Response(JSON.stringify(body),{status,headers:{'content-type':'application/json'}});
const signal=()=>new AbortController().signal;
async function mock(fetch,body){const previous=globalThis.fetch;globalThis.fetch=fetch;try{await body();}finally{globalThis.fetch=previous;}}
test('closed lifecycle status binds exact target revision and positive safe schedule revision',()=>{
 assert.deepEqual(parse(value(),targetId,2),value());
 for(const edit of [v=>v.extra='private',v=>v.targetId='11111111-1111-4111-8111-000000000002',v=>v.targetRevision=1,v=>v.targetRevision='2',v=>v.scheduleRevision=0,v=>v.scheduleRevision=1.5,v=>v.scheduleRevision=Number.MAX_SAFE_INTEGER+1,v=>v.collectorEnabled='false']){const v=value();edit(v);assert.throws(()=>parse(v,targetId,2));}
 assert.throws(()=>parse(value(),targetId,0));assert.throws(()=>parse([],targetId,2));
});
test('GET is bounded no-store and PUT sends both CAS revisions and expected enabled state',async()=>{
 const calls=[];
 await mock(async(url,init)=>{calls.push({url,init});return json(init.method==='PUT'?{...value(),scheduleRevision:8,collectorEnabled:true}:value());},async()=>{
  const state=await read(targetId,2,signal());await write(state,true,signal());
 });
 assert.equal(calls[0].url,`/api/v1/observation-targets/${targetId}/lifecycle/collection?targetRevision=2`);
 assert.equal(calls[0].init.cache,'no-store');assert.equal(calls[1].init.method,'PUT');assert.equal(calls[1].init.credentials,'same-origin');
 assert.deepEqual(JSON.parse(calls[1].init.body),{expectedTargetRevision:2,expectedScheduleRevision:7,expectedEnabled:false,enabled:true});
});
test('changed write requires exactly one schedule revision; no-op retains revision',async()=>{
 for(const revision of [7,9])await mock(async()=>json({...value(),collectorEnabled:true,scheduleRevision:revision}),async()=>assert.rejects(write(value(),true,signal()),e=>e instanceof LifecycleCollectionWriteError&&e.outcomeUnknown));
 await mock(async()=>json(value()),async()=>assert.deepEqual(await write(value(),false,signal()),value()));
});
for(const status of [401,403,404,409,429])test(`write ${status} clears authority/state through a classified refusal without retry`,async()=>{
 let calls=0;await mock(async()=>{calls++;return json({private:'not displayed'},status);},async()=>assert.rejects(write(value(),true,signal()),e=>e instanceof LifecycleCollectionWriteError&&e.status===status&&!e.outcomeUnknown&&!e.message.includes('private')));assert.equal(calls,1);
});
test('network and server write failures remain unknown and are never retried',async()=>{
 for(const response of [()=>{throw new TypeError('SECRET provider error');},()=>json({},500)]){let calls=0;await mock(async()=>{calls++;return response();},async()=>assert.rejects(write(value(),true,signal()),e=>e.outcomeUnknown&&!e.message.includes('SECRET')));assert.equal(calls,1);}
});
test('wrong content type, overbound body and malformed successful receipts are unknown writes',async()=>{
 for(const response of [()=>new Response('{}',{headers:{'content-type':'text/html'}}),()=>json({...value(),collectorEnabled:true,scheduleRevision:8,large:'x'.repeat(17000)}),()=>json({...value(),scheduleRevision:8}),()=>json({...value(),collectorEnabled:true,scheduleRevision:8,targetRevision:3})])await mock(async()=>response(),async()=>assert.rejects(write(value(),true,signal()),e=>e.outcomeUnknown));
});
test('invalid input and already aborted operations never send a request',async()=>{
 let calls=0;await mock(async()=>{calls++;return json(value());},async()=>{await assert.rejects(write({...value(),scheduleRevision:0},true,signal()));const stop=new AbortController();stop.abort();await assert.rejects(write(value(),true,stop.signal));});assert.equal(calls,0);
});
test('read denial carries HTTP status without accepting untrusted response text',async()=>{
 await mock(async()=>json({detail:'SECRET'},403),async()=>assert.rejects(read(targetId,2,signal()),e=>e.status===403&&!e.message.includes('SECRET')));
});
