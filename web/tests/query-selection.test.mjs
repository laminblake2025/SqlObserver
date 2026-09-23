import test from "node:test";
import assert from "node:assert/strict";
import { historyForObservation, sameQueryObservation, metricsForObservation, queryPerformanceDatabaseIds, queryPerformanceDatabaseOptions } from "../src/features/queries/querySelection.ts";
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

test("second plan with the same query remains the exact selected observation and history series", () => {
 const first={...item,plan:{planFingerprint:"first"},observationKey:"first",metrics:{cpuMilliseconds:11}};
 const second={...item,plan:{planFingerprint:"second"},observationKey:"second",metrics:{cpuMilliseconds:0}};
 const selectedHistory={...second,metrics:{cpuMilliseconds:0,durationMilliseconds:0}};
 const samePlanOtherSource={...selectedHistory,source:"plan_cache",semantics:"plan_cache_delta",observationKey:"third"};
 const otherSemantics={...samePlanOtherSource,semantics:"plan_cache_cumulative",observationKey:"fourth"};
 const page={items:[first,selectedHistory,samePlanOtherSource,otherSemantics]};
 assert.equal(sameQueryObservation(first,second),false);
 assert.equal(sameQueryObservation(second,selectedHistory),true);
 assert.deepEqual(metricsForObservation(second,page),{cpuMilliseconds:0,durationMilliseconds:0});
 assert.deepEqual(historyForObservation(second,page),[selectedHistory]);
 assert.deepEqual(historyForObservation(samePlanOtherSource,page),[samePlanOtherSource]);
 assert.equal(metricsForObservation({...second,plan:{planFingerprint:"different"}},page).durationMilliseconds,undefined);
});
