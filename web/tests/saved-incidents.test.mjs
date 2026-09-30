import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { pathToFileURL } from "node:url";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { transformWithOxc } from "vite";
import { incidentUtc, prepareIncident, parseSavedIncident, parseSavedIncidentPage, verifySavedIncident, savedIncidentHref, incidentSelection } from "../src/features/incidents/incidentModel.ts";
import { getSavedIncident, listSavedIncidents, saveIncident, IncidentRequestError } from "../src/features/incidents/incidentApi.ts";
import { canSaveIncident } from "../src/features/targets/meApi.ts";
import { readOverviewScope, overviewWindow } from "../src/features/overview/overviewModel.ts";
import { clearApiCache } from "../src/apiClient.ts";
import { createSavedInvestigationReport } from "../src/features/incidents/savedIncidentReportApi.ts";

const target="11111111-1111-4111-8111-111111111111",thread="22222222-2222-4222-8222-222222222222",operation="33333333-3333-4333-8333-333333333333";
const window={ fromUtc:"2026-09-29T12:00:00.000001Z",toUtc:"2026-09-29T13:00:00.000002Z" };
const now=Date.parse("2026-09-30T12:00:00Z");
const command=()=>prepareIncident(window,7,"Lock investigation","<script>Plain text</script>\nNext line",operation,now);
const fixture=()=>({ targetId:target,targetRevision:7,threadId:thread,operationId:operation,
  fromUtc:window.fromUtc.replace("Z","+00:00"),toUtc:window.toUtc.replace("Z","+00:00"),savedAtUtc:"2026-09-29T14:00:00+00:00",
  title:"Lock investigation",notes:"<script>Plain text</script>\nNext line",evidenceTruncated:false,evidence:[{
    packetId:"44444444-4444-4444-8444-444444444444",occurredAtUtc:"2026-09-29T12:30:00+00:00",evidenceKind:"blocking",
    sourceRunId:null,sourceSha256:"a".repeat(64),identitySha256:"b".repeat(64),sourceCutoffSha256:"c".repeat(64),
    sourceCutoffUtc:null,visibilityState:"partial",confidence:null,evidence:{ count:3,description:"<img src=x onerror=alert(1)>" },
  }] });
const scope={ target,range:"1h",compare:true };
async function withFetch(handler,run) {
  const previous=globalThis.fetch; clearApiCache(); globalThis.fetch=handler;
  try { await run(); } finally { clearApiCache(); globalThis.fetch=previous; }
}

test("prepared investigations freeze the precise window and immutable operation for retries",()=>{
  const mutable={ ...window }; const prepared=prepareIncident(mutable,7,"Title","Notes",operation,now);
  mutable.toUtc="2026-09-29T15:00:00Z";
  assert.equal(prepared.toUtc,window.toUtc); assert.ok(Object.isFrozen(prepared));
  assert.throws(()=>{ prepared.notes="Changed"; },TypeError);
  assert.equal(incidentUtc(window.toUtc),incidentUtc(window.toUtc.replace("Z","+00:00")));
  assert.equal(incidentUtc(window.toUtc)-incidentUtc(window.toUtc.replace("000002","000001")),1n);
});

test("window and text bounds reject future, excessive, invalid and unsafe draft values",()=>{
  for (const invalid of ["2026-02-29T12:00:00Z","2026-09-29T24:00:00Z","2026-09-29T12:00:00.0000001Z","2026-09-29T12:00:00+01:00"])
    assert.throws(()=>incidentUtc(invalid));
  assert.throws(()=>prepareIncident({ ...window,toUtc:"2026-10-01T12:00:00Z" },7,"Title","",operation,now),/past/);
  assert.throws(()=>prepareIncident({ ...window,fromUtc:"2026-09-21T12:00:00Z" },7,"Title","",operation,now),/seven days/);
  for (const [title,notes] of [[" ",""],["界".repeat(67),""],["Title","界".repeat(2731)],["Title","bad\u0001"],["Title","\ud800"]])
    assert.throws(()=>prepareIncident(window,7,title,notes,operation,now));
  assert.throws(()=>prepareIncident(window,7,"Title","\n".repeat(8192),operation,now),/16 KiB/);
  assert.equal(prepareIncident(window,7,"Title","line one\nline two\tvalue",operation,now).notes,"line one\nline two\tvalue");
});

test("saved projections bind target and thread and reject malformed or out-of-window evidence",()=>{
  assert.equal(parseSavedIncident(fixture(),target,thread).evidence.length,1);
  for (const change of [
    row=>row.targetId=thread,row=>row.threadId=operation,row=>row.extra="drift",row=>row.evidenceTruncated="false",
    row=>row.evidence[0].occurredAtUtc=row.toUtc,row=>row.evidence[0].sourceCutoffUtc="2026-10-01T00:00:00Z",
    row=>row.evidence[0].sourceSha256="bad",row=>row.evidence[0].confidence=2,row=>row.evidence.push({...row.evidence[0]}),
  ]) { const row=fixture(); change(row); assert.throws(()=>parseSavedIncident(row,target,thread)); }
  const large=fixture(); large.evidence[0].evidence={ text:"x".repeat(1048577) };
  assert.throws(()=>parseSavedIncident(large,target,thread),/bounds/);
});

test("save verification detects even one microsecond of window drift",()=>{
  const saved=parseSavedIncident(fixture(),target);
  assert.doesNotThrow(()=>verifySavedIncident(saved,command()));
  for (const change of [ { toUtc:window.toUtc.replace("000002","000003") },{ operationId:thread },{ notes:"Changed" },{ targetRevision:8 } ])
    assert.throws(()=>verifySavedIncident({ ...saved,...change },command()),/did not match/);
});

test("uncertain writes retry identical bodies and validate the eventual receipt",async()=>{
  const requests=[];
  await withFetch(async(url,options)=>{
    requests.push({ url,options }); return requests.length===1 ? new Response(null,{status:503})
      : new Response(JSON.stringify(fixture()),{ headers:{ "content-type":"application/json" } });
  },async()=>{
    const exact=command(),signal=new AbortController().signal;
    await assert.rejects(()=>saveIncident(target,exact,signal),error=>error instanceof IncidentRequestError&&error.retryable);
    assert.equal((await saveIncident(target,exact,signal)).threadId,thread);
    assert.equal(requests[0].options.body,requests[1].options.body); assert.equal(requests[1].options.method,"POST");
    assert.equal(requests[1].options.cache,"no-store"); assert.equal(new Headers(requests[1].options.headers).get("content-type"),"application/json");
    assert.equal(JSON.parse(requests[1].options.body).operationId,operation);
  });
});

test("invalid success responses stay uncertain, while conflicts are not retryable",async()=>{
  await withFetch(async()=>new Response(JSON.stringify({ ...fixture(),notes:"Different" })),async()=>{
    await assert.rejects(()=>saveIncident(target,command(),new AbortController().signal),error=>error instanceof IncidentRequestError&&error.retryable);
  });
  await withFetch(async()=>new Response(null,{status:409}),async()=>{
    await assert.rejects(()=>saveIncident(target,command(),new AbortController().signal),error=>error instanceof IncidentRequestError&&!error.retryable);
  });
});

test("saved reads use the target-bound no-store route and preserve cancellation",async()=>{
  let request;
  await withFetch(async(url,options)=>{request={url,options};return new Response(JSON.stringify(fixture()));},async()=>{
    assert.equal((await getSavedIncident(target,thread,new AbortController().signal)).threadId,thread);
    assert.equal(request.url,`/api/v1/observation-targets/${target}/incidents/saved/${thread}`); assert.equal(request.options.cache,"no-store");
    const controller=new AbortController(); controller.abort();
    await assert.rejects(()=>getSavedIncident(target,thread,controller.signal),error=>error.name==="AbortError");
  });
});

test("shareable incident URLs restore the saved window at microsecond precision",()=>{
  const saved=parseSavedIncident(fixture(),target); const href=savedIncidentHref(saved,scope);
  assert.equal(incidentSelection(href),thread);
  assert.deepEqual(overviewWindow(readOverviewScope(href),now),window);
  const narrow={ ...saved,fromUtc:"2026-09-29T12:00:00.000001Z",toUtc:"2026-09-29T12:00:00.000002Z" };
  assert.deepEqual(overviewWindow(readOverviewScope(savedIncidentHref(narrow,scope)),now),{fromUtc:narrow.fromUtc,toUtc:narrow.toUtc});
  assert.equal(incidentSelection("#/health?target="+target),undefined);
  assert.equal(incidentSelection("#/health?incident=bad"),null);
  assert.equal(incidentSelection(href+"&incident="+operation),null);
});

test("only active matching target grants unlock saving an investigation",()=>{
  const access={active:true,targetId:target,grantedRoles:["Operator","Viewer"],allTargetRoles:[],targetRoles:["Viewer"]};
  assert.equal(canSaveIncident(access,target),false); assert.equal(canSaveIncident({...access,targetRoles:["Operator"]},target),true);
  assert.equal(canSaveIncident({...access,targetRoles:["TargetAdministrator"]},target),true);
  assert.equal(canSaveIncident({...access,targetId:thread,targetRoles:["Operator"]},target),false);
  assert.equal(canSaveIncident({...access,active:false,targetRoles:["Operator"]},target),false);
});

const require=createRequire(import.meta.url);
async function compile(name,replacements={}) {
  const source=await readFile(new URL(`../src/features/incidents/${name}.tsx`,import.meta.url),"utf8");
  let javascript=(await transformWithOxc(source,`${name}.tsx`,{jsx:{runtime:"automatic"}})).code;
  for (const [from,to] of Object.entries({ "react":pathToFileURL(require.resolve("react")).href,"react/jsx-runtime":pathToFileURL(require.resolve("react/jsx-runtime")).href,...replacements }))
    javascript=javascript.replaceAll(JSON.stringify(from),JSON.stringify(to));
  return `data:text/javascript;base64,${Buffer.from(javascript).toString("base64")}`;
}
const reportActionsUrl=await compile("SavedIncidentReportActions",{
  "./incidentApi.ts":new URL("../src/features/incidents/incidentApi.ts",import.meta.url).href,
  "./savedIncidentReportApi.ts":new URL("../src/features/incidents/savedIncidentReportApi.ts",import.meta.url).href,
  "../reports/reportsApi.ts":new URL("../src/features/reports/reportsApi.ts",import.meta.url).href,
});
const viewUrl=await compile("SavedIncidentView",{"./SavedIncidentReportActions.tsx":reportActionsUrl}); const {SavedIncidentView}=await import(viewUrl);
const listUrl=await compile("SavedIncidentList",{
  "./incidentApi.ts":new URL("../src/features/incidents/incidentApi.ts",import.meta.url).href,
  "./incidentModel.ts":new URL("../src/features/incidents/incidentModel.ts",import.meta.url).href,
});
const workspaceUrl=await compile("IncidentWorkspace",{
  "./incidentApi.ts":new URL("../src/features/incidents/incidentApi.ts",import.meta.url).href,
  "./incidentModel.ts":new URL("../src/features/incidents/incidentModel.ts",import.meta.url).href,
  "./SavedIncidentView.tsx":viewUrl,"./SavedIncidentList.tsx":listUrl,"../overview/overviewModel.ts":new URL("../src/features/overview/overviewModel.ts",import.meta.url).href,
});
const {IncidentWorkspace}=await import(workspaceUrl);

test("rendered investigations escape notes and evidence and explain empty, partial and truncated captures",()=>{
  const render=incident=>renderToStaticMarkup(createElement(SavedIncidentView,{incident,formatTime:value=>value,href:"#/health",onClose:()=>{}}));
  const html=render(parseSavedIncident(fixture(),target));
  assert.match(html,/&lt;script&gt;Plain text/); assert.doesNotMatch(html,/<script>/); assert.doesNotMatch(html,/<img /);
  assert.match(html,/partial or unavailable coverage/); assert.match(html,/Confidence Unknown/);
  assert.match(html,/Generate investigation report/);
  const empty=render({ ...parseSavedIncident(fixture(),target),evidence:[] }); assert.match(empty,/does not mean the server was healthy/);
  const truncated=render({ ...parseSavedIncident(fixture(),target),evidenceTruncated:true }); assert.match(truncated,/not the entire window/);
});

test("saved report generation binds the thread receipt and retries an identical operation",async()=>{
  const saved=parseSavedIncident(fixture(),target),requests=[];
  const digest=await crypto.subtle.digest("SHA-256",new TextEncoder().encode(JSON.stringify({reportKind:"saved-investigation",threadId:thread})));
  const parameterDigest=Array.from(new Uint8Array(digest),value=>value.toString(16).padStart(2,"0")).join("");
  const receipt={runId:operation,targetId:target,targetRevision:7,kind:4,definitionVersion:1,snapshotUtc:"2026-09-29T15:00:00Z",expiresAtUtc:"2026-09-30T15:00:00Z",parameterDigest,state:"finalized"};
  await withFetch(async(url,options)=>{requests.push({url,options});return requests.length===1?new Response(null,{status:503}):new Response(JSON.stringify(receipt));},async()=>{
    await assert.rejects(()=>createSavedInvestigationReport(saved,operation,new AbortController().signal),error=>error instanceof IncidentRequestError&&error.retryable);
    assert.equal((await createSavedInvestigationReport(saved,operation,new AbortController().signal)).runId,operation);
    assert.equal(requests[0].options.body,requests[1].options.body);
    assert.deepEqual(JSON.parse(requests[1].options.body),{reportKind:"saved-investigation",operationId:operation,threadId:thread});
    assert.equal(requests[1].options.cache,"no-store");
  });
  for(const changed of [{targetId:thread},{targetRevision:8},{kind:2},{parameterDigest:"0".repeat(64)},{expiresAtUtc:"2026-09-30T15:00:00.000001Z"}])
    await withFetch(async()=>new Response(JSON.stringify({...receipt,...changed})),async()=>{
      await assert.rejects(()=>createSavedInvestigationReport(saved,operation,new AbortController().signal),error=>error instanceof IncidentRequestError&&error.retryable);
    });
});

test("rendered workspace gates save actions and handles invalid selection without a read",()=>{
  const render=extra=>renderToStaticMarkup(createElement(IncidentWorkspace,{targetId:target,targetRevision:7,window,scope,selectionHash:"#/health",formatTime:value=>value,canSave:true,...extra}));
  assert.match(render({}),/Open incident/); assert.doesNotMatch(render({}),/<button[^>]+disabled/);
  assert.match(render({window:undefined}),/<button[^>]+disabled/);
  assert.doesNotMatch(render({canSave:false}),/>Open incident</); assert.match(render({canSave:false}),/Operator or Target Administrator/);
  assert.match(render({canSave:false}),/Saved investigations/);
  assert.match(render({selectionHash:"#/health?incident=bad"}),/incident link is invalid/);
});

const summary=(index=0)=>({targetId:target,targetRevision:7,threadId:`22222222-2222-4222-8222-${(100-index).toString(16).padStart(12,"0")}`,
  ...window,savedAtUtc:"2026-09-29T14:00:00Z",title:`Investigation ${index}`,evidenceCount:0,evidenceTruncated:false});
const summaryPage=()=>({targetId:target,snapshotUtc:"2026-09-29T15:00:00Z",items:Array.from({length:20},(_,index)=>summary(index)),hasMore:true,nextCursor:"payload.signature"});
test("summary pages validate dense descending keys, target scope and bounded metadata",()=>{
  const page=summaryPage(); assert.equal(parseSavedIncidentPage(page,target).items.length,20);
  for(const change of [row=>row.targetId=thread,row=>row.items[0].targetId=thread,row=>row.items[0].notes="Unexpected secret",
    row=>row.items[0].evidenceCount=201,row=>row.items.reverse(),row=>row.items[1]=row.items[0],row=>row.items.pop(),
    row=>row.nextCursor=null,row=>row.nextCursor="x".repeat(513),row=>row.items[0].savedAtUtc="2026-09-29T16:00:00Z"]){
    const invalid=summaryPage();change(invalid);assert.throws(()=>parseSavedIncidentPage(invalid,target));
  }
  const prior=parseSavedIncidentPage(page,target);
  const next={...page,items:[summary(20)],hasMore:false,nextCursor:null};
  assert.equal(parseSavedIncidentPage(next,target,20,prior).items.length,1);
  assert.throws(()=>parseSavedIncidentPage({...next,snapshotUtc:"2026-09-29T15:00:00.000001Z"},target,20,prior));
  assert.throws(()=>parseSavedIncidentPage({...next,items:[summary(19)]},target,20,prior));
});

test("summary API pages use no-store and preserve the first snapshot; stale cursors request refresh",async()=>{
  const requests=[];
  await withFetch(async(url,options)=>{
    requests.push({url,options});return new Response(JSON.stringify(requests.length===1?summaryPage():{...summaryPage(),items:[summary(20)],hasMore:false,nextCursor:null}));
  },async()=>{
    const first=await listSavedIncidents(target,new AbortController().signal);
    assert.equal((await listSavedIncidents(target,new AbortController().signal,first)).items[0].threadId,summary(20).threadId);
    assert.match(requests[1].url,/cursor=payload.signature/);assert.equal(requests[1].options.cache,"no-store");
  });
  await withFetch(async()=>new Response(null,{status:409}),async()=>{
    await assert.rejects(()=>listSavedIncidents(target,new AbortController().signal),error=>error instanceof IncidentRequestError&&!error.retryable&&/Refresh/.test(error.message));
  });
});
