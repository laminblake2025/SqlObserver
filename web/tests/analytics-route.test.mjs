import test from 'node:test';
import assert from 'node:assert/strict';
import { analyticsSurfaceHref, readRoute, retainAnalyticsSurfaceHref } from '../src/dashboardModel.ts';
import { analyticsSurfaceCatalog } from '../src/features/analytics/analyticsSurfaceCatalog.ts';
const target='11111111-1111-4111-8111-000000000001';
const scope={target,databaseId:6,range:'custom',from:'2026-09-26T08:00:00.123456Z',to:'2026-09-26T12:00:00.654321Z',compare:true};
test('every native Analytics category restores through its URL with exact shared scope',()=>{
 for(const option of analyticsSurfaceCatalog){
  const href=analyticsSurfaceHref(scope,target,option.value),params=new URLSearchParams(href.split('?')[1]);
  assert.equal(readRoute(href).analyticsSurface,option.value);
  assert.equal(params.get('from'),scope.from);assert.equal(params.get('to'),scope.to);
  assert.equal(params.get('databaseId'),'6');assert.equal(params.get('compare'),'1');
  assert.deepEqual(params.getAll('surface'),[option.value]);
 }
});
test('missing category defaults to incidents while malformed or duplicate exact selections remain refused',()=>{
 assert.equal(readRoute(`#/analytics?target=${target}`).analyticsSurface,'incidents');
 for(const query of ['surface=','surface=constructor','surface=unsupported','surface=jobs&surface=jobs','surface=jobs&surface=incidents']){
  const route=readRoute(`#/analytics?target=${target}&${query}`);
  assert.equal(route.analyticsSelectionInvalid,true,query);assert.equal(route.analyticsSurface,undefined);
 }
 assert.throws(()=>analyticsSurfaceHref(scope,target,'constructor'));
});
test('shared navigation retains valid and refused Analytics selections without inventing a replacement',()=>{
 for(const query of ['surface=host%2Fmetrics','surface=jobs&surface=incidents','surface=invalid']){
  const current=`#/analytics?target=${target}&${query}`;
  const next=retainAnalyticsSurfaceHref(`#/analytics?target=${target}&databaseId=7`,current);
  assert.deepEqual(new URLSearchParams(next.split('?')[1]).getAll('surface'),new URLSearchParams(query).getAll('surface'));
  assert.equal(new URLSearchParams(next.split('?')[1]).get('databaseId'),'7');
 }
});
test('explicit category selection replaces old selection and other screens do not inherit it',()=>{
 const current=`#/analytics?target=${target}&surface=jobs`;
 const selected=analyticsSurfaceHref(scope,target,'diagnostics/search');
 assert.equal(retainAnalyticsSurfaceHref(selected,current),selected);
 assert.equal(retainAnalyticsSurfaceHref('#/health?target=x',current),'#/health?target=x');
 assert.equal(readRoute('#/health?surface=invalid').analyticsSelectionInvalid,undefined);
});
