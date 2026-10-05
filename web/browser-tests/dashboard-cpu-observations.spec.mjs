import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {cpuObservationBinding as binding,cpuObservationAlpha as alpha,cpuObservationFixtureReceipt,installCpuObservationHistoryFixture} from '../tools/cpu-observation-history-fixture.mjs';
const sessions=new WeakMap(),panel=p=>p.getByRole('region',{name:'CPU component observations',exact:true}),exact=p=>panel(p).getByRole('region',{name:'Selected CPU observation',exact:true});
test.beforeEach(async({page})=>{
 await page.clock.install({time:new Date('2026-10-04T12:00:00Z')});await page.context().addCookies(['dashboardRefactor','resourceInspectionFixture'].map(name=>({name,value:'1',url:'http://127.0.0.1:4322'})).concat([{name:'chartReferenceRole',value:'viewer',url:'http://127.0.0.1:4322'}]));
 const session=await installCpuObservationHistoryFixture(page,{origin:'http://127.0.0.1:4322',allowOtherApi:true,adjusted:true,componentGap:true});sessions.set(page,session);page.on('pageerror',e=>session.unexpected.push(e.message));
});
test.afterEach(async({page})=>expect(sessions.get(page).unexpected).toEqual([]));
for(const width of [320,1440])test('Resources composes independent CPU observations with preserved raw capture history at '+width,async({page})=>{
 await page.setViewportSize({width,height:1000});const pin=cpuObservationFixtureReceipt();await page.goto('/#/resources?'+new URLSearchParams({target:alpha,range:'custom',from:binding.fromUtc,to:binding.toUtc,compare:'1',cpuObservationRun:pin.runId,cpuObservationRevision:'1'}));
 await expect(exact(page)).toContainText('ProviderAdjusted');await expect(page.getByRole('region',{name:'CPU paired capture history',exact:true})).toBeVisible();await expect(panel(page).getByRole('region',{name:'CPU comparison observations',exact:true})).toContainText('3 received observations');
 await expect(page.getByRole('region',{name:'CPU component collection',exact:true})).toContainText('Collection disabled');await expect(page.getByRole('button',{name:'Enable CPU component collection',exact:true})).toHaveCount(0);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);const axe=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(axe.violations).toEqual([]);
 const folder=fileURLToPath(new URL('../../artifacts/dashboard-cpu-observations-2026-10-05/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});await panel(page).screenshot({path:folder+`cpu-components-${width}.png`});await page.reload();await expect(exact(page)).toContainText(pin.runId);
});
test('Live CPU browsing freezes the received shared window and exact run without resetting legacy pins',async({page})=>{
 await page.goto('/#/resources?'+new URLSearchParams({target:alpha,range:'1h',compare:'1',cpuCaptureRun:'bad',cpuCaptureRevision:'01'}));await expect(exact(page)).toContainText('12%');await expect(page.getByRole('region',{name:'Resources evidence status',exact:true})).toBeVisible();
 const fixed=new URLSearchParams((await page.getByRole('link',{name:'Open fixed resource investigation',exact:true}).getAttribute('href')).split('?')[1]);
 await panel(page).getByText('Browse CPU observations',{exact:true}).click();await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('range')).toBe('custom');
 const params=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);expect(params.get('from')).toBe(fixed.get('from'));expect(params.get('to')).toBe(fixed.get('to'));expect(params.get('cpuObservationRun')).toBeTruthy();expect(params.get('cpuCaptureRun')).toBe('bad');await expect(page.getByRole('region',{name:'Selected CPU capture receipt',exact:true})).toContainText('identity is invalid');
});
test('responsive component chart drag changes the shared window and retains exact observation intent',async({page})=>{
 await page.setViewportSize({width:390,height:1000});const selected=cpuObservationFixtureReceipt();await page.goto('/#/resources?'+new URLSearchParams({target:alpha,range:'custom',from:binding.fromUtc,to:binding.toUtc,cpuObservationRun:selected.runId,cpuObservationRevision:'1'}));await expect(exact(page)).toContainText('12%');
 const chart=panel(page).getByRole('img',{name:'SQL-process CPU independent observations and gaps'});await chart.scrollIntoViewIfNeeded();const b=await chart.boundingBox(),left=b.x+42,right=b.x+b.width-12;
 await page.mouse.move(left+(right-left)*.2,b.y+110);await page.mouse.down();await page.mouse.move(left+(right-left)*.8,b.y+110,{steps:6});await page.mouse.up();
 await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('from')).not.toBe(binding.fromUtc);const p=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);expect(p.get('cpuObservationRun')).toBe(selected.runId);expect(p.get('cpuObservationRevision')).toBe('1');expect(Date.parse(p.get('to'))-Date.parse(p.get('from'))).toBeLessThan(3600000);
});
