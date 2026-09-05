import test from "node:test";
import assert from "node:assert/strict";
import { metricsForObservation } from "../src/features/queries/querySelection.ts";
const item={query:{databaseId:7,queryFingerprint:"a"},source:"query_store",semantics:"query_store_interval",collectionRunId:"run",observationKey:"observation",metrics:{cpuMilliseconds:0}};
test("selection keeps zero and uses only the exact observation's metrics",()=>{
 assert.equal(metricsForObservation(item,undefined).cpuMilliseconds,0);
 assert.equal(metricsForObservation(undefined,undefined),undefined);
 const row={...item,metrics:{cpuMilliseconds:0,durationMilliseconds:15}};
 assert.equal(metricsForObservation(item,{items:[row]}).durationMilliseconds,15);
 for(const change of [{collectionRunId:"other"},{observationKey:"other"},{source:"plan_cache"},{semantics:"plan_cache_cumulative"},{query:{databaseId:8,queryFingerprint:"a"}}]) {
  assert.equal(metricsForObservation(item,{items:[{...row,...change}]}).durationMilliseconds,undefined);
 }
});
