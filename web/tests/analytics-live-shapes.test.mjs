import assert from "node:assert/strict";
import test from "node:test";
import { parseAnalyticsSurfacePage } from "../src/features/analytics/analyticsParser.ts";
const target="11111111-1111-4111-8111-111111111111";
const at="2026-09-05T12:00:00.123456+00:00";
const page=(surface,items,state="complete")=>({targetId:target,surface,items,state,nextCursor:null,cutoffUtc:at,snapshotUtc:at,generation:5,targetRevision:1,fromUtc:"2026-09-05T00:00:00Z",toUtc:"2026-09-06T00:00:00Z"});
test("host status accepts the persisted opaque host identity and profile generation",()=>{
 const item={hostId:"6b641784-45e4-7e6a-e059-228b6034df6d",targetRevision:1,bindingRevision:1,hostName:"lab",bindingState:"active",capabilityState:"available",observedAtUtc:at,generation:1};
 assert.equal(parseAnalyticsSurfacePage(page("host/status",[item]),"host/status",target).items[0].hostId,item.hostId);
 assert.throws(()=>parseAnalyticsSurfacePage(page("host/status",[{...item,hostId:"invalid"}])));
 assert.throws(()=>parseAnalyticsSurfacePage(page("host/status",[{...item,generation:-1}])));
});
test("replication zero-offset timestamps preserve explicit missing topology evidence",()=>{
 const item={observedAtUtc:at,runId:target,role:"unknown",synchronizationState:"unknown",sendQueueBytes:null,redoQueueBytes:null,pendingCommands:null,latencySeconds:null,visibilityScope:3,coverage:"visibility_gap",visibilityGap:{kind:"visibility_gap",reason:"distribution_database_unbound",identity:"a".repeat(64)},stateAvailable:false,targetRevision:1};
 const result=parseAnalyticsSurfacePage(page("replication/evidence",[item],"visibility_gap"),"replication/evidence",target);
 assert.equal(result.state,"visibility_gap");assert.equal(result.items[0].stateAvailable,false);
 assert.throws(()=>parseAnalyticsSurfacePage(page("replication/evidence",[{...item,observedAtUtc:at.replace("+00:00","+01:00")}])));
});
test("replication command counts and latency retain distinct bounded units",()=>{
 const item={observedAtUtc:at,runId:target,role:"distributor",synchronizationState:"healthy",sendQueueBytes:null,redoQueueBytes:null,pendingCommands:1000,latencySeconds:5.79,visibilityScope:1,coverage:"complete",visibilityGap:null,stateAvailable:true,targetRevision:1};
 for(const surface of ["replication/status","replication/evidence"]){
  const result=parseAnalyticsSurfacePage(page(surface,[item]),surface,target);
  assert.equal(result.items[0].pendingCommands,1000);assert.equal(result.items[0].latencySeconds,5.79);
  for(const change of [{pendingCommands:-1},{pendingCommands:1.5},{pendingCommands:2_000_000_001},{latencySeconds:-1},{latencySeconds:86401}]){
   assert.throws(()=>parseAnalyticsSurfacePage(page(surface,[{...item,...change}]),surface,target));
  }
 }
});
