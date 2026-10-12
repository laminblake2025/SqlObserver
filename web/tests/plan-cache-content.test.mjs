import assert from 'node:assert/strict';
import test from 'node:test';
import {getPlanCacheContent} from '../src/features/queries/queryPerformanceApi.ts';
const selection={targetId:'11111111-1111-4111-8111-111111111111',databaseId:6,databaseIncarnation:'a'.repeat(64),queryFingerprint:'b'.repeat(64),planFingerprint:'c'.repeat(64)};
const run='22222222-2222-4222-8222-222222222222',entry='d'.repeat(64);
const body={...selection,collectionRunId:run,cacheEntryFingerprint:entry,payloadKind:'text',status:'available',reason:'available',observedAtUtc:'2026-09-28T12:00:00Z',text:'<script>inert</script>',xml:null};
async function read(value,kind='text') {
 const previous=globalThis.fetch;
 try {
  globalThis.fetch=async(url,options)=>{
   assert.equal(options.cache,'no-store');
   const request=new URL(url,'http://fixture');
   assert.ok(request.pathname.endsWith(`/runs/${run}/entries/${entry}/content/${kind}`));
   assert.equal(request.searchParams.get('databaseIncarnation'),selection.databaseIncarnation);
   return new Response(JSON.stringify(value),{headers:{'content-type':'application/json'}});
  };
  return await getPlanCacheContent(selection,run,entry,kind,new AbortController().signal);
 } finally {globalThis.fetch=previous;}
}
test('exact cache content uses uncached bounded transport and returns inert strings',async()=>{
 assert.equal((await read(body)).content,body.text);
 assert.equal((await read({...body,payloadKind:'plan',text:null,xml:'<ShowPlanXML />'},'plan')).content,'<ShowPlanXML />');
 // JSON escaping can make a valid one-MiB plan exceed the old two-MiB wire cap.
 const escaped='\u0001'.repeat(1024*1024);
 assert.equal((await read({...body,payloadKind:'plan',text:null,xml:escaped},'plan')).content.length,1024*1024);
});
test('missing content preserves every bounded reason without inventing plaintext',async()=>{
 for(const reason of ['evidence_unavailable','capture_missing','capture_disabled','not_selected','source_unavailable','oversized','response_byte_limit','protection_unavailable','protection_failed'])
  assert.equal((await read({...body,status:'unavailable',reason,text:null})).reason,reason);
});
test('foreign identities, kinds, contradictory availability and oversized content are rejected',async()=>{
 for(const change of [{targetId:run},{databaseId:7},{databaseIncarnation:'e'.repeat(64)},{queryFingerprint:'e'.repeat(64)},
  {planFingerprint:'e'.repeat(64)},{collectionRunId:selection.targetId},{cacheEntryFingerprint:'e'.repeat(64)},
  {payloadKind:'plan'},{reason:'invented'},{status:'unavailable'},{reason:'not_selected'},{text:'x'.repeat(16385)},
  {xml:'unexpected'},{observedAtUtc:null}]) await assert.rejects(read({...body,...change}));
 await assert.rejects(read({...body,payloadKind:'plan',text:null,xml:'x'.repeat(1048577)},'plan'));
});
test('malformed request cannot reach the transport',async()=>{
 const previous=globalThis.fetch;
 try {
  globalThis.fetch=()=>assert.fail('Malformed cache identity reached fetch');
  for(const changed of [{databaseId:4},{databaseIncarnation:'A'.repeat(64)},{queryFingerprint:'invalid'}])
   await assert.rejects(getPlanCacheContent({...selection,...changed},run,entry,'text',new AbortController().signal));
  await assert.rejects(getPlanCacheContent(selection,run,'bad','text',new AbortController().signal));
  await assert.rejects(getPlanCacheContent(selection,run,entry,'other',new AbortController().signal));
 } finally {globalThis.fetch=previous;}
});
