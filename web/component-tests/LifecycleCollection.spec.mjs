import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
const alpha='11111111-1111-4111-8111-000000000001',beta='11111111-1111-4111-8111-000000000002';
const panel=p=>p.getByRole('region',{name:'Server lifecycle collection',exact:true});
const read=p=>panel(p).getByRole('button',{name:'Read current lifecycle state',exact:true});
const enable=p=>panel(p).getByRole('button',{name:'Enable lifecycle collection',exact:true});
const sessions=new WeakMap();
async function json(route,value,status=200){await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(value)});}
test.beforeEach(async({page})=>{
 const session={calls:[],errors:[],handler:undefined,states:new Map()};sessions.set(page,session);page.on('pageerror',e=>session.errors.push(e.message));
 await page.route('**/api/**',async route=>{
  const url=new URL(route.request().url()),match=/^\/api\/v1\/observation-targets\/([a-f0-9-]{36})\/lifecycle\/collection$/u.exec(url.pathname);
  if(!match){session.errors.push('Unexpected API '+url.pathname);await route.abort();return;}
  const method=route.request().method(),body=method==='PUT'?route.request().postDataJSON():undefined,targetId=match[1],targetRevision=method==='GET'?Number(url.searchParams.get('targetRevision')):body.expectedTargetRevision;
  const call={method,body,targetId,targetRevision};session.calls.push(call);
  const key=targetId+'|'+targetRevision;let state=session.states.get(key)??{targetId,targetRevision,scheduleRevision:1,collectorEnabled:false};
  if(await session.handler?.(call,route,state))return;
  if(method==='PUT'){if(body.expectedScheduleRevision!==state.scheduleRevision||body.expectedEnabled!==state.collectorEnabled){await json(route,{},409);return;}state={...state,scheduleRevision:state.scheduleRevision+(state.collectorEnabled===body.enabled?0:1),collectorEnabled:body.enabled};session.states.set(key,state);}
  await json(route,state);
 });
});
test.afterEach(async({page})=>expect(sessions.get(page).errors).toEqual([]));
async function mount(page){await page.goto('/component-tests/lifecycle-collection.html');await expect(panel(page)).toContainText('Collection disabled · schedule revision 1');}
test('default-off control sends exact CAS state, prevents overlapping writes and reports confirmed state',async({page})=>{
 await mount(page);const s=sessions.get(page);expect(s.calls.filter(c=>c.method==='PUT')).toHaveLength(0);
 let release,started;const gate=new Promise(r=>release=r),begun=new Promise(r=>started=r);
 s.handler=async(call,route,state)=>{if(call.method==='PUT'){started();await gate;await json(route,{...state,collectorEnabled:true,scheduleRevision:2});return true;}};
 await enable(page).evaluate(button=>{button.click();button.click();});await begun;
 await expect(panel(page)).toContainText('outcome is not confirmed');await expect(read(page)).toBeDisabled();expect(s.calls.filter(c=>c.method==='PUT')).toHaveLength(1);
 expect(s.calls.at(-1).body).toEqual({expectedTargetRevision:1,expectedScheduleRevision:1,expectedEnabled:false,enabled:true});
 release();await expect(panel(page)).toContainText('Collection enabled · schedule revision 2');await expect(panel(page)).toContainText('Retained history is unchanged');
});
test('stale schedule refuses another write until explicit authoritative read',async({page})=>{
 await mount(page);const s=sessions.get(page);s.states.set(alpha+'|1',{targetId:alpha,targetRevision:1,scheduleRevision:9,collectorEnabled:false});
 await enable(page).click();await expect(panel(page)).toContainText('target or collection schedule changed');await expect(enable(page)).toBeDisabled();expect(s.calls.filter(c=>c.method==='GET')).toHaveLength(1);
 await read(page).click();await expect(panel(page)).toContainText('schedule revision 9');await enable(page).click();await expect(panel(page)).toContainText('Collection enabled · schedule revision 10');expect(s.calls.at(-1).body.expectedScheduleRevision).toBe(9);
});
test('uncertain write requires readback and never retries even if server applied it',async({page})=>{
 await mount(page);const s=sessions.get(page);s.handler=async(call,route,state)=>{if(call.method==='PUT'){s.states.set(alpha+'|1',{...state,scheduleRevision:2,collectorEnabled:true});await route.abort('failed');return true;}};
 await enable(page).click();await expect(panel(page)).toContainText('outcome is unknown');await expect(enable(page)).toBeDisabled();expect(s.calls.filter(c=>c.method==='PUT')).toHaveLength(1);expect(s.calls.filter(c=>c.method==='GET')).toHaveLength(1);
 await read(page).click();await expect(panel(page)).toContainText('Collection enabled · schedule revision 2');expect(s.calls.filter(c=>c.method==='PUT')).toHaveLength(1);
});
test('read denial purges current state and write denial remains fenced until authority changes',async({page})=>{
 await mount(page);const s=sessions.get(page);s.handler=async(call,route)=>{await json(route,{},403);return true;};
 await read(page).click();await expect(panel(page)).toContainText('access was denied');await expect(panel(page)).not.toContainText('schedule revision 1');await expect(enable(page)).toBeDisabled();
 s.handler=undefined;await read(page).click();await expect(enable(page)).toBeEnabled();
 s.handler=async(call,route)=>{if(call.method==='PUT'){await json(route,{},403);return true;}};
 await enable(page).click();await expect(enable(page)).toHaveCount(0);await read(page).click();await expect(panel(page)).toContainText('Collection disabled');await expect(enable(page)).toHaveCount(0);
 s.handler=undefined;await page.getByRole('combobox',{name:'Authority context',exact:true}).selectOption('admin-b');await expect(enable(page)).toBeEnabled();
 await page.getByRole('combobox',{name:'Authority context',exact:true}).selectOption('viewer');await expect(panel(page)).toContainText('Collection disabled');await expect(enable(page)).toHaveCount(0);
});
for(const context of ['target','revision','authority'])test(`late ${context} read cannot overwrite a new context or an ABA return`,async({page})=>{
 await mount(page);const s=sessions.get(page);let release,started;const gate=new Promise(r=>release=r),begun=new Promise(r=>started=r);let held=false;
 s.handler=async(call,route,state)=>{if(!held&&call.method==='GET'){held=true;started();await gate;try{await json(route,{...state,scheduleRevision:99,collectorEnabled:true});}catch{}return true;}};
 await read(page).click();await begun;
 const chooser=page.getByRole('combobox',{name:context==='target'?'Selected server':context==='revision'?'Target revision':'Authority context',exact:true});
 await chooser.selectOption(context==='target'?beta:context==='revision'?'2':'admin-b');await expect(panel(page)).toContainText('Collection disabled · schedule revision 1');
 await chooser.selectOption(context==='target'?alpha:context==='revision'?'1':'admin-a');await expect(panel(page)).toContainText('Collection disabled · schedule revision 1');
 release();await expect(panel(page)).not.toContainText('schedule revision 99');await expect(enable(page)).toBeEnabled();
});
test('late write result is discarded after unmount and remount reads fresh state',async({page})=>{
 await mount(page);const s=sessions.get(page);let release,started;const gate=new Promise(r=>release=r),begun=new Promise(r=>started=r);
 s.handler=async(call,route,state)=>{if(call.method==='PUT'){started();await gate;try{await json(route,{...state,collectorEnabled:true,scheduleRevision:2});}catch{}return true;}};
 await enable(page).click();await begun;await page.getByRole('button',{name:'Unmount control',exact:true}).click();release();await expect(panel(page)).toHaveCount(0);
 await page.getByRole('button',{name:'Mount control',exact:true}).click();await expect(panel(page)).toContainText('Collection disabled · schedule revision 1');expect(s.calls.filter(c=>c.method==='PUT')).toHaveLength(1);
});
for(const width of [320,1440])test(`responsive controls retain 44px targets and scoped accessibility at ${width}`,async({page})=>{
 await page.setViewportSize({width,height:1000});await mount(page);await expect(panel(page)).toContainText('Exact occurrence time, cause, physical AG failover and uninterrupted coverage are not established');
 for(const button of await panel(page).getByRole('button').all()){const box=await button.boundingBox();expect(box.height).toBeGreaterThanOrEqual(44);expect(box.width).toBeGreaterThanOrEqual(44);}
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const axe=await new AxeBuilder({page}).include('.lifecycle-collection-control').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(axe.violations).toEqual([]);
 const folder=fileURLToPath(new URL('../../artifacts/dashboard-lifecycle-collection-2026-10-05/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});await panel(page).screenshot({path:folder+`lifecycle-collection-${width}.png`});
});
