import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { transformWithOxc } from "vite";
import { agentHistoryPage, resolveAgentWindow, validateAgentWindow, healthCheckHref } from "../src/features/operations/agentHistoryModel.ts";
import { readAgentHistory } from "../src/features/operations/agentHistoryApi.ts";
import { readRoute } from "../src/dashboardModel.ts";
import { issueHref, readOverviewScope } from "../src/features/overview/overviewModel.ts";
import { clearApiCache } from "../src/apiClient.ts";

const target="11111111-1111-4111-8111-111111111111",job="22222222-2222-4222-8222-222222222222";
const window={fromUtc:"2026-09-30T01:00:00.000001Z",toUtc:"2026-09-30T02:00:00.000002Z"};
const row=(extra={})=>({jobId:job,historyInstanceId:10,stepId:0,runStatus:0,isJobOutcome:true,countsAsJobFailure:true,
  failureKind:"Failed",retryAttempt:0,durationSeconds:42,firstObservedAtUtc:"2026-09-30T01:30:00.0000000Z",
  sourceLocalStart:"2026-09-29T21:20:00",failureFingerprint:"a".repeat(64),contentAvailable:false,...extra});
const page=(items=[row()])=>({targetId:target,targetRevision:4,runId:job,observedAtUtc:"2026-09-30T03:00:00Z",
  state:"Complete",coverageFromUtc:window.fromUtc,coverageToUtc:window.toUtc,items,hasMore:false,nextCursor:null,truncated:false});

test("Agent markers use first observation, count job outcomes once and retain supporting step/retry evidence",()=>{
  const result=agentHistoryPage(page([row(),row({historyInstanceId:11,stepId:1,isJobOutcome:false,countsAsJobFailure:false}),
    row({historyInstanceId:12,runStatus:2,failureKind:"Retry",countsAsJobFailure:false}),
    row({historyInstanceId:13,runStatus:3,failureKind:"Cancelled",countsAsJobFailure:false})]),target,4,window);
  assert.equal(result.rows.length,4); assert.equal(result.markers.length,1);
  assert.equal(result.markers[0].timeUtc,row().firstObservedAtUtc);
  assert.match(result.markers[0].label,/first observed/);
  assert.doesNotMatch(result.markers[0].label,/2026-09-29/);
});
test("Agent history binds target, target revision, exact coverage and the exclusive microsecond window",()=>{
  for(const changed of [{targetId:job},{targetRevision:5},{coverageFromUtc:window.fromUtc.replace("000001","000000")},{coverageToUtc:null}])
    assert.throws(()=>agentHistoryPage({...page(),...changed},target,4,window));
  assert.equal(agentHistoryPage(page([row({firstObservedAtUtc:window.fromUtc})]),target,4,window).markers.length,1);
  for(const firstObservedAtUtc of [window.toUtc,"2026-09-30T01:00:00.000000Z","2026-09-30T01:30:00+01:00","2026-02-30T01:30:00Z"])
    assert.throws(()=>agentHistoryPage(page([row({firstObservedAtUtc})]),target,4,window));
  assert.throws(()=>agentHistoryPage(page([row(),row()]),target,4,window),/repeated/);
  assert.throws(()=>agentHistoryPage(page([row({JobId:target})]),target,4,window),/ambiguous/);
});
test("Agent history rejects unsupported windows without silently clipping and retains exact shared UTC bounds",()=>{
  assert.doesNotThrow(()=>validateAgentWindow({fromUtc:"2026-09-23T02:00:00Z",toUtc:"2026-09-30T02:00:00Z"}));
  assert.throws(()=>validateAgentWindow({fromUtc:"2026-09-23T01:59:59.999999Z",toUtc:"2026-09-30T02:00:00Z"}),/seven days/);
  assert.throws(()=>validateAgentWindow({fromUtc:window.toUtc,toUtc:window.fromUtc}));
  assert.deepEqual(resolveAgentWindow({target,range:"custom",from:window.fromUtc,to:window.toUtc,compare:false},Date.parse("2026-09-30T04:00:00Z")),window);
});
test("Agent history URLs restore the shared selection and explicitly select SQL Agent",()=>{
  const scope={target,range:"custom",from:window.fromUtc,to:window.toUtc,compare:true};
  const href=healthCheckHref(scope,target,"agent");
  assert.deepEqual(readOverviewScope(href),scope);
  assert.deepEqual(readRoute(href),{page:"operations",target,healthCheck:"agent"});
  assert.deepEqual(readRoute(href.replace("check=agent","check=unknown")),{page:"operations",target});
  assert.deepEqual(readRoute(issueHref(scope,target,"operations",window.toUtc,"SQL Agent failures")),{page:"operations",target,healthCheck:"agent"});
});
async function mockFetch(handler,run) {
  const previous=globalThis.fetch; clearApiCache(); globalThis.fetch=handler;
  try {await run();} finally {globalThis.fetch=previous;clearApiCache();}
}
test("Agent API paging preserves the fixed window, limit and opaque cursor",async()=>{
  const urls=[];
  await mockFetch(async url=>{urls.push(new URL(String(url),"https://lab.invalid"));return new Response(JSON.stringify(page()),{headers:{"content-type":"application/json"}});},async()=>{
    const signal=new AbortController().signal;
    await readAgentHistory(target,4,window,signal);
    await readAgentHistory(target,4,window,signal,"opaque+/=");
  });
  for(const url of urls) {assert.equal(url.searchParams.get("fromUtc"),window.fromUtc);assert.equal(url.searchParams.get("toUtc"),window.toUtc);assert.equal(url.searchParams.get("limit"),"50");}
  assert.equal(urls[1].searchParams.get("cursor"),"opaque+/=");
});
test("Agent API denies malformed or mismatched pages and avoids reads for invalid windows",async()=>{
  for(const changed of [{targetRevision:5},{coverageToUtc:"2026-09-30T04:00:00Z"},{items:[row({countsAsJobFailure:false})]}])
    await mockFetch(async()=>new Response(JSON.stringify({...page(),...changed})),async()=>{
      await assert.rejects(()=>readAgentHistory(target,4,window,new AbortController().signal));
    });
  await mockFetch(()=>{throw new Error("Must not read");},async()=>{
    await assert.rejects(()=>readAgentHistory(target,4,{...window,toUtc:window.fromUtc},new AbortController().signal),/seven days/);
  });
});

const require=createRequire(import.meta.url);
let component;
test("rendered Agent evidence explains clocks, shows rows as inert text and preserves gap warnings",async()=>{
  let javascript=(await transformWithOxc(await readFile(new URL("../src/features/operations/AgentFailureHistoryPanel.tsx",import.meta.url),"utf8"),"AgentFailureHistoryPanel.tsx",{jsx:{runtime:"automatic"}})).code;
  const timeStub="data:text/javascript;base64,"+Buffer.from("export const useTimeFormatter=()=>value=>String(value);").toString("base64");
  const replacements={react:pathToFileURL(require.resolve("react")).href,"react/jsx-runtime":pathToFileURL(require.resolve("react/jsx-runtime")).href,
    "../../TimeDisplayContext":timeStub,"./agentHistoryModel.ts":new URL("../src/features/operations/agentHistoryModel.ts",import.meta.url).href,
    "./useAgentHistory.ts":new URL("../src/features/operations/useAgentHistory.ts",import.meta.url).href};
  for(const [from,to] of Object.entries(replacements))javascript=javascript.replaceAll(JSON.stringify(from),JSON.stringify(to));
  component=await import("data:text/javascript;base64,"+Buffer.from(javascript).toString("base64"));
  const model=agentHistoryPage(page(),target,4,window);
  const html=renderToStaticMarkup(createElement(component.AgentFailureEvidence,{page:{...model,rows:[{...model.rows[0],sourceLocalStart:"<script>unsafe</script>"}],hasMore:true,truncated:true}}));
  assert.match(html,/not its execution time/); assert.match(html,/timezone is unknown/);
  assert.match(html,/More history pages/); assert.match(html,/capture was truncated/);
  assert.match(html,/&lt;script&gt;unsafe&lt;\/script&gt;/); assert.doesNotMatch(html,/<script>/);
});
test("rendered unsupported Agent ranges do not offer paging or imply an empty successful history",()=>{
  const html=renderToStaticMarkup(createElement(component.AgentFailureHistoryPanel,{instanceId:target,targetRevision:4,
    scope:{target,range:"custom",from:"2026-08-01T00:00:00Z",to:"2026-08-20T00:00:00Z",compare:false},refresh:0}));
  assert.match(html,/seven days/); assert.doesNotMatch(html,/Next history page/);
  const empty=agentHistoryPage(page([]),target,4,window);
  const emptyHtml=renderToStaticMarkup(createElement(component.AgentFailureEvidence,{page:empty}));
  assert.match(emptyHtml,/Missing collection coverage can hide failures/);
});
