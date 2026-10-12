import test from 'node:test';
import assert from 'node:assert/strict';
import { readResourceSelection, resourceSelectionHref, retainResourceSelectionHref, preserveInvalidCpuCaptureHref } from '../src/features/resources/resourceSelectionModel.ts';
import { readOverviewScope } from '../src/features/overview/overviewModel.ts';

const target='11111111-1111-4111-8111-000000000001', other='11111111-1111-4111-8111-000000000002';
const run='22222222-2222-4222-8222-000000000001';
const base=`#/resources?target=${target}&range=custom&from=2026-10-02T10:00:00.000001Z&to=2026-10-02T11:00:00.000001Z`;
const pin=`${base}&cpuCaptureRun=${run.toUpperCase()}&cpuCaptureRevision=1`;

test('CPU exact run and sample revision round trip independently of loaded receipts',()=>{
  const selected=readResourceSelection(pin,target);
  assert.deepEqual(selected.cpuCapture,{runId:run,sampleTargetRevision:1});
  assert.deepEqual(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),selected),target),selected);
  assert.equal(readResourceSelection(pin,other).cpuCapture,undefined);
  assert.equal(readResourceSelection(pin.replace('#/resources','#/queries'),target).cpuCapture,undefined);
});

test('malformed and duplicate CPU pins remain explicit and never choose a substitute',()=>{
  for(const fields of [`cpuCaptureRun=${run}`,`cpuCaptureRevision=1`,`cpuCaptureRun=${run}&cpuCaptureRevision=01`,`cpuCaptureRun=${run}&cpuCaptureRevision=9007199254740992`,`cpuCaptureRun=00000000-0000-0000-0000-000000000000&cpuCaptureRevision=1`,`cpuCaptureRun=${run}&cpuCaptureRun=${run}&cpuCaptureRevision=1`,`cpuCaptureRun=${run}&cpuCaptureRevision=1&cpuCaptureRevision=1`]){
    const hash=`${base}&${fields}`, selection=readResourceSelection(hash,target);
    assert.equal(selection.invalidCpuCapture,true);assert.equal(selection.cpuCapture,undefined);
    const retained=preserveInvalidCpuCaptureHref(resourceSelectionHref({...readOverviewScope(hash),compare:true},selection),hash);
    assert.equal(readResourceSelection(retained,target).invalidCpuCapture,true);
    const p=new URLSearchParams(retained.split('?')[1]);const source=new URLSearchParams(fields);
    for(const key of ['cpuCaptureRun','cpuCaptureRevision']) assert.deepEqual(p.getAll(key),source.getAll(key));
  }
});

test('CPU pins survive same-target window, compare and database changes, then clear on target/route changes',()=>{
  const next=resourceSelectionHref({...readOverviewScope(base),databaseId:5,compare:true,from:'2026-10-02T09:00:00.000001Z'},{});
  assert.deepEqual(readResourceSelection(retainResourceSelectionHref(next,pin),target).cpuCapture,{runId:run,sampleTargetRevision:1});
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace(target,other),pin),other).cpuCapture,undefined);
  assert.equal(readResourceSelection(retainResourceSelectionHref(next.replace('#/resources','#/queries'),pin),target).cpuCapture,undefined);
  assert.equal(readResourceSelection(resourceSelectionHref(readOverviewScope(pin),{}),target).cpuCapture,undefined);
});

test('ambiguous shared context fences CPU selection and prevents a fallback request',()=>{
  const selection=readResourceSelection(`${pin}&range=custom`,target);
  assert.equal(selection.invalidContext,true);assert.equal(selection.invalidCpuCapture,true);assert.equal(selection.cpuCapture,undefined);
});
