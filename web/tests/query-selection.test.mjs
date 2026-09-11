import test from "node:test";
import assert from "node:assert/strict";
import { metricsForObservation, queryPerformanceDatabaseIds, queryPerformanceDatabaseOptions } from "../src/features/queries/querySelection.ts";
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
test("database selector includes catalog entries even when a ranking page has no row",()=>{
 const page={items:[{query:{databaseId:6,queryFingerprint:"a"}}]};
 const status={databaseStatuses:[{databaseId:9},{databaseId:5},{databaseId:6}],databaseCatalog:[{databaseId:5,databaseName:"SqlObserverLabSales"},{databaseId:6,databaseName:"SqlObserverLabWarehouse"},{databaseId:7,databaseName:"SqlObserverLabPublisher"}]};
 assert.deepEqual(queryPerformanceDatabaseIds(page,status),[5,6,7,9]);
 assert.deepEqual(queryPerformanceDatabaseOptions(page,status),[
  {databaseId:5,databaseName:"SqlObserverLabSales"},
  {databaseId:6,databaseName:"SqlObserverLabWarehouse"},
  {databaseId:7,databaseName:"SqlObserverLabPublisher"},
  {databaseId:9,databaseName:undefined},
 ]);
 assert.deepEqual(queryPerformanceDatabaseIds(undefined,undefined),[]);
});
