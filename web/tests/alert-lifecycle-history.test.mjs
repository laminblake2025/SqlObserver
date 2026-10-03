import test from 'node:test';
import assert from 'node:assert/strict';
import {parseAlertLifecyclePage,parseAlertLifecycleDetail,lifecycleWindow,lifecycleCursor,lifecycleUtc,lifecycleEventIdentity,lifecycleBindingIdentity,lifecycleSelectionIdentity,alertLifecycleTimelineEvents,lifecycleKinds} from '../src/features/health/alertLifecycleHistoryModel.ts';
import {getAlertLifecycleHistory,getAlertLifecycleEvent} from '../src/features/health/alertLifecycleHistoryApi.ts';
import {timelineEventsInWindow} from '../src/components/dashboardTimelineModel.ts';
import {alertLifecycleHistoryBackend,lifecycleFixtureEvent,lifecycleAlpha,lifecycleBeta,lifecycleFromUtc,lifecycleToUtc,lifecycleUuid} from '../tools/alert-lifecycle-history-fixture.mjs';
const binding={targetId:lifecycleAlpha,targetRevision:1,fromUtc:lifecycleFromUtc,toUtc:lifecycleToUtc},scope={target:lifecycleAlpha,range:'custom',from:lifecycleFromUtc,to:lifecycleToUtc,compare:false},clone=v=>structuredClone(v);
const selection=e=>({eventId:e.eventId,alertId:e.alertId,episodeId:e.episodeId,eventAtUtc:e.eventAtUtc});
test('all eight native verbs preserve immutable cycle/time metadata independently of active or resolved state',()=>{
 const page=parseAlertLifecyclePage(alertLifecycleHistoryBackend().read(binding),binding);assert.deepEqual(new Set(page.items.map(e=>e.kind)),new Set(lifecycleKinds));assert.equal(page.coverage.cycleCount,2);assert.notEqual(page.items[0].alertId,page.items[8].alertId);assert.equal(page.items[7].toState,'resolved');
 assert.equal(page.items[4].assigneeSid,page.items[4].previousAssigneeSid);assert.equal(page.items[4].kind,'reassigned');
 const events=alertLifecycleTimelineEvents(page.items,scope);assert.equal(events.length,16);assert.ok(events.every(e=>e.href.includes('timelineEvent=alert-history')));assert.ok(events.some(e=>e.timeBasis==='observed'));assert.ok(events.some(e=>e.timeBasis==='applied'));
});
test('half-open windows retain microseconds and reject invalid calendar, reversed and oversized ranges',()=>{
 assert.deepEqual(lifecycleWindow(lifecycleFromUtc,lifecycleToUtc),{fromUtc:lifecycleFromUtc,toUtc:lifecycleToUtc});assert.equal(lifecycleUtc('2026-10-02T10:00:00.1234560+00:00'),lifecycleFromUtc);
 for(const pair of [[lifecycleToUtc,lifecycleFromUtc],['2026-02-30T00:00:00Z',lifecycleToUtc],['2026-08-01T00:00:00Z',lifecycleToUtc]])assert.throws(()=>lifecycleWindow(...pair));
});
test('legacy partial null metadata remains unknown and never borrows current rule or episode',()=>{
 const page=parseAlertLifecyclePage(alertLifecycleHistoryBackend({legacy:true}).read(binding),binding);const row=page.items[0];assert.equal(page.state,'partial');for(const key of ['episodeId','ruleId','recordedAtUtc','ruleName','stateRevision'])assert.equal(row[key],null);
 const bad=clone(page);bad.items[0].provenance='complete';assert.throws(()=>parseAlertLifecyclePage(bad,binding));assert.match(alertLifecycleTimelineEvents([row],scope)[0].detail,/never|not filled from current/);
});
test('recording cutoff and source revision are fenced while source-observed clock time can exceed cutoff',()=>{
 const body=alertLifecycleHistoryBackend({count:1}).read(binding);body.cutoffUtc='2026-10-02T10:00:00.123456Z';body.repositoryTimeUtc=body.cutoffUtc;body.items[0].recordedAtUtc=body.cutoffUtc;assert.ok(parseAlertLifecyclePage(body,binding).items[0].eventAtUtc>body.cutoffUtc);
 for(const patch of [{recordedAtUtc:'2026-10-03T13:00:00Z'},{sourceTargetRevision:2}]){const bad=clone(body);Object.assign(bad.items[0],patch);assert.throws(()=>parseAlertLifecyclePage(bad,binding));}
});
test('native semantic source, verb, state, deadline and owner-pair invariants reject invented lifecycle receipts',()=>{
 const page=alertLifecycleHistoryBackend().read(binding),patches=[{kind:'expiry'},{timeBasis:'occurred'},{source:'assignment'},{source:'evaluation',timeBasis:'applied'},{toState:'normal'},{fromState:'firing'},{snoozedUntilUtc:lifecycleToUtc},{ruleId:null},{value:Infinity},{ruleName:'x'.repeat(129)},{actorSid:'PRIVATE\\name'}];
 for(const patch of patches){const bad=clone(page);Object.assign(bad.items[0],patch);assert.throws(()=>parseAlertLifecyclePage(bad,binding));}
 for(const [index,patch] of [[1,{snoozedUntilUtc:null}],[3,{assigneeName:null}],[4,{previousAssigneeSid:null}],[5,{assigneeSid:'S-1-5-1',assigneeName:'Name'}]]){const bad=clone(page);Object.assign(bad.items[index],patch);assert.throws(()=>parseAlertLifecyclePage(bad,binding));}
});
test('reason bound is native UTF8 and retained actors/owners require normalized nonempty SID segments',()=>{
 const body=alertLifecycleHistoryBackend().read(binding);body.items[0].reason='é'.repeat(4096);body.items[0].actorSid='S-1-5-1';assert.equal(parseAlertLifecyclePage(body,binding).items[0].reason.length,4096);
 for(const actorSid of ['S-1-','S-1-5','S-1-5--1','S-1-5-1-']){const bad=clone(body);bad.items[0].actorSid=actorSid;assert.throws(()=>parseAlertLifecyclePage(bad,binding));}const owner=clone(body);owner.items[3].assigneeSid='S-1-5-1-';assert.throws(()=>parseAlertLifecyclePage(owner,binding));
 for(const patch of [{reason:'é'.repeat(4097)},{reason:'line\ncontrol'},{reason:'\uD800'}]){const bad=clone(body);Object.assign(bad.items[0],patch);assert.throws(()=>parseAlertLifecyclePage(bad,binding));}const missing=clone(body);delete missing.items[0].sourceTargetRevision;assert.throws(()=>parseAlertLifecyclePage(missing,binding));
});
test('native headers bind target/current revision/window and closed state/reason/coverage formulas',()=>{
 const body=alertLifecycleHistoryBackend().read(binding);for(const patch of [{targetId:lifecycleBeta},{targetRevision:2},{fromUtc:'2026-10-02T09:00:00Z'},{readEpoch:'g'.repeat(64)},{state:'healthy'},{reason:'legacy_provenance'},{coverage:{...body.coverage,eventCount:10001}},{coverage:{...body.coverage,cycleCount:0}},{extra:1}])assert.throws(()=>parseAlertLifecyclePage({...body,...patch},binding));
 const empty=alertLifecycleHistoryBackend({count:0}).read(binding);assert.equal(parseAlertLifecyclePage(empty,binding).state,'unavailable');assert.throws(()=>parseAlertLifecyclePage({...empty,state:'available'},binding));
});
test('protected cursor accepts canonical4096 characters and refuses noncanonical or oversized forms',()=>{
 assert.equal(lifecycleCursor(Buffer.alloc(3072).toString('base64')),true);assert.equal(lifecycleCursor(Buffer.alloc(3073).toString('base64')),false);for(const value of ['','YR==','YQ=','abc\n',4])assert.equal(lifecycleCursor(value),false);
});
test('ownership identity compares exact microseconds rather than native fractional UTC spelling',()=>{
 const original={...binding,fromUtc:'2026-10-02T10:00:00.100Z',toUtc:'2026-10-02T11:00:00.100000Z'},equivalent={...original,targetId:original.targetId.toUpperCase(),fromUtc:'2026-10-02T10:00:00.1+00:00',toUtc:'2026-10-02T11:00:00.1000000+00:00'};assert.equal(lifecycleBindingIdentity(original),lifecycleBindingIdentity(equivalent));const selected=selection(lifecycleFixtureEvent(7,binding));assert.equal(lifecycleSelectionIdentity(selected),lifecycleSelectionIdentity({...selected,eventAtUtc:lifecycleUtc(selected.eventAtUtc)}));assert.notEqual(lifecycleBindingIdentity(original),lifecycleBindingIdentity({...original,targetRevision:2}));
});
test('native100 paging validates cutoff/epoch/full membership and never silently drops or repeats events',()=>{
 const backend=alertLifecycleHistoryBackend({count:201}),first=parseAlertLifecyclePage(backend.read(binding),binding),second=parseAlertLifecyclePage(backend.read(binding,first.nextCursor),binding,[first],first.nextCursor),last=backend.read(binding,second.nextCursor);assert.equal(parseAlertLifecyclePage(last,binding,[first,second],second.nextCursor).items.length,1);
 for(const patch of [{readEpoch:'f'.repeat(64)},{cutoffUtc:'2026-10-03T11:00:00Z'},{items:[]},{items:[first.items[0]]},{nextCursor:first.nextCursor}])assert.throws(()=>parseAlertLifecyclePage({...last,...patch},binding,[first,second],second.nextCursor));backend.settings.epoch++;assert.throws(()=>backend.read(binding,second.nextCursor),e=>e.status===409);
});
test('terminal exact counts and cycle counts match all received events and same-time UUID ordering is preserved',()=>{
 const body=alertLifecycleHistoryBackend().read(binding);const missing={...body,items:body.items.slice(1)};assert.throws(()=>parseAlertLifecyclePage(missing,binding));const wrong=clone(body);wrong.coverage.cycleCount=3;assert.throws(()=>parseAlertLifecyclePage(wrong,binding));
 const tied=clone(body);tied.items[1].eventAtUtc=tied.items[0].eventAtUtc;tied.items.sort((a,b)=>a.eventAtUtc.localeCompare(b.eventAtUtc)||a.eventId.localeCompare(b.eventId));assert.equal(parseAlertLifecyclePage(tied,binding).items.length,16);tied.items.reverse();assert.throws(()=>parseAlertLifecyclePage(tied,binding));
});

test('unread coverage cannot conceal contradictory received provenance or extra cycles',()=>{
 const body=alertLifecycleHistoryBackend({count:201}).read(binding),legacy=clone(body);Object.assign(legacy.items[0],{episodeId:null,ruleId:null,ruleName:null,recordedAtUtc:null,stateRevision:null,provenance:'legacy_partial'});assert.throws(()=>parseAlertLifecyclePage(legacy,binding));
 const noComplete=clone(body);Object.assign(noComplete.coverage,{completeEventCount:0,legacyPartialEventCount:201});Object.assign(noComplete,{state:'partial',reason:'legacy_provenance'});assert.throws(()=>parseAlertLifecyclePage(noComplete,binding));
 const extraCycle=clone(body);extraCycle.items[99].episodeId=lifecycleUuid('extra cycle');assert.throws(()=>parseAlertLifecyclePage(extraCycle,binding));
});
test('exact lookup is independent of list caps and unrelated manifest drift, with no whole-window coverage claim',()=>{
 const backend=alertLifecycleHistoryBackend({count:10001}),row=lifecycleFixtureEvent(0,binding,backend.settings),selected=selection(row);assert.throws(()=>backend.read(binding),e=>e.status===422);const initial=backend.detail(binding,selected);assert.equal(parseAlertLifecycleDetail(initial,binding,selected).event.eventId,row.eventId);assert.equal(Object.hasOwn(initial,'coverage'),false);backend.settings.count=200;backend.settings.epoch++;assert.equal(backend.detail(binding,selected).readEpoch,initial.readEpoch);
});
test('exact event/cycle/time mismatch never substitutes a newer event or refire; missing receipt stays unavailable',()=>{
 const backend=alertLifecycleHistoryBackend(),row=lifecycleFixtureEvent(7,binding),selected=selection(row),body=backend.detail(binding,selected);assert.equal(parseAlertLifecycleDetail(body,binding,selected).event.kind,'resolved');
 for(const patch of [{eventId:lifecycleUuid('foreign')},{alertId:lifecycleUuid('foreign')},{episodeId:null},{eventAtUtc:'2026-10-02T10:59:00.123456Z'}])assert.throws(()=>parseAlertLifecycleDetail(body,binding,{...selected,...patch}));
 const missing={...selected,eventId:lifecycleUuid('missing')};assert.equal(parseAlertLifecycleDetail(backend.detail(binding,missing),binding,missing).event,null);
});
test('exact selected event older than120 marker summary is pinned alongside119 latest without substituting a same-time event',()=>{
 const rows=Array.from({length:201},(_,i)=>lifecycleFixtureEvent(i,binding,{count:201})),events=alertLifecycleTimelineEvents(rows,scope),selected=events[0],visible=timelineEventsInWindow(events,binding.fromUtc,binding.toUtc,selected);assert.equal(visible.length,120);assert.equal(visible[0].id,lifecycleEventIdentity(rows[0]));assert.equal(visible.at(-1).id,events.at(-1).id);
});
test('no-store transport binds exact event/cycle/native UTC without active-inbox requests or writes',async()=>{
 const old=globalThis.fetch,backend=alertLifecycleHistoryBackend(),calls=[],row=lifecycleFixtureEvent(7,binding),selected=selection(row);globalThis.fetch=async(url,init)=>{calls.push({url,init});return new Response(JSON.stringify(String(url).includes('/history/')?backend.detail(binding,selected):backend.read(binding)),{headers:{'content-type':'application/json'}});};
 try{await getAlertLifecycleHistory(binding,new AbortController().signal);await getAlertLifecycleEvent(binding,selected,new AbortController().signal);assert.equal(calls.length,2);assert.ok(calls.every(c=>c.init.cache==='no-store'&&!c.init.body));const url=new URL(calls[1].url,'http://fixture');assert.equal(url.searchParams.get('episodeId'),selected.episodeId);assert.equal(url.searchParams.get('eventAtUtc'),lifecycleUtc(selected.eventAtUtc));assert.equal(url.searchParams.has('cursor'),false);assert.equal(url.searchParams.has('limit'),false);}finally{globalThis.fetch=old;}
});
for(const code of [401,403,404,409,422,503])test('HTTP'+code+' preserves typed read status and hides provider details',async()=>{const old=globalThis.fetch;globalThis.fetch=async()=>new Response('{"detail":"PRIVATE_PROVIDER_ERROR"}',{status:code});try{await assert.rejects(getAlertLifecycleHistory(binding,new AbortController().signal),e=>e.status===code&&!e.message.includes('PRIVATE'));}finally{globalThis.fetch=old;}});
