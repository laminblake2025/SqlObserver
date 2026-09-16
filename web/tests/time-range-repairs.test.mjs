import test from 'node:test';
import assert from 'node:assert/strict';
import {customUtcWindow,overviewHref,readOverviewScope} from '../src/features/overview/overviewModel.ts';
import {analyticsWindow} from '../src/features/analytics/analyticsWindow.ts';
import {parseAnalyticsSurfacePage} from '../src/features/analytics/analyticsParser.ts';

const now=Date.parse('2026-09-15T18:00:00Z');
test('custom UTC inputs accept minutes and seconds without adding duplicate seconds',()=>{
  for(const suffix of ['',':00',':00.000']) {
    const window=customUtcWindow(`2026-08-15T00:00${suffix}`,`2026-09-15T00:00${suffix}`,now);
    const scope={target:'test',range:'custom',from:window.fromUtc,to:window.toUtc,compare:false};
    assert.deepEqual(readOverviewScope(overviewHref(scope)),scope);
    assert.equal(window.fromUtc,'2026-08-15T00:00:00.000Z');
  }
  assert.throws(()=>customUtcWindow('2026-09-15T00:00','2026-09-14T00:00',now),/after the start/);
  assert.throws(()=>customUtcWindow('2026-09-15T00:00','2026-09-16T00:00',now),/future/);
});
test('analytics inherits seven days and rejects oversized custom ranges',()=>{
  const scope={target:'test',range:'7d',compare:false};
  assert.deepEqual(analyticsWindow(scope,now),{fromUtc:'2026-09-08T18:00:00.000Z',toUtc:'2026-09-15T18:00:00.000Z'});
  assert.throws(()=>analyticsWindow({...scope,range:'custom',from:'2026-08-15T00:00:00Z',to:'2026-09-15T00:00:00Z'},now),/7 days/);
  assert.throws(()=>analyticsWindow({...scope,range:'custom'},now),/up to 7 days/);
});
test('backfill parser rejects general inventory and unrelated job kinds',()=>{
  const target='11111111-1111-4111-8111-111111111111';
  const at='2026-09-15T00:00:00Z';
  const item={jobId:target,jobKind:'backfill',status:'succeeded',requestedAtUtc:at,startedAtUtc:at,completedAtUtc:at,attempt:1,targetRevision:1};
  const page={targetId:target,surface:'backfill',items:[item],state:'complete',fromUtc:'2026-09-14T00:00:00Z',toUtc:at,snapshotUtc:at,cutoffUtc:at,generation:1,targetRevision:1,nextCursor:null};
  assert.equal(parseAnalyticsSurfacePage(page,'backfill',target).items.length,1);
  assert.throws(()=>parseAnalyticsSurfacePage({...page,surface:'jobs'},'backfill',target),/surface/);
  assert.throws(()=>parseAnalyticsSurfacePage({...page,items:[{...item,jobKind:'rollup'}]},'backfill',target),/non-backfill/);
});
