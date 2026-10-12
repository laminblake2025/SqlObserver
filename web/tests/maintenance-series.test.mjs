import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSeriesDefinition, seriesDefinitionDigest, validSeriesZone, parseSeriesStatus, parseSeriesPreview,
  parseSeriesPage, parseSeriesOccurrences, parseSeriesReceipt, MaintenanceMutationLane } from '../src/features/alerts/maintenanceSeriesModel.ts';
import { previewMaintenanceSeries, getMaintenanceSeries, getMaintenanceSeriesStatus, getMaintenanceSeriesOccurrences, changeMaintenanceSeries, getOneOffMaintenanceForSeries } from '../src/features/alerts/maintenanceSeriesApi.ts';
import { alpha,beta,seriesId,auditId,now,definition,digest,status,preview,receipt,expansion,seriesBackend,windowId } from '../component-tests/maintenanceSeriesFixture.mjs';
const operationId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const create=()=>({kind:'create',body:{operationId,seriesId,definition:definition()}});
test('daily weekly monthly nullable fields and sorted Sunday-zero weekdays match native definitions',()=>{
 assert.equal(parseSeriesDefinition(definition()).dayOfMonth,null);
 assert.deepEqual(parseSeriesDefinition(definition({frequency:'weekly',weekdays:[6,0,2]})).weekdays,[0,2,6]);
 assert.equal(parseSeriesDefinition(definition({frequency:'monthly',weekdays:[],dayOfMonth:31})).dayOfMonth,31);
 for(const bad of [{frequency:'weekly',weekdays:[]},{frequency:'weekly',weekdays:[0,0]},{frequency:'weekly',weekdays:[-1]},
  {frequency:'weekly',weekdays:[7]},{frequency:'weekly',weekdays:[0],dayOfMonth:1},{frequency:'daily',dayOfMonth:1},
  {frequency:'daily',weekdays:[0]},{frequency:'monthly',dayOfMonth:null},{frequency:'monthly',dayOfMonth:32},{frequency:'monthly',dayOfMonth:0}])
  assert.throws(()=>parseSeriesDefinition(definition(bad)));
});
test('finite civil dates, cadence bounds, elapsed duration and safe reason reject invalid producer inputs',()=>{
 for(const bad of [{startDate:'2026-02-30'},{endDate:'2031-11-02'},{endDate:'2026-10-30'},{localStartTime:'24:00'},
  {durationMinutes:0},{durationMinutes:10081},{interval:0},{interval:366},{frequency:'weekly',weekdays:[0],interval:53},
  {frequency:'monthly',dayOfMonth:1,interval:13},{gapPolicy:'shift'},{foldPolicy:'guess'},{reason:'a\nb'},{reason:'é'.repeat(257)}])
  assert.throws(()=>parseSeriesDefinition(definition(bad)));
 assert.equal(parseSeriesDefinition(definition({startDate:'2024-02-29',endDate:'2029-02-28'})).endDate,'2029-02-28');
 assert.throws(()=>parseSeriesDefinition(definition({startDate:'2024-02-29',endDate:'2029-03-01'})));
});
test('zone validation follows native lexical aliases while the server alone resolves membership and DST',()=>{
 for(const zone of ['UTC','America/New_York','CET','GMT','Europe/Paris'])assert.equal(validSeriesZone(zone),true);
 for(const zone of ['Eastern Standard Time','+03:00','Etc/GMT+5','America/New_York\n','x'.repeat(129),''])assert.equal(validSeriesZone(zone),false);
});
test('canonical digest preserves exact reason, monthly null and sorted weekday bytes',async()=>{
 for(const d of [definition(),definition({frequency:'weekly',weekdays:[6,0],reason:' Exact | reason '}),
  definition({frequency:'monthly',dayOfMonth:31})])assert.equal(await seriesDefinitionDigest(d),digest(d));
});
test('native preview validates source digest, exact UTC elapsed duration, selected civil offset and repeated starts',async()=>{
 const d=definition(),p=preview(),hash=await seriesDefinitionDigest(d),result=parseSeriesPreview(p,alpha,seriesId,d,hash);
 assert.deepEqual(result.occurrences.map(item=>[item.startsAtUtc,item.utcOffsetMinutes,item.ambiguous]),
  [['2026-11-01T05:30:00.000000Z',-240,true],['2026-11-02T06:30:00.000000Z',-300,false]]);
 for(const bad of [{targetId:beta},{seriesId:alpha},{definitionDigest:'0'.repeat(64)},{expansionVersion:'unknown'},
  {occurrences:[{...p.occurrences[0],endsAtUtc:'2026-11-01T05:31:00Z'}]},
  {occurrences:[{...p.occurrences[0],utcOffsetMinutes:-300}]},{occurrences:[p.occurrences[0],p.occurrences[0]]}])
  assert.throws(()=>parseSeriesPreview({...p,...bad},alpha,seriesId,d,hash));
 const later=definition({foldPolicy:'later'});assert.equal(parseSeriesPreview(preview(seriesId,later),alpha,seriesId,later,digest(later)).occurrences[0].startsAtUtc,'2026-11-01T06:30:00.000000Z');
});
test('missing month day is a display-only skipped requested date; actual occurrences and commands require valid dates',()=>{
 const d=definition({frequency:'monthly',startDate:'2027-02-01',endDate:'2027-03-31',dayOfMonth:31}),p=preview(seriesId,d);
 assert.deepEqual(parseSeriesPreview(p,alpha,seriesId,d,digest(d)).skippedDates,[{localDate:'2027-02-31',reason:'missing_month_day'}]);
 assert.throws(()=>parseSeriesPreview({...p,skippedDates:[{localDate:'2027-02-31',reason:'nonexistent_local_time'}]},alpha,seriesId,d,digest(d)));
 assert.throws(()=>parseSeriesDefinition({...d,startDate:'2027-02-31'}));
});
test('DST gap skipped date validates as a real civil date and does not create a synthetic window',()=>{
 const d=definition({startDate:'2027-03-14',endDate:'2027-03-15',localStartTime:'02:30'}),p=preview(seriesId,d);
 const parsed=parseSeriesPreview(p,alpha,seriesId,d,digest(d));
 assert.deepEqual(parsed.skippedDates,[{localDate:'2027-03-14',reason:'nonexistent_local_time'}]);assert.equal(parsed.occurrences.length,1);
 assert.equal(parsed.occurrences[0].localDate,'2027-03-15');
});
test('current series validates native revision, null cancellation fence, bounded counts and microsecond UTC',()=>{
 assert.equal(parseSeriesStatus(status(),alpha,seriesId).serverNowUtc,'2026-10-03T15:00:00.123456Z');
 for(const bad of [{targetId:beta},{seriesId:alpha},{revision:0},{occurrenceCount:2049},{cancelledFromDate:'2027-02-31'},
  {updatedAtUtc:'2026-10-03T15:00:00.1234561+00:00'}])assert.throws(()=>parseSeriesStatus(status(seriesId,definition(),bad),alpha,seriesId));
});
test('immutable command receipts require exact revision and all real applied counts, including updated matches',()=>{
 for(const command of [create(),{kind:'edit',seriesId,body:{operationId,expectedRevision:7,effectiveFromDate:'2026-11-02',definition:definition()}},
  {kind:'cancel',seriesId,body:{operationId,expectedRevision:7,effectiveFromDate:'2026-11-02',reason:'Cancel future'}}]){
  const source=receipt(command,{updatedOccurrences:1,preservedExceptions:2});assert.equal(parseSeriesReceipt(source,command).updatedOccurrences,1);
  for(const bad of [{operationId:alpha},{seriesId:alpha},{auditId:'invalid'},{revision:source.revision+1},{updatedOccurrences:undefined},
   {revision:Number.MAX_SAFE_INTEGER+1},{createdOccurrences:-1},{preservedExceptions:2049}])assert.throws(()=>parseSeriesReceipt({...source,...bad},command));
 }
});
test('validated empty future edit preview removes obsolete dates while fresh creation still needs future occurrences',()=>{
 const backend=seriesBackend();backend.change(alpha,create());
 const d=definition({endDate:'2026-11-01'}),p=preview(seriesId,d,'2026-11-02');
 assert.deepEqual(parseSeriesPreview(p,alpha,seriesId,d,digest(d)).occurrences,[]);
 const command={kind:'edit',seriesId,body:{operationId:beta,expectedRevision:1,effectiveFromDate:'2026-11-02',definition:d}};
 const result=parseSeriesReceipt(backend.change(alpha,command),command);assert.equal(result.cancelledOccurrences,1);assert.equal(result.createdOccurrences,0);
 assert.equal(backend.windows.get(windowId(seriesId,'2026-11-01')).cancelledAtUtc,null);
});
test('occurrence exceptions retain original offset metadata while their actual UTC bounds may be edited',()=>{
 const row={...expansion().occurrences[0],startsAtUtc:'2026-11-01T13:00:00Z',endsAtUtc:'2026-11-01T14:00:00Z',
  reason:'Individual edit',revision:3,seriesRevision:1,exception:true,cancelledAtUtc:null};
 const p=parseSeriesOccurrences({targetId:alpha,seriesId,serverNowUtc:now,items:[row],nextBeforeWindowId:null},alpha,seriesId);
 assert.equal(p.items[0].utcOffsetMinutes,-240);assert.equal(p.items[0].startsAtUtc,'2026-11-01T13:00:00.000Z');
});
test('native series and occurrence pages bind exact tail anchors, reject duplicate retained identities and preserve opaque IDs',()=>{
 const statuses=Array.from({length:50},(_,i)=>status('77777777-7777-4777-8777-'+String(i+1).padStart(12,'0')));
 const first=parseSeriesPage({targetId:alpha,serverNowUtc:now,items:statuses,nextBeforeSeriesId:statuses.at(-1).seriesId},alpha);
 assert.throws(()=>parseSeriesPage({targetId:alpha,serverNowUtc:now,items:[statuses[0]],nextBeforeSeriesId:null},alpha,[first],first.nextBeforeSeriesId));
 assert.throws(()=>parseSeriesPage({targetId:alpha,serverNowUtc:now,items:statuses,nextBeforeSeriesId:statuses[1].seriesId},alpha));
 const backend=seriesBackend();backend.change(alpha,create());const page=backend.occurrences(alpha,seriesId);
 assert.equal(parseSeriesOccurrences(page,alpha,seriesId).items.length,2);
 assert.throws(()=>parseSeriesOccurrences({...page,targetId:beta},alpha,seriesId));
 assert.throws(()=>parseSeriesOccurrences({...page,nextBeforeWindowId:page.items[0].windowId},alpha,seriesId));
});
test('stable series/civil window IDs survive future edits; past/cancelled exceptions and irreversible fence survive later changes',()=>{
 const backend=seriesBackend();backend.change(alpha,create());const first=windowId(seriesId,'2026-11-01'),last=windowId(seriesId,'2026-11-02');
 backend.windows.get(first).exception=true;backend.windows.get(first).reason='Individual exception';
 const cancel={kind:'cancel',seriesId,body:{operationId:alpha,expectedRevision:1,effectiveFromDate:'2026-11-02',reason:'Future cutoff'}};
 const immutable=backend.change(alpha,cancel);assert.equal(immutable.cancelledOccurrences,1);
 const edit={kind:'edit',seriesId,body:{operationId:beta,expectedRevision:2,effectiveFromDate:'2026-11-01',definition:definition({localStartTime:'02:30',endDate:'2026-11-03'})}};
 backend.change(alpha,edit);assert.deepEqual(backend.change(alpha,cancel),immutable);
 assert.equal(backend.windows.get(first).reason,'Individual exception');assert.ok(backend.windows.get(last).cancelledAtUtc);
 assert.equal(backend.windows.has(windowId(seriesId,'2026-11-03')),false);assert.equal(backend.read(alpha,seriesId).cancelledFromDate,'2026-11-02');
});
test('synchronous maintenance lane excludes both writer families and old ABA release cannot clear a new owner',()=>{
 const lane=new MaintenanceMutationLane(),a={},b={},c={};lane.setContext('A');assert.equal(lane.claim('A',a),true);assert.equal(lane.claim('A',b),false);
 lane.setContext('B');lane.setContext('A');assert.equal(lane.claim('A',c),true);lane.release(a);assert.equal(lane.busy('A'),true);lane.release(c);assert.equal(lane.busy('A'),false);
});
test('native HTTP preview/create/current/occurrence routes preserve explicit nulls and distinct readback',async()=>{
 const previous=globalThis.fetch,calls=[],command=create();
 globalThis.fetch=async(url,options)=>{calls.push({url:String(url),options});const path=String(url);
  const body=path.endsWith('/preview')?preview():options.method==='POST'?receipt(command):path.includes('/occurrences?')?{targetId:alpha,seriesId,serverNowUtc:now,items:[],nextBeforeWindowId:null}:status();
  return new Response(JSON.stringify(body),{headers:{'content-type':'application/json'}});};
 try{const signal=new AbortController().signal;await previewMaintenanceSeries(alpha,seriesId,definition(),null,signal);await changeMaintenanceSeries(alpha,command,signal);
  await getMaintenanceSeriesStatus(alpha,seriesId,signal);await getMaintenanceSeriesOccurrences(alpha,seriesId,signal);
  assert.equal(JSON.parse(calls[0].options.body).effectiveFromDate,null);assert.deepEqual(JSON.parse(calls[1].options.body),command.body);
  assert.equal(calls[2].options.method,'GET');assert.equal(calls[3].url.endsWith('/occurrences?limit=50'),true);
 }finally{globalThis.fetch=previous;}
});
for(const code of [401,403,409,422,503])test('series HTTP '+code+' stays typed for ownership decisions',async()=>{
 const previous=globalThis.fetch;globalThis.fetch=async()=>new Response('{}',{status:code});
 try{await assert.rejects(()=>getMaintenanceSeries(alpha,new AbortController().signal),error=>error.status===code);}
 finally{globalThis.fetch=previous;}
});
test('one-off unavailable disclosure stays specific while native authorization failures remain typed',async()=>{
 const previous=globalThis.fetch;
 try{for(const code of [401,403,503]){globalThis.fetch=async()=>new Response('{}',{status:code});
  await assert.rejects(()=>getOneOffMaintenanceForSeries(alpha,new AbortController().signal),error=>error.status===code
   && (code!==503||error.message==='Maintenance windows are temporarily unavailable.'));}}
 finally{globalThis.fetch=previous;}
});
