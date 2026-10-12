import assert from 'node:assert/strict';
import test from 'node:test';
import { getPlanCacheIntervalHistory } from '../src/features/queries/queryPerformanceApi.ts';
const selection = { targetId:'11111111-1111-4111-8111-111111111111',databaseId:6,databaseIncarnation:'a'.repeat(64),queryFingerprint:'b'.repeat(64),planFingerprint:'c'.repeat(64) };
const fromUtc='2026-09-28T00:00:00Z',toUtc='2026-09-29T00:00:00Z';
const measured = { collectionRunId:'11111111-1111-4111-8111-000000000001',cacheEntryFingerprint:'d'.repeat(64),sourceEpoch:'e'.repeat(64),
  observedAtUtc:'2026-09-28T12:00:00Z',committedAtUtc:'2026-09-28T12:00:01Z',captureComplete:false,transition:'comparable',
  intervalFromUtc:'2026-09-28T11:00:00Z',intervalToUtc:'2026-09-28T12:00:00Z',cpuMilliseconds:'9223372036854775.80700000000000000000',
  durationMilliseconds:'0.00100000000000000000',executions:'9007199254740993',logicalReads:'0',writes:'0',rows:'0' };
const base = { ...selection,fromUtc,toUtc,snapshotUtc:toUtc,revision:1,totalRows:1,nextCursor:null,semantics:'plan_cache_delta',coverage:'observed_entries_only',items:[measured] };
async function read(body,cursor) {
  const previous=globalThis.fetch;
  try {
    globalThis.fetch=async (url,options) => {
      const request=new URL(url,'http://fixture'); assert.equal(request.searchParams.get('databaseIncarnation'),selection.databaseIncarnation);
      assert.equal(request.searchParams.get('cursor'),cursor??null); assert.equal(options.cache,'no-store');
      return new Response(JSON.stringify(body),{headers:{'content-type':'application/json'}});
    };
    return await getPlanCacheIntervalHistory(selection,fromUtc,toUtc,new AbortController().signal,cursor);
  } finally {globalThis.fetch=previous;}
}
test('cache history keeps native counter precision and unknown comparisons explicit',async()=>{
  const result=await read(base); assert.equal(result.items[0].cpuMilliseconds,measured.cpuMilliseconds); assert.equal(result.items[0].executions,measured.executions);
  const reset={...measured,transition:'reset',intervalFromUtc:null,intervalToUtc:null,cpuMilliseconds:null,durationMilliseconds:null,executions:null,logicalReads:null,writes:null,rows:null};
  assert.equal((await read({...base,items:[reset]})).items[0].executions,null);
  assert.equal((await read({...base,items:[{...reset,transition:'window_boundary',intervalFromUtc:'2026-09-27T23:00:00Z',intervalToUtc:measured.observedAtUtc}]})).items[0].transition,'window_boundary');
});
test('wrong incarnation, invented counters, precision loss and malformed continuation are rejected',async()=>{
  for(const change of [{databaseIncarnation:'f'.repeat(64)},{nextCursor:'invalid'}, {totalRows:2},
    {items:[{...measured,transition:'reset'}]}, {items:[{...measured,durationMilliseconds:'0.0001'}]},
    {items:[{...measured,executions:'9223372036854775808'}]}, {items:[{...measured,intervalFromUtc:'2026-09-27T23:00:00Z'}]},
    {items:[measured,measured],totalRows:2}]) await assert.rejects(read({...base,...change}));
});
test('continuation uses an opaque bounded token and exact explicit window',async()=>{
  const result=await read({...base,totalRows:200},'YQ.Yg'); assert.equal(result.totalRows,200);
  await assert.rejects(read(base,'x'.repeat(729)));
});
