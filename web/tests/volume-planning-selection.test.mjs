import test from 'node:test';
import assert from 'node:assert/strict';
import { readResourceSelection, resourceSelectionHref, retainResourceSelectionHref, preserveInvalidVolumePlanningHref } from '../src/features/resources/resourceSelectionModel.ts';
import { readOverviewScope } from '../src/features/overview/overviewModel.ts';

const target='11111111-1111-4111-8111-000000000001', other='11111111-1111-4111-8111-000000000002';
const run='22222222-2222-4222-8222-000000000001';
const base=`#/resources?target=${target}&range=custom&from=2026-10-02T10:00:00.000001Z&to=2026-10-02T11:00:00.000001Z`;
const pin=`${base}&volumePlanningRun=${run.toUpperCase()}&volumePlanningRevision=1`;

test('Volume planning exact run and sample revision round trip independently of loaded receipts',()=>{
  const selected=readResourceSelection(pin,target);
  assert.deepEqual(selected.volumePlanning,{runId:run,sampleTargetRevision:1});
  assert.deepEqual(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),selected),target),selected);
  assert.equal(readResourceSelection(pin,other).volumePlanning,undefined);
  assert.equal(readResourceSelection(pin.replace('#/resources','#/queries'),target).volumePlanning,undefined);
});

test('malformed and duplicate Volume planning pins remain explicit and never choose a substitute',()=>{
  for(const fields of [`volumePlanningRun=${run}`,`volumePlanningRevision=1`,`volumePlanningRun=${run}&volumePlanningRevision=01`,`volumePlanningRun=${run}&volumePlanningRevision=9007199254740992`,`volumePlanningRun=00000000-0000-0000-0000-000000000000&volumePlanningRevision=1`,`volumePlanningRun=${run}&volumePlanningRun=${run}&volumePlanningRevision=1`,`volumePlanningRun=${run}&volumePlanningRevision=1&volumePlanningRevision=1`]){
    const hash=`${base}&${fields}`, selection=readResourceSelection(hash,target);
    assert.equal(selection.invalidVolumePlanning,true);assert.equal(selection.volumePlanning,undefined);
    const retained=preserveInvalidVolumePlanningHref(resourceSelectionHref({...readOverviewScope(hash),compare:true},selection),hash);
    assert.equal(readResourceSelection(retained,target).invalidVolumePlanning,true);
    const p=new URLSearchParams(retained.split('?')[1]);const source=new URLSearchParams(fields);
    for(const key of ['volumePlanningRun','volumePlanningRevision']) assert.deepEqual(p.getAll(key),source.getAll(key));
  }
});

test('Volume planning pins survive same-window compare and database changes, then clear on target/route changes',()=>{
  const next=resourceSelectionHref({...readOverviewScope(base),databaseId:5,compare:true},{});
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(next,pin),target).volumePlanning,{runId:run,sampleTargetRevision:1});
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace(target,other),pin),other).volumePlanning,undefined);
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace('#/resources','#/queries'),pin),target).volumePlanning,undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),{}),target).volumePlanning,undefined);
});

test('true planning time changes retire only its valid exact pin while equivalent microseconds retain it',()=>{
  const scope=readOverviewScope(base);
  for(const change of [{from:'2026-10-02T10:00:00.000002Z'},{to:'2026-10-02T11:00:00.000002Z'},{range:'1h',from:undefined,to:undefined}]) {
    const next=resourceSelectionHref({...scope,...change},{});
    const current=pin+`&cpuCaptureRun=${run}&cpuCaptureRevision=2&volumeRevision=3&volumeKey=${'a'.repeat(64)}`;
    const selection=readResourceSelection(retainResourceSelectionHref(next,current),target);
    assert.equal(selection.volumePlanning,undefined);
    assert.deepEqual(selection.cpuCapture,{runId:run,sampleTargetRevision:2});
    assert.deepEqual(selection.volume,{sampleTargetRevision:3,volumeKey:'a'.repeat(64)});
  }
  const equivalent=base.replace(/\.000001Z/gu,'.0000010Z');
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(equivalent,pin),target).volumePlanning,{runId:run,sampleTargetRevision:1});
  const live=`#/resources?target=${target}&range=1h&volumePlanningRun=${run}&volumePlanningRevision=1`;
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(`#/resources?target=${target}&range=1h&databaseId=5`,live),target).volumePlanning,{runId:run,sampleTargetRevision:1});
  assert.equal(readResourceSelection(retainResourceSelectionHref(`#/resources?target=${target}&range=6h`,live),target).volumePlanning,undefined);
});

test('ambiguous shared context fences Volume planning selection and prevents a fallback request',()=>{
  const selection=readResourceSelection(`${pin}&range=custom`,target);
  assert.equal(selection.invalidContext,true);assert.equal(selection.invalidVolumePlanning,true);assert.equal(selection.volumePlanning,undefined);
});

test('planning receipt and volume trend identity coexist with file and CPU pins',()=>{
  const fields=`&volumeRevision=2&volumeKey=${'a'.repeat(64)}&fileSnapshotRun=${run}&fileSnapshotRevision=3&cpuCaptureRun=${run}&cpuCaptureRevision=4`;
  const hash=pin+fields, selected=readResourceSelection(hash,target);
  assert.deepEqual(selected.volumePlanning,{runId:run,sampleTargetRevision:1});
  assert.deepEqual(selected.volume,{sampleTargetRevision:2,volumeKey:'a'.repeat(64)});
  const next=resourceSelectionHref(readOverviewScope(hash),{...selected,volumePlanning:undefined});
  const cleared=readResourceSelection(next,target);
  assert.equal(cleared.volumePlanning,undefined);
  assert.deepEqual(cleared.volume,selected.volume);
  assert.deepEqual(cleared.fileSnapshot,selected.fileSnapshot);
  assert.deepEqual(cleared.cpuCapture,selected.cpuCapture);
});

test('planning pins support the 31-day window boundary and microseconds without widening',()=>{
  const hash=pin.replace('2026-10-02T10:00:00.000001Z','2026-09-01T11:00:00.000001Z');
  assert.deepEqual(readResourceSelection(hash,target).volumePlanning,{runId:run,sampleTargetRevision:1});
  assert.equal(readResourceSelection(hash.replace('2026-09-01T11:00:00.000001Z','2026-09-01T11:00:00.000000Z'),target).invalidContext,true);
});
