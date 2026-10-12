import test from 'node:test';
import assert from 'node:assert/strict';
import { readResourceSelection, resourceSelectionHref, retainResourceSelectionHref, preserveInvalidCpuObservationHref } from '../src/features/resources/resourceSelectionModel.ts';
import { readOverviewScope } from '../src/features/overview/overviewModel.ts';

const target='11111111-1111-4111-8111-000000000001', other='11111111-1111-4111-8111-000000000002';
const run='22222222-2222-4222-8222-000000000001';
const base=`#/resources?target=${target}&range=custom&from=2026-10-02T10:00:00.000001Z&to=2026-10-02T11:00:00.000001Z`;
const pin=`${base}&cpuObservationRun=${run.toUpperCase()}&cpuObservationRevision=1`;

test('CPU exact run and sample revision round trip independently of loaded receipts',()=>{
  const selected=readResourceSelection(pin,target);
  assert.deepEqual(selected.cpuObservation,{runId:run,sampleTargetRevision:1});
  assert.deepEqual(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),selected),target),selected);
  assert.equal(readResourceSelection(pin,other).cpuObservation,undefined);
  assert.equal(readResourceSelection(pin.replace('#/resources','#/queries'),target).cpuObservation,undefined);
});

test('malformed and duplicate CPU pins remain explicit and never choose a substitute',()=>{
  for(const fields of [`cpuObservationRun=${run}`,`cpuObservationRevision=1`,`cpuObservationRun=${run}&cpuObservationRevision=01`,`cpuObservationRun=${run}&cpuObservationRevision=9007199254740992`,`cpuObservationRun=00000000-0000-0000-0000-000000000000&cpuObservationRevision=1`,`cpuObservationRun=${run}&cpuObservationRun=${run}&cpuObservationRevision=1`,`cpuObservationRun=${run}&cpuObservationRevision=1&cpuObservationRevision=1`]){
    const hash=`${base}&${fields}`, selection=readResourceSelection(hash,target);
    assert.equal(selection.invalidCpuObservation,true);assert.equal(selection.cpuObservation,undefined);
    const retained=preserveInvalidCpuObservationHref(resourceSelectionHref({...readOverviewScope(hash),compare:true},selection),hash);
    assert.equal(readResourceSelection(retained,target).invalidCpuObservation,true);
    const p=new URLSearchParams(retained.split('?')[1]);const source=new URLSearchParams(fields);
    for(const key of ['cpuObservationRun','cpuObservationRevision']) assert.deepEqual(p.getAll(key),source.getAll(key));
  }
});

test('CPU pins survive same-target window, compare and database changes, then clear on target/route changes',()=>{
  const next=resourceSelectionHref({...readOverviewScope(base),databaseId:5,compare:true,from:'2026-10-02T09:00:00.000001Z'},{});
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(next,pin),target).cpuObservation,{runId:run,sampleTargetRevision:1});
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace(target,other),pin),other).cpuObservation,undefined);
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace('#/resources','#/queries'),pin),target).cpuObservation,undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),{}),target).cpuObservation,undefined);
});

test('ambiguous shared context fences CPU selection and prevents a fallback request',()=>{
  const selection=readResourceSelection(`${pin}&range=custom`,target);
  assert.equal(selection.invalidContext,true);assert.equal(selection.invalidCpuObservation,true);assert.equal(selection.cpuObservation,undefined);
});
