import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { transformWithOxc } from "vite";
import { parseBaseline } from "../src/features/analytics/analyticsParser.ts";
import { getBaselineWindow } from "../src/features/analytics/analyticsApi.ts";
import { clearApiCache } from "../src/apiClient.ts";
import { baselineLookupWindow, matchingReferences, memoryBaselineBands, ruleThresholds } from "../src/features/overview/chartReferenceModel.ts";
import { overviewChartRange } from "../src/features/overview/overviewChartRange.ts";
import { readChartRuleThresholds } from "../src/features/overview/chartReferenceApi.ts";

const target="11111111-1111-4111-8111-111111111111", other="22222222-2222-4222-8222-222222222222", gib=1073741824;
const from="2026-09-30T10:30:00Z", to="2026-09-30T12:15:00Z", source=baselineLookupWindow(from);
const baseline=(extra={})=>({metricKey:"host.memory.available_bytes",hourOfWeek:82,completeDays:28,sampleCount:4,
  mean:null,stddev:null,median:10*gib,mad:gib,p10:8*gib,p90:12*gib,lowerBound:null,upperBound:null,
  coverage:1,confidence:.5,windowStartUtc:source.fromUtc,windowEndUtc:source.toUtc,generation:3,visibilityState:"complete",
  dimensions:{},dimensionsSha256:"a".repeat(64),...extra});
const rule=(extra={})=>({ruleId:target,name:"SQL CPU",kind:"metric_threshold",metricId:"engine.sql_scheduler_cpu_percent",
  comparison:"greater_than",threshold:80,hysteresis:5,confirmationCount:3,confirmationSeconds:60,evaluationSeconds:30,
  clearConfirmationCount:2,enabled:true,revision:7,updatedAtUtc:"2026-09-30T14:00:00Z",...extra});
const series=(metric="host.memory.available_bytes",unit="GiB")=>[{targetId:target,label:"Collector-host memory",metric,unit,
  dimension:null,state:"observed",points:[{timeUtc:from,value:10,samples:1},{timeUtc:"2026-09-30T11:00:00Z",value:11,samples:1}]}];

test("baseline parser accepts the actual serialized repository result and retains dimension identity",()=>{
  const parsed=parseBaseline(baseline());
  assert.deepEqual(parsed.dimensions,{});assert.equal(parsed.dimensionsSha256,"a".repeat(64));assert.equal(parsed.p90,12*gib);
  for(const extra of [{unrecognized:true},{p10:13*gib},{mad:-1},{stddev:-1},{dimensionsSha256:"wrong"},{dimensions:{bad:42}}])
    assert.throws(()=>parseBaseline(baseline(extra)));
});
test("baseline source lookup uses the exact preceding 28 complete UTC days",()=>{
  assert.deepEqual(source,{fromUtc:"2026-09-02T00:00:00.000Z",toUtc:"2026-09-30T00:00:00.000Z"});
  assert.deepEqual(baselineLookupWindow("2026-09-30T00:00:00Z"),source);
  assert.throws(()=>baselineLookupWindow("invalid"));
});
test("baseline bands use UTC hour-of-week, convert bytes to GiB and leave missing hours as gaps",()=>{
  const bands=memoryBaselineBands(target,[parseBaseline(baseline()),parseBaseline(baseline({hourOfWeek:83,p10:9*gib,median:11*gib,p90:13*gib}))],from,to);
  assert.equal(bands.length,2);assert.equal(bands[0].fromUtc,"2026-09-30T10:30:00.000Z");
  assert.equal(bands[0].toUtc,"2026-09-30T11:00:00.000Z");assert.equal(bands[0].low,8);assert.equal(bands[0].median,10);
  assert.equal(bands[1].toUtc,"2026-09-30T12:00:00.000Z");assert.match(bands[0].label,/confidence 50%/);
  assert.equal(bands[0].visibilityState,"complete");assert.equal(bands[0].coverage,1);assert.equal(bands[0].confidence,.5);
  assert.equal(bands[0].sourceFromUtc,source.fromUtc);assert.equal(bands[0].sourceToUtc,source.toUtc);
  const sunday=memoryBaselineBands(target,[parseBaseline(baseline({hourOfWeek:0}))],"2026-10-04T00:00:00Z","2026-10-04T00:30:00Z");
  assert.equal(sunday.length,1);
});
test("baseline bands refuse future, ambiguous, dimensionless legacy and incomplete evidence",()=>{
  for(const extra of [{windowEndUtc:to},{dimensions:{mount:"C:"}},{dimensions:undefined},{visibilityState:"partial"},{p10:null},{sampleCount:0}])
    assert.deepEqual(memoryBaselineBands(target,[parseBaseline(baseline(extra))],from,to),[]);
  assert.throws(()=>memoryBaselineBands(target,[parseBaseline(baseline()),parseBaseline(baseline())],from,to),/repeats/);
  assert.throws(()=>memoryBaselineBands(target,Array.from({length:200},()=>parseBaseline(baseline())),from,to),/limit/);
  const rows=[parseBaseline(baseline({generation:2,median:9*gib})),parseBaseline(baseline())];
  assert.equal(memoryBaselineBands(target,rows,from,to)[0].median,10);
});
test("threshold lines use only enabled metric rules with explicit matching units",()=>{
  const thresholds=ruleThresholds(target,[rule(),rule({enabled:false}),rule({kind:"collector_health",metricId:null}),
    rule({metricId:"engine.unknown"}),rule({metricId:"engine.os_available_memory_bytes",threshold:8*gib})]);
  assert.equal(thresholds.length,2);assert.equal(thresholds[1].value,8);assert.equal(thresholds[1].unit,"GiB");
  assert.match(thresholds[0].label,/Current rule/);assert.match(thresholds[0].label,/revision 7/);
});
test("reference binding and scale reject other targets, metrics, units and out-of-window bands",()=>{
  const band=memoryBaselineBands(target,[parseBaseline(baseline())],from,to)[0];
  const references={bands:[band],thresholds:[{targetId:target,metric:band.metric,unit:"GiB",value:18,label:"Current test"}]};
  const range=overviewChartRange(series(),undefined,from,to,references);
  assert.ok(range.minimum<8&&range.maximum>18);
  assert.equal(overviewChartRange([],undefined,from,to,references),null);
  for(const change of [{targetId:other},{metric:"engine.other"},{unit:"bytes"}])
    assert.deepEqual(matchingReferences(series(),{bands:[{...band,...change}],thresholds:[]}),{bands:[],thresholds:[]});
  assert.deepEqual(overviewChartRange(series(),undefined,from,to,{bands:[{...band,fromUtc:to,toUtc:"2026-09-30T14:00:00Z",high:1000}],thresholds:[]}),overviewChartRange(series(),undefined,from,to));
});
test("baseline client pins target, metric and source window instead of using now defaults",async()=>{
  const previous=globalThis.fetch;clearApiCache();let calls=0;
  const response=extra=>new Response(JSON.stringify({targetId:target,metricKey:"host.memory.available_bytes",...source,state:"complete",items:[baseline()],...extra}));
  globalThis.fetch=async url=>{calls++;const query=new URL(String(url),"https://fixture.invalid").searchParams;
    assert.equal(query.get("fromUtc"),source.fromUtc);assert.equal(query.get("toUtc"),source.toUtc);assert.equal(query.get("limit"),"200");return response({});};
  try {
    assert.equal((await getBaselineWindow(target,"host.memory.available_bytes",source,new AbortController().signal)).length,1);
    for(const extra of [{targetId:other},{metricKey:"host.cpu.percent"},{toUtc:"2026-09-30T01:00:00Z"},{items:[baseline({metricKey:"host.cpu.percent"})]},
      {items:[baseline({windowStartUtc:"2026-08-01T00:00:00Z"})]},{items:[baseline({windowEndUtc:"2026-10-01T00:00:00Z"})]}]) {
      clearApiCache();globalThis.fetch=async()=>response(extra);
      await assert.rejects(getBaselineWindow(target,"host.memory.available_bytes",source,new AbortController().signal));
    }
    assert.equal(calls,1);
  } finally {globalThis.fetch=previous;clearApiCache();}
});
test("threshold loader pins rule listing across paging and bounds reads without implying completeness",async()=>{
  const previous=globalThis.fetch;clearApiCache();let calls=0;const queries=[];
  globalThis.fetch=async url=>{const query=new URL(String(url),"https://fixture.invalid").searchParams;queries.push(query.get("cursor"));
    calls++;return new Response(JSON.stringify({targetId:target,listingStartedUtc:"2026-09-30T13:00:00Z",hasMore:true,nextCursor:Buffer.from(`page-${calls}`).toString("base64"),
      items:[rule({ruleId:`11111111-1111-4111-8111-${String(calls).padStart(12,"0")}`})]}));};
  try {
    const result=await readChartRuleThresholds(target,new AbortController().signal);
    assert.equal(calls,4);assert.equal(result.truncated,true);assert.equal(result.thresholds.length,4);
    assert.equal(queries[0],null);assert.equal(queries[1],Buffer.from("page-1").toString("base64"));
    for(const mode of ["duplicate","cursor","changed"]){
      clearApiCache();let page=0;
      globalThis.fetch=async()=>{page++;return new Response(JSON.stringify({targetId:target,
        listingStartedUtc:mode==="changed"&&page>1?"2026-09-30T14:00:00Z":"2026-09-30T13:00:00Z",hasMore:true,
        nextCursor:mode==="cursor"?"cGFnZQ==":Buffer.from(`next-${page}`).toString("base64"),
        items:[rule({ruleId:mode==="duplicate"?target:`11111111-1111-4111-8111-${String(page).padStart(12,"0")}`})]}));};
      await assert.rejects(readChartRuleThresholds(target,new AbortController().signal));
    }
  }finally{globalThis.fetch=previous;clearApiCache();}
});
test("rendered chart contains bands, median and threshold lines with accessible inert reference evidence",async()=>{
  const require=createRequire(import.meta.url);
  let javascript=(await transformWithOxc(await readFile(new URL("../src/features/overview/OverviewChart.tsx",import.meta.url),"utf8"),"OverviewChart.tsx",{jsx:{runtime:"automatic"}})).code;
  const timeStub="data:text/javascript;base64,"+Buffer.from("export const useTimeDisplay=()=>({mode:'utc'});").toString("base64");
  const replacements={react:pathToFileURL(require.resolve("react")).href,"react/jsx-runtime":pathToFileURL(require.resolve("react/jsx-runtime")).href,
    "../../TimeDisplayContext":timeStub,"../../timeDisplay":new URL("../src/timeDisplay.ts",import.meta.url).href,
    "./chartTimeModel":new URL("../src/features/overview/chartTimeModel.ts",import.meta.url).href,
    "../../components/useChartGesture":new URL("../src/components/useChartGesture.ts",import.meta.url).href,
    "./overviewChartRange":new URL("../src/features/overview/overviewChartRange.ts",import.meta.url).href,
    "./chartReferenceModel.ts":new URL("../src/features/overview/chartReferenceModel.ts",import.meta.url).href};
  for(const [key,value] of Object.entries(replacements))javascript=javascript.replaceAll(JSON.stringify(key),JSON.stringify(value));
  const {OverviewChart}=await import("data:text/javascript;base64,"+Buffer.from(javascript).toString("base64"));
  const bands=memoryBaselineBands(target,[parseBaseline(baseline())],from,to);
  const thresholds=[{targetId:target,metric:"host.memory.available_bytes",unit:"GiB",value:6,label:"<script>rule</script>"}];
  const html=renderToStaticMarkup(createElement(OverviewChart,{series:series(),fromUtc:from,toUtc:to,references:{bands,thresholds}}));
  assert.match(html,/class="chart-baseline-band"/);assert.match(html,/class="chart-threshold"/);
  assert.match(html,/View chart references/);assert.match(html,/Baseline reference hours/);assert.match(html,/not historical settings or an alert decision/);
  assert.match(html,/&lt;script&gt;rule&lt;\/script&gt;/);assert.doesNotMatch(html,/<script>/);
});
