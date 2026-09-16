import assert from "node:assert/strict";
import test from "node:test";
import { getActivitySnapshot, getBlockingHistoryPage } from "../src/features/activity/activityApi.ts";
const target = "11111111-1111-4111-8111-111111111111";
test("history continuation preserves the original UTC bounds and encodes opaque cursors", async () => {
 const original = globalThis.fetch;
 const window = {fromUtc:"2026-09-04T12:00:00Z",toUtc:"2026-09-05T12:00:00Z"};
 const urls=[];
 try {
  globalThis.fetch=async url=>{urls.push(new URL(String(url),"https://lab.invalid")); return new Response(JSON.stringify({instanceId:target,repositoryTimeUtc:window.toUtc,...window,items:[],nextCursor:urls.length===1?"opaque+/=token":null}),{headers:{"content-type":"application/json"}});};
  const first=await getBlockingHistoryPage(target,window,new AbortController().signal);
  const last=await getBlockingHistoryPage(target,window,new AbortController().signal,first.nextCursor);
  assert.equal(last.nextCursor,undefined);
  assert.equal(urls[1].searchParams.get("cursor"),"opaque+/=token");
  for(const url of urls){assert.equal(url.searchParams.get("limit"),"25"); assert.equal(url.searchParams.get("fromUtc"),window.fromUtc); assert.equal(url.searchParams.get("toUtc"),window.toUtc);}
  assert.equal(urls[0].searchParams.has("cursor"),false);
 }finally{globalThis.fetch=original;}
});
test("history page failures are sanitized and aborted requests reject",async()=>{
 const original=globalThis.fetch;
 const window={fromUtc:"2026-09-05T11:00:00Z",toUtc:"2026-09-05T12:00:00Z"};
 try {
  globalThis.fetch=async()=>new Response("private provider detail",{status:500});
  await assert.rejects(getBlockingHistoryPage(target,window,new AbortController().signal,"cursor"),/failed safely/);
  const controller=new AbortController();controller.abort();
  globalThis.fetch=async()=>{throw new DOMException("Aborted","AbortError");};
  await assert.rejects(getBlockingHistoryPage(target,window,controller.signal),{name:"AbortError"});
 }finally{globalThis.fetch=original;}
});
test("one unavailable activity route preserves four independent evidence sections", async () => {
 const original = globalThis.fetch;
 try {
  globalThis.fetch = async url => String(url).includes("/requests?") ? new Response(null, {status:503}) : new Response(JSON.stringify({instanceId:target,repositoryTimeUtc:"2026-09-05T12:00:00Z",evidence:null,items:[]}),{headers:{"content-type":"application/json"}});
  const result = await getActivitySnapshot(target,new AbortController().signal);
  assert.equal(result.requests,undefined);
  for(const key of ["sessions","waits","blocking","history"]) assert.deepEqual(result[key].items,[]);
  assert.equal(result.errors.length,1);
  assert.match(result.errors[0],/^Requests:/u);
 } finally {globalThis.fetch=original;}
});
test("aborting activity loading rejects instead of displaying section failures", async () => {
 const original = globalThis.fetch;
 const controller=new AbortController();controller.abort();
 try {
  globalThis.fetch=async()=>{throw new DOMException("Aborted","AbortError");};
  await assert.rejects(getActivitySnapshot(target,controller.signal),{name:"AbortError"});
 } finally {globalThis.fetch=original;}
});
test("blocking history requests stay within the selected bounded UTC window",async()=>{
 const original=globalThis.fetch;
 let historyUrl;
 try {
  globalThis.fetch=async url=>{if(String(url).includes("blocking/history"))historyUrl=new URL(String(url),"https://lab.invalid");return new Response(JSON.stringify({instanceId:target,repositoryTimeUtc:"2026-09-05T12:00:00Z",evidence:null,items:[]}),{headers:{"content-type":"application/json"}});};
  await getActivitySnapshot(target,new AbortController().signal,24);
  assert.equal(Date.parse(historyUrl.searchParams.get("toUtc"))-Date.parse(historyUrl.searchParams.get("fromUtc")),24*3600000);
  await assert.rejects(getActivitySnapshot(target,new AbortController().signal,25),/Invalid blocking history window/u);
 }finally{globalThis.fetch=original;}
});
