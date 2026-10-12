import test from 'node:test';
import assert from 'node:assert/strict';
import { adminRestorePageLimit, adminSelectionHref, adminSelectionMessage, appendAdminPage, readAdminSelection, retainAdminSelectionHref } from '../src/features/alerts/adminSelectionModel.ts';
import { baselineBandsNeedAttention } from '../src/features/overview/chartReferenceModel.ts';
const target='11111111-1111-4111-8111-111111111111', other='22222222-2222-4222-8222-222222222222';
const selected='abcdefab-1234-4234-8234-abcdefabcdef';
const scope={target,range:'custom',from:'2026-10-01T00:00:00.000001Z',to:'2026-10-01T02:00:00.000001Z',compare:true,databaseId:3};
for (const [page,key] of [['rules','ruleId'],['maintenance','windowId'],['destinations','destinationId']]) {
 test(`${page} shared selection restores exact identity and workspace context`,()=>{
  const href=adminSelectionHref(scope,page,selected.toUpperCase());
  assert.equal(readAdminSelection(href,page,target.toUpperCase()),selected);
  const params=new URLSearchParams(href.split('?')[1]);
  assert.equal(params.get('from'),scope.from);assert.equal(params.get('to'),scope.to);
  assert.equal(params.get('databaseId'),'3');assert.equal(params.get('compare'),'1');
  assert.equal(readAdminSelection(adminSelectionHref(scope,page),page),undefined);
  assert.equal(readAdminSelection(href,page,other),null);
  for (const bad of [`${href}&${key}=${selected}`,href.replace(selected,'bad'),`${href}&target=${target}`,href.replace(selected,'00000000-0000-0000-0000-000000000000')])
   assert.equal(readAdminSelection(bad,page),null);
  assert.equal(readAdminSelection(href.replace(`/${page}?`,'/activity?'),page),null);
  const wrongKey=key==='ruleId'?'windowId':'ruleId';
  assert.equal(readAdminSelection(adminSelectionHref(scope,page)+`&${wrongKey}=${selected}`,page),null);
  const retained=retainAdminSelectionHref(adminSelectionHref({...scope,range:'1h'},page),href);
  assert.equal(readAdminSelection(retained,page),selected);
  assert.equal(readAdminSelection(retainAdminSelectionHref(adminSelectionHref({...scope,target:other},page),href),page),undefined);
 });
}
test('invalid and missing admin selections never imply a default item',()=>{
 assert.equal(adminRestorePageLimit,5);
 assert.match(adminSelectionMessage(null,false,false,false),/invalid/);
 assert.match(adminSelectionMessage(selected,false,true,true),/Finding/);
 assert.match(adminSelectionMessage(selected,false,false,true),/Load more/);
 assert.match(adminSelectionMessage(selected,false,false,false),/unavailable/);
 assert.equal(adminSelectionMessage(undefined,false,false,false),undefined);
 assert.equal(adminSelectionMessage(selected,true,false,true),undefined);
 assert.throws(()=>adminSelectionHref(scope,'rules','bad'));
 assert.equal(readAdminSelection(adminSelectionHref(scope,'rules',selected)+'&windowId='+selected,'rules'),null);
});
test('restored admin pages bind to the same target and listing and reject duplicate identities',()=>{
 const page={targetId:target,listingStartedUtc:'2026-10-01T00:00:00Z',items:[{id:target}],hasMore:true,nextCursor:'first'};
 const next={...page,items:[{id:selected}],hasMore:false,nextCursor:undefined};
 const id=item=>item.id;
 assert.deepEqual(appendAdminPage(page,next,id).items,[...page.items,...next.items]);
 assert.throws(()=>appendAdminPage(page,{...next,targetId:other},id),/listing changed/);
 assert.throws(()=>appendAdminPage(page,{...next,listingStartedUtc:'2026-10-01T00:00:01Z'},id),/listing changed/);
 assert.throws(()=>appendAdminPage({...page,items:[{id:selected.toUpperCase()}]},next,id),/listing changed/);
});
test('quiet baseline evidence requires complete contiguous coverage, including exact bounds',()=>{
 const band=(fromUtc,toUtc)=>({fromUtc,toUtc,visibilityState:'complete',coverage:1});
 const bands=[band(scope.from,'2026-10-01T01:00:00Z'),band('2026-10-01T01:00:00Z',scope.to)];
 assert.equal(baselineBandsNeedAttention(bands,scope.from,scope.to),false);
 assert.equal(baselineBandsNeedAttention([],scope.from,scope.to),true);
 assert.equal(baselineBandsNeedAttention(bands.slice(0,1),scope.from,scope.to),true);
 assert.equal(baselineBandsNeedAttention([{...bands[0],coverage:.99},bands[1]],scope.from,scope.to),true);
 assert.equal(baselineBandsNeedAttention([{...bands[0],visibilityState:'partial'},bands[1]],scope.from,scope.to),true);
 assert.equal(baselineBandsNeedAttention([bands[0],{...bands[1],fromUtc:'2026-10-01T01:00:00.000001Z'}],scope.from,scope.to),true);
});
