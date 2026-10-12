import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {parseServerLifecyclePage,parseServerLifecycleDetail,verifyServerLifecycleIdentity,verifyServerLifecycleProof,serverLifecycleWindow,serverLifecycleUtc,serverLifecycleCursor,serverLifecycleBindingIdentity,serverLifecycleSelectionIdentity,serverLifecycleTimelineEvents,serverLifecycleEventIdentity} from '../src/features/health/serverLifecycleHistoryModel.ts';
import {getServerLifecycleHistory,getServerLifecycleEvent} from '../src/features/health/serverLifecycleHistoryApi.ts';
import {readServerLifecycleTimelineIdentity,readDashboardTimelineSelection} from '../src/components/dashboardTimelineSelectionModel.ts';
import {timelineEventsInWindow} from '../src/components/dashboardTimelineModel.ts';
import {serverLifecycleBinding as binding,serverLifecycleBeta,serverLifecycleHistoryBackend,serverLifecycleFixtureEvent,serverLifecycleFixtureEvidence} from '../tools/server-lifecycle-history-fixture.mjs';
const clone=value=>structuredClone(value),selection=event=>({eventId:event.eventId,sampleTargetRevision:event.sampleTargetRevision,afterRunId:event.afterRunId,detectedAtUtc:event.detectedAtUtc});
const scope={target:binding.targetId,range:'custom',from:binding.fromUtc,to:binding.toUtc,databaseId:6,compare:true};

test('native sampled changes preserve detection completion and unknown occurrence without a failover claim',async()=>{
  const page=parseServerLifecyclePage(serverLifecycleHistoryBackend().read(binding),binding);
  assert.ok(page.items.some(item=>item.kind==='restart_detected'));assert.ok(page.items.some(item=>item.kind==='responding_node_changed'));
  for(const item of page.items){await verifyServerLifecycleIdentity(item);assert.equal(item.occurredAtUtc,null);}
  const markers=serverLifecycleTimelineEvents(page.items,scope);assert.ok(markers.every(marker=>marker.kind==='server_lifecycle'&&marker.timeBasis==='detected'));
  assert.ok(markers.every(marker=>/occurrence|cause|intervening/.test(marker.detail)&&/does not prove physical failover/.test(marker.detail)));
});
test('half-open UTC windows preserve exact microseconds and native equivalent spellings',()=>{
  assert.equal(serverLifecycleUtc('2026-10-02T10:00:00.1234560+00:00'),binding.fromUtc);
  assert.doesNotThrow(()=>serverLifecycleWindow('2026-10-02T10:00:00.000001Z','2026-10-02T10:00:00.000002Z'));
  for(const pair of [[binding.toUtc,binding.fromUtc],['2026-02-30T00:00:00Z',binding.toUtc],['2026-08-01T00:00:00Z',binding.toUtc]])assert.throws(()=>serverLifecycleWindow(...pair));
  for(const value of ['2026-10-02T10:00:00.1234561Z','2026-10-02T10:00:00+01:00'])assert.throws(()=>serverLifecycleUtc(value));
});
test('target and exact event identities compare equivalent UTC spelling but fence a true one-microsecond change',()=>{
  const initial={...binding,fromUtc:'2026-10-02T10:00:00.100Z',toUtc:'2026-10-02T11:00:00.100000Z'},equivalent={...initial,targetId:initial.targetId.toUpperCase(),fromUtc:'2026-10-02T10:00:00.1+00:00',toUtc:'2026-10-02T11:00:00.1000000+00:00'};
  assert.equal(serverLifecycleBindingIdentity(initial),serverLifecycleBindingIdentity(equivalent));assert.notEqual(serverLifecycleBindingIdentity(initial),serverLifecycleBindingIdentity({...initial,fromUtc:'2026-10-02T10:00:00.100001Z'}));
  const selected=selection(serverLifecycleFixtureEvent(0,binding));assert.equal(serverLifecycleSelectionIdentity(selected),serverLifecycleSelectionIdentity({...selected,detectedAtUtc:serverLifecycleUtc(selected.detectedAtUtc)}));
  assert.notEqual(serverLifecycleSelectionIdentity(selected),serverLifecycleSelectionIdentity({...selected,sampleTargetRevision:2}));
});
test('protected cursor accepts canonical4096 and rejects invalid padding unused bits or overbound bytes',()=>{
  assert.equal(serverLifecycleCursor(Buffer.alloc(3072).toString('base64')),true);assert.equal(serverLifecycleCursor(Buffer.alloc(3073).toString('base64')),false);
  for(const value of ['',4,'YR==','YQ=','abc\n'])assert.equal(serverLifecycleCursor(value),false);
});
test('headers reject foreign target revision window and contradictory source coverage while loss can overlap complete',()=>{
  const body=serverLifecycleHistoryBackend().read(binding);
  for(const patch of [{targetId:serverLifecycleBeta},{targetRevision:2},{fromUtc:'2026-10-02T09:00:00Z'},{readEpoch:'g'.repeat(64)},{state:'healthy'},{reason:'no_source_receipts'},{extra:1}])assert.throws(()=>parseServerLifecyclePage({...body,...patch},binding));
  for(const patch of [{runCount:10001},{completeRunCount:body.coverage.completeRunCount+1},{retainedSnapshotCount:0},{availabilityQualification:'qualified'},{sourceConsistencyVerified:true},{failedRunCount:body.coverage.gapRunCount+1}]){const bad=clone(body);Object.assign(bad.coverage,patch);assert.throws(()=>parseServerLifecyclePage(bad,binding));}
  const overlap=clone(body);overlap.coverage.lostRunCount=overlap.coverage.completeRunCount;assert.equal(parseServerLifecyclePage(overlap,binding).coverage.lostRunCount,overlap.coverage.completeRunCount);
});
test('event parser refuses invented occurrence failover foreign run or nonascending observation bracket',()=>{
  const body=serverLifecycleHistoryBackend().read(binding);
  for(const patch of [{kind:'failover_detected'},{occurredAtUtc:binding.fromUtc},{sourceSchemaVersion:2},{targetId:serverLifecycleBeta},{sampleTargetRevision:2},{beforeRunId:body.items[0].afterRunId},{afterObservedAtUtc:body.items[0].beforeObservedAtUtc},{detectedAtUtc:binding.toUtc},{affectedIdentityFingerprint:'g'.repeat(64)}]){const bad=clone(body);Object.assign(bad.items[0],patch);assert.throws(()=>parseServerLifecyclePage(bad,binding));}
});
test('native hash verification rejects structurally valid forged event and copied startup identities',async()=>{
  const native=serverLifecycleFixtureEvidence(0,binding);await verifyServerLifecycleIdentity(native.event);await verifyServerLifecycleProof(native.beforeEngine);await verifyServerLifecycleProof(native.afterEngine);
  await assert.rejects(verifyServerLifecycleIdentity({...native.event,affectedIdentityFingerprint:'f'.repeat(64)}));
  await assert.rejects(verifyServerLifecycleProof({...native.beforeEngine,startupIncarnationFingerprint:'f'.repeat(64)}));
});
test('exact engine proofs preserve Int64 decimal counters and offset-free local seventh startup digit',async()=>{
  const backend=serverLifecycleHistoryBackend(),event=serverLifecycleFixtureEvent(0,binding),selected=selection(event),body=backend.detail(binding,selected),parsed=parseServerLifecycleDetail(body,binding,selected);
  assert.match(parsed.beforeEngine.sourceStartLocal,/\.\d{7}$/);assert.equal(parsed.beforeEngine.sourceStartLocal,body.beforeEngine.sourceStartLocal);
  assert.equal(parsed.afterEngine.currentMsTicks,body.afterEngine.currentMsTicks);await verifyServerLifecycleProof(parsed.beforeEngine);
  for(const patch of [{currentMsTicks:9223372036854775807},{currentMsTicks:'9223372036854775808'},{startMsTicks:'01'},{startMsTicks:'-1'},{sourceStartLocal:'2026-02-30T10:00:00.1234567'},{sourceStartLocal:'2026-01-01T10:00:00.1234567Z'},{processId:0},{coverage:'partial'}]){const bad=clone(body);Object.assign(bad.beforeEngine,patch);assert.throws(()=>parseServerLifecycleDetail(bad,binding,selected));}
});
test('native trimmed local startup fractions normalize without rounding the100ns identity proof',async()=>{
  const backend=serverLifecycleHistoryBackend(),selected=selection(serverLifecycleFixtureEvent(0,binding));
  for(const sourceStartLocal of ['2026-09-01T00:00:00','2026-09-01T00:00:00.1','2026-09-01T00:00:00.123456']){
    const body=backend.detail(binding,selected),proof=body.beforeEngine;proof.sourceStartLocal=sourceStartLocal;
    const ticks=BigInt(Date.parse(sourceStartLocal.slice(0,19)+'Z'))*10000n+BigInt((sourceStartLocal.split('.')[1]??'').padEnd(7,'0'))+621355968000000000n;
    proof.startupIncarnationFingerprint=createHash('sha256').update(`sqlobserver-lifecycle-startup-v1\0${proof.physicalNodeFingerprint}|${proof.logicalInstanceFingerprint}|${proof.processId}|${ticks}|${proof.startMsTicks}`).digest('hex');
    const parsed=parseServerLifecycleDetail(body,binding,selected);assert.equal(parsed.beforeEngine.sourceStartLocal,sourceStartLocal.slice(0,19)+'.'+(sourceStartLocal.split('.')[1]??'').padEnd(7,'0'));await verifyServerLifecycleProof(parsed.beforeEngine);
  }
});
test('restart or node-change exact proofs cannot borrow current logical or physical identities',()=>{
  const backend=serverLifecycleHistoryBackend();
  for(let index=0;index<3;index++){const selected=selection(serverLifecycleFixtureEvent(index,binding)),body=backend.detail(binding,selected);assert.ok(parseServerLifecycleDetail(body,binding,selected).event);
    const bad=clone(body);bad.afterEngine.logicalInstanceFingerprint='f'.repeat(64);assert.throws(()=>parseServerLifecycleDetail(bad,binding,selected));}
});
test('full native100 pages preserve cutoff epoch coverage order and every unique event exactly once',()=>{
  const backend=serverLifecycleHistoryBackend({count:201}),first=parseServerLifecyclePage(backend.read(binding),binding),second=parseServerLifecyclePage(backend.read(binding,first.nextCursor),binding,[first],first.nextCursor),last=backend.read(binding,second.nextCursor);
  assert.equal(parseServerLifecyclePage(last,binding,[first,second],second.nextCursor).items.length,1);
  assert.equal(new Set([...first.items,...second.items,...last.items].map(item=>item.eventId)).size,201);
  for(const patch of [{readEpoch:'f'.repeat(64)},{items:[]},{items:[first.items[0]]},{nextCursor:first.nextCursor}])assert.throws(()=>parseServerLifecyclePage({...last,...patch},binding,[first,second],second.nextCursor));
  backend.settings.epoch++;assert.throws(()=>backend.read(binding,second.nextCursor),error=>error.status===409);
});
test('native tied detection timestamps require deterministic ascending UUID without silently reordered data',()=>{
  const body=serverLifecycleHistoryBackend().read(binding);for(const event of body.items)event.detectedAtUtc=body.items.at(-1).detectedAtUtc;body.items.sort((a,b)=>a.eventId.localeCompare(b.eventId));assert.equal(parseServerLifecyclePage(body,binding).items.length,3);body.items.reverse();assert.throws(()=>parseServerLifecyclePage(body,binding));
});
test('whole-window overflow does not affect independent exact proof or permit another run tuple',()=>{
  const backend=serverLifecycleHistoryBackend({count:10001}),selected=selection(serverLifecycleFixtureEvent(0,binding));assert.throws(()=>backend.read(binding),error=>error.status===422);
  const body=backend.detail(binding,selected);assert.ok(parseServerLifecycleDetail(body,binding,selected).event);assert.equal(Object.hasOwn(body,'coverage'),false);
  for(const patch of [{afterRunId:serverLifecycleBeta},{sampleTargetRevision:2},{detectedAtUtc:binding.toUtc}])assert.throws(()=>parseServerLifecycleDetail(body,binding,{...selected,...patch}));
  backend.settings.count=201;backend.settings.epoch++;assert.equal(backend.detail(binding,selected).readEpoch,body.readEpoch);
});
test('marker identity URL binds event sample revision after-run and detection while retaining database and comparison',()=>{
  const row=serverLifecycleFixtureEvent(0,binding),marker=serverLifecycleTimelineEvents([row],scope)[0];assert.equal(marker.id,serverLifecycleEventIdentity(row));
  const identity=readServerLifecycleTimelineIdentity(marker.id);assert.deepEqual(identity,{eventId:row.eventId,sampleTargetRevision:row.sampleTargetRevision,afterRunId:row.afterRunId});
  const hash=marker.href,restored=readDashboardTimelineSelection(hash,binding.targetId);assert.equal(restored.eventId,marker.id);assert.equal(restored.timeUtc,serverLifecycleUtc(row.detectedAtUtc));
  const params=new URLSearchParams(hash.split('?')[1]);assert.equal(params.get('databaseId'),'6');assert.equal(params.get('compare'),'1');
});
test('exact old detection stays pinned alongside119 latest markers without substituting equal-time evidence',()=>{
  const rows=Array.from({length:201},(_,index)=>serverLifecycleFixtureEvent(index,binding)),markers=serverLifecycleTimelineEvents(rows,scope),old=markers[0],visible=timelineEventsInWindow(markers,binding.fromUtc,binding.toUtc,old);
  assert.equal(visible.length,120);assert.equal(visible[0].id,old.id);assert.equal(visible.at(-1).id,markers.at(-1).id);
});
test('GET-only no-store transport binds full exact proof tuple independently of list cursor and hides private failures',async()=>{
  const original=globalThis.fetch,backend=serverLifecycleHistoryBackend(),calls=[],selected=selection(serverLifecycleFixtureEvent(0,binding));
  globalThis.fetch=async(url,init)=>{calls.push({url,init});return new Response(JSON.stringify(String(url).includes('/history/')?backend.detail(binding,selected):backend.read(binding)),{headers:{'content-type':'application/json'}});};
  try{await getServerLifecycleHistory(binding,new AbortController().signal);await getServerLifecycleEvent(binding,selected,new AbortController().signal);assert.equal(calls.length,2);assert.ok(calls.every(call=>call.init.cache==='no-store'&&!call.init.body));const url=new URL(calls[1].url,'http://fixture');for(const key of ['sampleTargetRevision','afterRunId','detectedAtUtc'])assert.ok(url.searchParams.has(key));assert.equal(url.searchParams.has('cursor'),false);assert.equal(url.searchParams.has('limit'),false);}finally{globalThis.fetch=original;}
});
for(const code of [401,403,404,409,422,503])test('HTTP'+code+' remains typed without exposing provider text',async()=>{const original=globalThis.fetch;globalThis.fetch=async()=>new Response('{"detail":"PRIVATE_PROVIDER_ERROR"}',{status:code});try{await assert.rejects(getServerLifecycleHistory(binding,new AbortController().signal),error=>error.status===code&&!error.message.includes('PRIVATE'));}finally{globalThis.fetch=original;}});

test('no-source and failed-only windows remain unavailable without inventing retirement or normal health',()=>{
  const body=serverLifecycleHistoryBackend({count:0}).read(binding);assert.equal(parseServerLifecyclePage(body,binding).reason,'no_source_receipts');
  const failed=clone(body);Object.assign(failed.coverage,{runCount:1,gapRunCount:1,failedRunCount:1,comparisonBoundaryCount:1});failed.reason='source_visibility_gap';
  const parsed=parseServerLifecyclePage(failed,binding);assert.equal(parsed.state,'unavailable');assert.equal(parsed.coverage.retiredRunCount,0);assert.equal(parsed.coverage.retainedSnapshotCount,0);
  assert.throws(()=>parseServerLifecyclePage({...failed,state:'available'},binding));
});
test('missing exact proof remains null rather than borrowing another retained event',()=>{
  const backend=serverLifecycleHistoryBackend(),row=serverLifecycleFixtureEvent(0,binding),selected={...selection(row),eventId:serverLifecycleBeta},body=backend.detail(binding,selected);
  const parsed=parseServerLifecycleDetail(body,binding,selected);assert.equal(parsed.event,null);assert.equal(parsed.beforeEngine,null);assert.equal(parsed.afterEngine,null);
  assert.throws(()=>parseServerLifecycleDetail({...body,beforeEngine:serverLifecycleFixtureEvidence(0,binding).beforeEngine},binding,selected));
});
