import test from 'node:test';
import assert from 'node:assert/strict';
import {parseVolumeHistoryPage,volumeHistoryWindow,previousVolumeHistoryWindow,isSqlVolumeHistoryCursor,comparisonEligibility,volumeHistorySegments,volumeHistoryCompletionFraction,approximateVolumeGib,approximateVolumeAxisLabel,invalidVolumeHistory} from '../src/features/resources/sqlVolumeHistoryModel.ts';
import {getSqlVolumeHistory} from '../src/features/resources/sqlVolumeHistoryApi.ts';
import {volumeHistoryBackend,historyAlpha,historyBeta,historyKeyA,historyFromUtc,historyToUtc,historyIdentity} from '../tools/sql-volume-history-fixture.mjs';
const binding={instanceId:historyAlpha,targetRevision:1,fromUtc:historyFromUtc,toUtc:historyToUtc},selected={...binding,selected:historyIdentity()},clone=value=>structuredClone(value);
const exact=(backend=volumeHistoryBackend())=>backend.read(selected),catalog=(backend=volumeHistoryBackend())=>backend.read(binding);
const parse=(page,context=selected,prior=[],cursor)=>parseVolumeHistoryPage(page,context,prior,cursor);
test('completion windows preserve microseconds and previous equal-duration arithmetic before and after the Unix epoch',()=>{
 assert.deepEqual(volumeHistoryWindow(historyFromUtc,historyToUtc),{fromUtc:historyFromUtc,toUtc:historyToUtc});assert.equal(previousVolumeHistoryWindow(historyFromUtc,historyToUtc).fromUtc,'2026-10-02T09:00:00.123456Z');
 assert.equal(previousVolumeHistoryWindow('1969-12-31T23:59:58.123456Z','1969-12-31T23:59:59.123456Z').fromUtc,'1969-12-31T23:59:57.123456Z');
 for(const pair of [[historyToUtc,historyFromUtc],['2026-02-30T00:00:00Z',historyToUtc],['2026-08-01T00:00:00Z',historyToUtc]])assert.throws(()=>volumeHistoryWindow(...pair));
});
test('native current fence and older exact sample revision/key never substitute another target, revision or window',()=>{
 const backend=volumeHistoryBackend({sampleTargetRevision:4});const request={...selected,selected:historyIdentity(0,4)},page=backend.read(request);assert.equal(parse(page,request).sampleTargetRevision,4);
 for(const patch of [{instanceId:historyBeta},{targetRevision:2},{sampleTargetRevision:1},{volumeKey:'b'.repeat(64)},{fromUtc:'2026-10-02T09:00:00Z'},{toUtc:'2026-10-02T12:00:00Z'}])assert.throws(()=>parse({...page,...patch},request));
});
test('strict native headers reject unknown producer states/reasons/fields and inconsistent coverage',()=>{
 const page=exact();for(const patch of [{state:'healthy'},{reason:'invented'},{readEpoch:'g'.repeat(64)},{cutoffUtc:'2026-10-04T00:00:00Z'},{secret:'path'},
  {coverage:{...page.coverage,completeRunCount:0}},{coverage:{...page.coverage,retainedObservationCount:250001}},{state:'partial'}])assert.throws(()=>parse({...page,...patch}),{message:invalidVolumeHistory});
});
test('native header state/reason and terminal complete counts cannot manufacture healthy empty or lossy evidence',()=>{
 const empty=exact(volumeHistoryBackend({mode:'unavailable'}));assert.equal(parse(empty).state,'unavailable');assert.throws(()=>parse({...empty,state:'available',reason:'retained_window'}));
 const absent=exact(volumeHistoryBackend({mode:'absent'}));assert.equal(parse(absent).state,'unavailable');assert.equal(parse(absent).reason,'retained_evidence_unavailable');assert.throws(()=>parse({...absent,state:'partial'}));
 const healthy=exact();for(const reason of ['no_completed_runs','retained_evidence_unavailable','visibility_gaps','revision_boundary'])assert.throws(()=>parse({...healthy,reason}));
 const lossy=clone(healthy);lossy.items[0].minimumLostItems=1;assert.throws(()=>parse(lossy));const missing=clone(healthy);missing.items[0].state='partial';missing.items[0].reason='collection_gap';assert.throws(()=>parse(missing));
 const wrongCount={...healthy,state:'partial',reason:'visibility_gaps',coverage:{...healthy.coverage,hasVisibilityGap:true,completeRunCount:3,gapRunCount:1}};assert.throws(()=>parse(wrongCount));
 const boundary=exact(volumeHistoryBackend({mode:'revision_boundary'}));assert.equal(parse(boundary).coverage.gapRunCount,0);assert.equal(parse(boundary).state,'partial');
});
test('native Int64 capacities/deltas stay exact strings and approximate GiB is explicitly only plotting',()=>{
 const page=exact();for(const row of page.items){row.totalBytes='9223372036854775807';row.availableBytes='9007199254740993';}
 assert.equal(parse(page).items[0].availableBytes,'9007199254740993');assert.equal(approximateVolumeGib('9007199254740993'),Number(9007199254740993n)/1073741824);
 for(const value of [3,'-1','00','9223372036854775808']){const bad=clone(page);bad.items[0].totalBytes=value;assert.throws(()=>parse(bad));}
 const cat=catalog();cat.items[0].availableBytesDelta='-0';assert.throws(()=>parse(cat,binding));
});
test('approximate axis labels fit the plot gutter for large Int64 capacities without changing received byte strings',()=>{
 assert.equal(approximateVolumeAxisLabel(4294967296),'4.3e9');assert.equal(approximateVolumeAxisLabel(1000),'1.0e3');assert.equal(approximateVolumeAxisLabel(50.25),'50.25');assert.equal(approximateVolumeAxisLabel(0),'0');
 const received='9223372036854775807';assert.ok(approximateVolumeAxisLabel(approximateVolumeGib(received)).length<=6);assert.equal(parse(exact()).items[0].totalBytes,'107374182400');
});
test('native absent/unknown/loss/skew rows preserve nulls and actual timestamps without inventing complete evidence',()=>{
 for(const mode of ['absent','unknown','loss','gap','skew']){const page=exact(volumeHistoryBackend({mode}));assert.equal(parse(page).coverage.hasVisibilityGap,true);assert.ok(comparisonEligibility([parse(page)],[parse(page)]));}
 const absent=parse(exact(volumeHistoryBackend({mode:'absent'})));assert.equal(absent.identityKind,null);assert.equal(absent.items[0].observedAtUtc,null);assert.equal(absent.items[0].availableBytes,null);
 const unknown=parse(exact(volumeHistoryBackend({mode:'unknown'})));assert.notEqual(unknown.items[0].observedAtUtc,null);assert.equal(unknown.items[0].totalBytes,null);
});
test('sample cross-field validation rejects unknown enums, late ingestion, source skew called available and manufactured capacity',()=>{
 const page=exact();const patches=[{outcome:'ok'},{state:'complete'},{reason:'fiction'},{lossKind:'unknown'},{mappedFileCount:0},{minimumLostItems:2147483648},
  {observedAtUtc:null},{collectedAtUtc:'2026-10-04T00:00:00Z'},{observedAtUtc:'2026-10-04T00:00:00Z'},{availableBytes:null},{state:'available',reason:'collection_loss'}];
 for(const patch of patches){const bad=clone(page);Object.assign(bad.items[0],patch);assert.throws(()=>parse(bad));}
});
test('completion-axis order is exact at microsecond precision with UUID ties; SQL observation time does not reorder history',()=>{
 const page=exact();page.items[0].observedAtUtc='2026-10-02T10:59:00.123456+00:00';assert.ok(parse(page).items[0].completedAtUtc);
 const bad=clone(page);bad.items.reverse();assert.throws(()=>parse(bad));const duplicate=clone(page);duplicate.items[1].runId=duplicate.items[0].runId;assert.throws(()=>parse(duplicate));
 const edge=clone(page);edge.items[0].completedAtUtc=historyToUtc;assert.throws(()=>parse(edge));
});
test('history-specific protected cursor permits canonical4096 characters and rejects noncanonical/truncated/oversized forms',()=>{
 assert.equal(isSqlVolumeHistoryCursor(Buffer.alloc(3072,7).toString('base64')),true);assert.equal(isSqlVolumeHistoryCursor(Buffer.alloc(3073,7).toString('base64')),false);
 for(const cursor of ['', 'YQ=', 'YR==', 'a\nAAA', 4])assert.equal(isSqlVolumeHistoryCursor(cursor),false);
});
test('full native catalog/selected continuation binds source epoch, cutoff, coverage and exact ordered membership',()=>{
 const backend=volumeHistoryBackend({catalogCount:26,runCount:251}),firstCatalog=backend.read(binding),first=parse(firstCatalog,binding),second=backend.read(binding,first.nextCursor);
 assert.equal(parse(second,binding,[first],first.nextCursor).items.length,1);
 const firstSelected=parse(backend.read(selected)),last=backend.read(selected,firstSelected.nextCursor);assert.equal(parse(last,selected,[firstSelected],firstSelected.nextCursor).items.length,1);
 for(const patch of [{readEpoch:'f'.repeat(64)},{cutoffUtc:'2026-10-03T11:00:00Z'},{coverage:{...last.coverage,retentionDays:29}},{items:[firstSelected.items[0]]},{nextCursor:firstSelected.nextCursor}])assert.throws(()=>parse({...last,...patch},selected,[firstSelected],firstSelected.nextCursor));
 backend.settings.epoch++;assert.throws(()=>backend.read(selected,firstSelected.nextCursor),error=>error.status===409);
});
test('terminal pages cannot silently omit a retained run or identity observation and unread pages require full limits',()=>{
 const page=exact();assert.throws(()=>parse({...page,items:page.items.slice(1)}));const cat=catalog();assert.throws(()=>parse({...cat,items:cat.items.slice(1)},binding));
 assert.throws(()=>parse({...page,nextCursor:btoa('protected-native')},selected));const overflow=volumeHistoryBackend({runCount:10001});assert.throws(()=>overflow.read(selected),error=>error.status===422);
});
test('catalog comparison deltas require known stable capacity and source identity with complete window evidence',()=>{
 const page=catalog();assert.equal(parse(page,binding).items[0].totalBytesDelta,'0');for(const patch of [{identityKind:'mount_point'},{observationCount:1},{availableBytesDelta:'77'},{totalBytesDelta:'1'},{comparisonState:'invented'}]){const bad=clone(page);Object.assign(bad.items[0],patch);assert.throws(()=>parse(bad,binding));}
});
test('eligible independent windows require exact identity, complete received pages, stable total and two observations each',()=>{
 const backend=volumeHistoryBackend(),window=previousVolumeHistoryWindow(historyFromUtc,historyToUtc),previousBinding={...selected,...window},c=parse(backend.read(selected)),p=parse(backend.read(previousBinding),previousBinding);
 assert.equal(comparisonEligibility([c],[p]),undefined);
 for(const mode of ['unknown','gap','capacity_changed','revision_boundary']){const b=volumeHistoryBackend({mode}),page=parse(b.read(selected));assert.ok(comparisonEligibility([page],[p]));}
 assert.ok(comparisonEligibility([c],[{...p,sampleTargetRevision:2}]));assert.ok(comparisonEligibility([c],[{...p,identityKind:'mount_point'}]));assert.ok(comparisonEligibility([c],[{...p,nextCursor:btoa('native-more')}]));
});
test('mount point and run-scoped unknown identities show singleton gauges without cross-run line or inferred incarnation',()=>{
 const rows=parse(exact()).items;assert.equal(volumeHistorySegments(rows,'volume_id').length,1);
 for(const kind of ['mount_point','file_scoped_unknown'])assert.deepEqual(volumeHistorySegments(rows,kind).map(segment=>segment.length),[1,1,1,1]);
});
test('completion plot geometry remains finite for valid windows smaller than one millisecond',()=>{
 const from='2026-10-02T10:00:00.000001Z',to='2026-10-02T10:00:00.000003Z';assert.equal(Date.parse(from),Date.parse(to));
 assert.equal(volumeHistoryCompletionFraction('2026-10-02T10:00:00.000002Z',from,to),.5);
 assert.equal(volumeHistoryCompletionFraction('2026-10-02T09:59:59.999999Z',from,to,2n),0);
});
test('missing/loss/unknown/gap samples, resize and elapsed completion intervals break actual gauge segments',()=>{
 for(const mode of ['loss','gap','unknown','skew']){const rows=parse(exact(volumeHistoryBackend({mode}))).items;assert.ok(volumeHistorySegments(rows,'volume_id').every(segment=>segment.every(row=>row.state==='available')));}
 assert.equal(volumeHistorySegments(parse(exact(volumeHistoryBackend({mode:'capacity_changed'}))).items,'volume_id').length,2);
 const rows=clone(parse(exact()).items);rows[3].completedAtUtc='2026-10-02T12:00:00Z';assert.equal(volumeHistorySegments(rows,'volume_id').length,2);
});
test('typed no-store history transport sends only protected context and separate independent previous-window GETs',async()=>{
 const previous=globalThis.fetch,backend=volumeHistoryBackend(),calls=[];globalThis.fetch=async(url,init)=>{calls.push({url:String(url),init});return new Response(JSON.stringify(backend.read(selected)),{headers:{'content-type':'application/json'}});};
 try{await getSqlVolumeHistory(selected,new AbortController().signal);const url=new URL(calls[0].url,'http://fixture');assert.equal(url.pathname.endsWith('/'+historyKeyA+'/history'),true);assert.equal(url.searchParams.get('sampleTargetRevision'),'1');assert.equal(url.searchParams.get('limit'),'250');assert.equal(calls[0].init.cache,'no-store');assert.equal(url.searchParams.has('cutoffUtc'),false);assert.equal(url.searchParams.has('readEpoch'),false);}finally{globalThis.fetch=previous;}
});
for(const code of [401,403,404,409,422,503])test('native HTTP'+code+' keeps ownership status and hides provider detail',async()=>{
 const previous=globalThis.fetch;globalThis.fetch=async()=>new Response('{"detail":"PRIVATE_PROVIDER_ERROR"}',{status:code});try{await assert.rejects(()=>getSqlVolumeHistory(selected,new AbortController().signal),error=>error.status===code&&!error.message.includes('PRIVATE'));}finally{globalThis.fetch=previous;}
});
