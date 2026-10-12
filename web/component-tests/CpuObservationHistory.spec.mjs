import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {cpuObservationBinding as binding,cpuObservationAlpha as alpha,cpuObservationBeta as beta,cpuObservationFixtureReceipt,installCpuObservationHistoryFixture,cpuObservationHistoryJson} from '../tools/cpu-observation-history-fixture.mjs';
const sessions=new WeakMap(),panel=p=>p.getByRole('region',{name:'CPU component observations',exact:true}),exact=p=>p.getByRole('region',{name:'Selected CPU observation',exact:true});
const url=(extra={})=>'/component-tests/cpu-observation-history.html#resources?'+new URLSearchParams({target:alpha,range:'custom',from:binding.fromUtc,to:binding.toUtc,...extra});
async function mount(p,extra={}){await p.goto(url(extra));await expect(exact(p)).toContainText('12%');}
test.beforeEach(async({page})=>{await page.clock.setFixedTime(new Date('2026-10-04T12:00:00Z'));await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));const s=await installCpuObservationHistoryFixture(page,{adjusted:true,componentGap:true});sessions.set(page,s);page.on('pageerror',e=>s.unexpected.push(e.message));});
test.afterEach(async({page})=>expect(sessions.get(page).unexpected).toEqual([]));
for(const width of [320,1440])test('independent component charts, quality and exact intervals remain accessible at '+width,async({page})=>{
 await page.setViewportSize({width,height:1000});await mount(page);await expect(panel(page)).toContainText('ProviderAdjusted');await expect(panel(page)).toContainText('134000000000000001');
 await expect(panel(page).getByRole('img',{name:'SQL-process CPU independent observations and gaps'})).toBeVisible();await expect(panel(page).locator('[data-cpu-gap]')).toHaveCount(2);
 await expect(panel(page).locator('[data-quality="provider_adjusted"]')).toHaveCount(2);
 for(const plot of await panel(page).locator('.chart-plot-scroll').all()) expect(await plot.evaluate(e=>e.scrollWidth-e.clientWidth)).toBeLessThanOrEqual(1);
 for(const point of await panel(page).locator('[data-quality]').all()) { const box=await point.boundingBox(), plot=await point.evaluate(e=>{const r=e.closest('svg').getBoundingClientRect();return {x:r.x,width:r.width};});expect(box.x).toBeGreaterThanOrEqual(plot.x);expect(box.x+box.width).toBeLessThanOrEqual(plot.x+plot.width); }
 await panel(page).getByText('Browse CPU observations',{exact:true}).click();await expect(panel(page).getByRole('region',{name:'CPU observations table',exact:true})).toBeVisible();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const axe=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(axe.violations).toEqual([]);
 await panel(page).getByText('Browse CPU observations',{exact:true}).click();
 const folder=fileURLToPath(new URL('../../artifacts/dashboard-cpu-observations-2026-10-05/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});
 await panel(page).screenshot({path:folder+`cpu-components-mounted-${width}.png`});
});
test('exact URL run omitted from page one survives reload and local paging',async({page})=>{
 const s=sessions.get(page);s.backend.settings.count=101;const old=cpuObservationFixtureReceipt(100);await mount(page,{cpuObservationRun:old.runId,cpuObservationRevision:'1'});await expect(exact(page)).toContainText(old.runId);
 await panel(page).getByRole('button',{name:'Next CPU observations page',exact:true}).click();await expect(panel(page).getByRole('region',{name:'CPU observations',exact:true})).toContainText('101 received observations');
 const n=s.requests.length;await panel(page).getByRole('button',{name:'First CPU observations page',exact:true}).click();await panel(page).getByRole('button',{name:'Next CPU observations page',exact:true}).click();expect(s.requests).toHaveLength(n);
 await page.reload();await expect(exact(page)).toContainText(old.runId);
});
test('malformed pin fences detail without selecting a default and survives window changes',async({page})=>{
 await page.goto(url({cpuObservationRun:'bad',cpuObservationRevision:'01'}));await expect(exact(page)).toContainText('identity is invalid');const s=sessions.get(page);expect(s.requests.some(r=>r.selection)).toBe(false);
 await page.getByRole('button',{name:'Change shared window one microsecond',exact:true}).click();await expect(exact(page)).toContainText('identity is invalid');expect(s.requests.some(r=>r.selection)).toBe(false);
 await page.getByRole('button',{name:'Clear CPU observation selection',exact:true}).click();await expect(exact(page)).toContainText('12%');
});
test('comparison ordinary failure does not erase current evidence; access refusal purges all three sources and requires explicit retry',async({page})=>{
 await mount(page);const s=sessions.get(page);s.setHandler(async(call,route)=>{if(!call.selection&&call.binding.fromUtc!==binding.fromUtc){await cpuObservationHistoryJson(route,{},409);return true;}});
 await page.getByRole('button',{name:'Refresh CPU comparison observations',exact:true}).click();await expect(page.getByRole('region',{name:'CPU comparison observations',exact:true})).toContainText('changed or expired');await expect(exact(page)).toContainText('12%');
 s.setHandler(async(call,route)=>{if(call.selection){await cpuObservationHistoryJson(route,{},403);return true;}});await page.getByRole('button',{name:'Refresh CPU observation',exact:true}).click();await expect(panel(page)).toContainText('All received current, comparison and exact evidence was removed');await expect(panel(page)).not.toContainText('134000000000000001');
 const n=s.requests.length;await page.getByRole('button',{name:'Refresh workspace',exact:true}).click();expect(s.requests).toHaveLength(n);s.setHandler(undefined);await page.getByRole('button',{name:'Retry CPU observation access',exact:true}).click();await expect(exact(page)).toContainText('12%');
});
test('held prior-target completion cannot replace the new target after an ABA visit',async({page})=>{
 await mount(page);const s=sessions.get(page);let release,started;const gate=new Promise(r=>release=r),begun=new Promise(r=>started=r);let once=false;
 s.setHandler(async(call,route)=>{if(call.selection&&!once){once=true;started();await gate;try{await cpuObservationHistoryJson(route,{},403);}catch{}return true;}});
 await page.getByRole('button',{name:'Refresh CPU observation',exact:true}).click();await begun;await page.getByRole('combobox',{name:'Selected server',exact:true}).selectOption(beta);await expect(exact(page)).toContainText('12%');await page.getByRole('combobox',{name:'Selected server',exact:true}).selectOption(alpha);await expect(exact(page)).toContainText('12%');release();await expect(page.getByRole('button',{name:'Retry CPU observation access',exact:true})).toHaveCount(0);
});
test('collection toggle uses fresh state and unknown outcome requires readback without replay',async({page})=>{
 await mount(page);const s=sessions.get(page),control=page.getByRole('region',{name:'CPU component collection',exact:true});await expect(control).toContainText('Collection disabled');
 await control.getByRole('button',{name:'Enable CPU component collection',exact:true}).click();await expect(control).toContainText('Collection enabled');expect(s.collectionCalls.filter(c=>c.method==='PUT')[0].body).toEqual({expectedTargetRevision:1,expectedEnabled:false,enabled:true});
 let writes=0;await page.route('**/resources/cpu/collection',async route=>{if(route.request().method()==='PUT'){writes++;await route.abort('failed');}else await route.fallback();});
 await control.getByRole('button',{name:'Disable CPU component collection',exact:true}).click();await expect(control).toContainText('outcome is unknown');await expect(control.getByRole('button',{name:'Enable CPU component collection',exact:true})).toBeDisabled();expect(writes).toBe(1);
 await control.getByRole('button',{name:'Reload CPU collection state',exact:true}).click();await expect(control).toContainText('Collection enabled');expect(writes).toBe(1);
 await page.getByRole('combobox',{name:'Authority context',exact:true}).selectOption('grant-b');await expect(control).toContainText('A Target Administrator');await expect(control.getByRole('button',{name:'Disable CPU component collection',exact:true})).toHaveCount(0);
});
