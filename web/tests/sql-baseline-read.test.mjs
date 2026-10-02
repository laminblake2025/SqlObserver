import assert from "node:assert/strict";
import test from "node:test";
import { parseSqlBaselinePage, sqlBaselineChecksum } from "../src/features/analytics/sqlBaselineModel.ts";
import { getSqlBaselineWindow } from "../src/features/analytics/analyticsApi.ts";
import { sqlBaselineBands, matchingReferences } from "../src/features/overview/chartReferenceModel.ts";
import { clearApiCache } from "../src/apiClient.ts";

const targetId="11111111-1111-4111-8111-111111111111", metricKey="engine.process_physical_memory_bytes", gib=1073741824;
const window={fromUtc:"2026-09-02T00:00:00Z",toUtc:"2026-09-30T00:00:00Z"};
const expected={targetId,targetRevision:4,metricKey,...window};
const identity={catalogVersion:2,catalogChecksum:sqlBaselineChecksum,algorithmVersion:"sql-baseline-v2",sourceAlgorithmVersion:"sql-rollup-v2"};
function response() {
  return {targetId,targetRevision:4,metricKey,unit:"bytes",...window,snapshotUtc:"2026-09-30T10:00:00Z",generation:1,state:"complete",...identity,
    items:Array.from({length:168},(_,hourOfWeek)=>({...identity,sourceCutoffUtc:"2026-09-30T00:10:00Z",statistics:{metricKey,
      dimensionsSha256:"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",dimensions:{},hourOfWeek,completeDays:28,sampleCount:4,
      mean:null,stddev:null,median:10*gib,mad:gib,p10:8*gib,p90:12*gib,coverage:1,confidence:.5,lowerBound:null,upperBound:null,
      windowStartUtc:window.fromUtc,windowEndUtc:window.toUtc,generation:1,visibilityState:"complete"}}))};
}
test("SQL baseline reader accepts the versioned serialized shape and requires all hourly identities",()=>{
  const page=parseSqlBaselinePage(response(),expected); assert.equal(page.items.length,168); assert.equal(page.items[82].median,10*gib);
  for(const extra of [{catalogVersion:1},{catalogChecksum:"bad"},{targetRevision:5},{targetId:"other"},{unit:"percent"},{generation:2},
    {toUtc:"2026-10-01T00:00:00Z"},{state:"partial"},{items:response().items.slice(1)}]) assert.throws(()=>parseSqlBaselinePage({...response(),...extra},expected));
  for(const mutate of [p=>p.items[0].statistics.hourOfWeek=1,p=>p.items[0].statistics.metricKey="engine.user_connections",
    p=>p.items[0].catalogVersion=1,p=>p.items[0].statistics.dimensions={volume:"C:"},p=>p.items[0].statistics.confidence=1,
    p=>p.items[0].statistics.p10=13*gib,p=>p.items[0].statistics.median=Infinity,p=>p.items[0].sourceCutoffUtc="2026-09-30T11:00:00Z",
    p=>p.items[0].sourceCutoffUtc="2026-09-30T00:11:00Z"]) {const value=response();mutate(value);assert.throws(()=>parseSqlBaselinePage(value,expected));}
});
test("SQL chart references use their metric unit and UTC hours without borrowing host evidence",()=>{
  const page=parseSqlBaselinePage(response(),expected);
  const bands=sqlBaselineBands(page,"2026-09-30T10:30:00Z","2026-09-30T12:15:00Z");
  assert.equal(bands.length,3);assert.equal(bands[0].low,8);assert.equal(bands[0].high,12);assert.equal(bands[0].unit,"GiB");
  assert.equal(bands[0].fromUtc,"2026-09-30T10:30:00.000Z");assert.match(bands[0].label,/SQL process physical memory baseline/);
  assert.match(bands[0].label,/confidence 50%/);
  const series=[{targetId,metric:metricKey,unit:"GiB",dimension:null}];assert.equal(matchingReferences(series,{bands,thresholds:[]}).bands.length,3);
  for(const change of [{metric:"host.memory.available_bytes"},{unit:"bytes"},{targetId:"other"}])
    assert.equal(matchingReferences([{...series[0],...change}],{bands,thresholds:[]}).bands.length,0);
  assert.equal(sqlBaselineBands(page,"2026-09-29T10:30:00Z","2026-09-29T12:15:00Z").length,0);
});
test("partial SQL history stays labelled and unavailable hours are gaps",()=>{
  const value=response();value.state="partial";
  for(const item of value.items) {const row=item.statistics;row.completeDays=1;row.sampleCount=0;row.coverage=1/28;row.confidence=0;
    row.median=null;row.mad=null;row.p10=null;row.p90=null;row.visibilityState="unavailable";}
  Object.assign(value.items[82].statistics,{sampleCount:1,median:10*gib,mad:0,p10:10*gib,p90:10*gib,confidence:(1/28)/8,visibilityState:"partial"});
  const page=parseSqlBaselinePage(value,expected),bands=sqlBaselineBands(page,"2026-09-30T10:30:00Z","2026-09-30T12:15:00Z");
  assert.equal(bands.length,1);assert.match(bands[0].label,/partial/);assert.match(bands[0].label,/coverage 4%/);
  assert.equal(bands[0].visibilityState,"partial");assert.equal(bands[0].coverage,1/28);assert.equal(bands[0].confidence,(1/28)/8);
  assert.equal(bands[0].sourceFromUtc,window.fromUtc);assert.equal(bands[0].sourceToUtc,window.toUtc);
  assert.equal(parseSqlBaselinePage({...response(),items:[],state:"no_data"},expected).sourceCutoffUtc,null);
});

test("hourly references retain microsecond clips and include an end bucket only when it is inside the exact window",()=>{
  const page=parseSqlBaselinePage(response(),expected);
  const bands=sqlBaselineBands(page,"2026-09-30T10:30:00.1234560Z","2026-09-30T12:00:00.000001Z");
  assert.equal(bands.length,3);assert.equal(bands[0].fromUtc,"2026-09-30T10:30:00.123456Z");
  assert.equal(bands[2].fromUtc,"2026-09-30T12:00:00.000Z");assert.equal(bands[2].toUtc,"2026-09-30T12:00:00.000001Z");
  assert.equal(sqlBaselineBands(page,"2026-09-30T10:30:00.123456Z","2026-09-30T12:00:00Z").length,2);
  const narrow=sqlBaselineBands(page,"2026-09-30T10:30:00.123456Z","2026-09-30T10:30:00.123457Z");
  assert.equal(narrow.length,1);assert.equal(narrow[0].fromUtc,"2026-09-30T10:30:00.123456Z");
  assert.equal(narrow[0].toUtc,"2026-09-30T10:30:00.123457Z");
});
test("SQL baseline client pins target revision, version and source window",async()=>{
  const previous=globalThis.fetch;clearApiCache();
  globalThis.fetch=async url=>{const parsed=new URL(String(url),"https://fixture.invalid");
    assert.match(parsed.pathname,/analytics\/sql-baselines$/);assert.equal(parsed.searchParams.get("targetRevision"),"4");
    assert.equal(parsed.searchParams.get("catalogVersion"),"2");assert.equal(parsed.searchParams.get("generation"),"1");
    assert.equal(parsed.searchParams.get("fromUtc"),window.fromUtc);assert.equal(parsed.searchParams.get("toUtc"),window.toUtc);
    return new Response(JSON.stringify(response()));};
  try {assert.equal((await getSqlBaselineWindow(targetId,4,metricKey,window,new AbortController().signal)).items.length,168);}
  finally {globalThis.fetch=previous;clearApiCache();}
});
