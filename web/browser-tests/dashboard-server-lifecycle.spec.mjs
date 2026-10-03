import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {resolve} from 'node:path';
import {serverLifecycleBinding as binding,serverLifecycleFixtureEvent} from '../tools/server-lifecycle-history-fixture.mjs';

const origin='http://127.0.0.1:4322';
const event=serverLifecycleFixtureEvent(0,binding);
const identity=`server-lifecycle:${event.eventId}:${event.sampleTargetRevision}:${event.afterRunId}`;
const panel=page=>page.getByRole('region',{name:'Server lifecycle history',exact:true});
const selected=page=>page.getByRole('region',{name:'Selected server lifecycle event',exact:true});
const params=page=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const hash=(selection=false)=>'#/health?'+new URLSearchParams({target:binding.targetId,range:'custom',from:binding.fromUtc,to:binding.toUtc,
  ...(selection?{healthSection:'summary',timelineAt:event.detectedAtUtc.replace(/0\+00:00$/u,'Z'),timelineEvent:identity}:{})});
async function prepare(page){
  await page.context().addCookies(['dashboardRefactor','serverLifecycleHistoryFixture'].map(name=>({name,value:'1',url:origin})));
  await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
}
async function open(page,selection=false){await prepare(page);await page.goto('/'+hash(selection));await expect(panel(page)).toContainText('3 retained events');}
async function proof(page){await expect(selected(page)).toContainText('Sampled restart detected');await selected(page).getByText('Immutable before and after engine evidence',{exact:true}).click();
  await expect(selected(page).getByRole('region',{name:'Before immutable engine evidence',exact:true})).toBeVisible();}

test('server lifecycle selection restores its full immutable tuple through reload and browser history',async({page})=>{
  await open(page);await panel(page).getByText('Browse server lifecycle events',{exact:true}).click();
  await panel(page).getByRole('button',{name:`Inspect server lifecycle event ${event.eventId}: Sampled restart detected`,exact:true}).click();
  await expect(selected(page)).toContainText(event.afterRunId);
  expect(params(page).get('timelineEvent')).toBe(identity);
  const saved=page.url();await page.reload();await expect(selected(page)).toContainText(event.afterRunId);expect(page.url()).toBe(saved);
  await panel(page).getByRole('button',{name:'Clear selected server lifecycle event',exact:true}).click();await expect(selected(page)).toHaveCount(0);
  await page.goBack();await expect(selected(page)).toContainText(event.afterRunId);
  await page.goForward();await expect(selected(page)).toHaveCount(0);
});

test('exact server lifecycle proof survives list overflow and unavailable core dashboard analytics',async({page})=>{
  await page.route('**/api/v1/overview?*',route=>route.fulfill({status:503,contentType:'application/json',body:'{"detail":"PRIVATE_PROVIDER_ERROR"}'}));
  await page.route('**/lifecycle/history?*',route=>route.fulfill({status:422,contentType:'application/json',body:'{"detail":"PRIVATE_PROVIDER_ERROR"}'}));
  await prepare(page);await page.goto('/'+hash(true));await proof(page);
  await expect(panel(page)).toContainText('Choose a narrower shared window');
  await expect(selected(page)).toContainText('9223372036854775807');
  await expect(selected(page)).toContainText('.1234567');
  await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_ERROR');
  await expect(page.locator('.dashboard-timeline-selected-evidence')).toContainText('SQL responding instance restart detected');
});

test('timeline category filtering preserves the exact lifecycle selection and restores its event focus',async({page})=>{
  await open(page,true);await expect(selected(page)).toContainText(event.afterRunId);
  const saved=page.url(),timeline=page.locator('.dashboard-timeline');
  await timeline.getByRole('combobox',{name:'Timeline event category',exact:true}).selectOption('deadlock');
  expect(page.url()).toBe(saved);await expect(selected(page)).toContainText(event.afterRunId);
  await timeline.getByRole('button',{name:'Show selected timeline event',exact:true}).click();
  await expect(timeline.getByRole('combobox',{name:'Timeline event category',exact:true})).toHaveValue('all');
  await expect(timeline.locator('button[aria-pressed="true"]')).toBeFocused();
  expect(page.url()).toBe(saved);
});

test('malformed server lifecycle tuple never substitutes a sampled event',async({page})=>{
  await prepare(page);await page.goto('/'+hash(true).replace(encodeURIComponent(identity),encodeURIComponent(identity.replace(':1:',':01:'))));
  await expect(panel(page)).toContainText('requested server lifecycle event is invalid');
  await expect(selected(page)).toHaveCount(0);
});

test('denied server lifecycle exact read purges protected receipts and requires explicit recovery',async({page})=>{
  await open(page,true);await expect(selected(page)).toContainText(event.afterRunId);
  let refuse=true;await page.route('**/lifecycle/history/**',route=>refuse
    ?route.fulfill({status:403,contentType:'application/json',body:'{"detail":"PRIVATE_PROVIDER_ERROR"}'}) :route.continue());
  await selected(page).getByRole('button',{name:'Refresh selected server lifecycle event',exact:true}).click();
  await expect(panel(page)).toContainText('Protected pages and selected evidence were removed');
  await expect(selected(page)).toHaveCount(0);await expect(panel(page)).not.toContainText(event.afterRunId);
  refuse=false;await panel(page).getByRole('button',{name:'Retry server lifecycle history access',exact:true}).click();
  await expect(selected(page)).toContainText(event.afterRunId);
});

test('inspecting a sampled lifecycle event in Live fixes its received window and retains selection',async({page})=>{
  await prepare(page);await page.goto('/#/health?'+new URLSearchParams({target:binding.targetId,range:'1h'}));
  await expect(panel(page)).toContainText('3 retained events');
  await panel(page).getByText('Browse server lifecycle events',{exact:true}).click();
  await expect.poll(()=>params(page).get('range')).toBe('custom');
  await panel(page).getByRole('button',{name:/^Inspect server lifecycle event/}).first().click();
  await expect(selected(page)).toContainText('Sampled restart detected');
  expect(params(page).get('timelineEvent')).toMatch(/^server-lifecycle:/u);
});

for(const width of [320,390,768,1440])test(`sampled lifecycle evidence is accessible and contained in dark layout at ${width}px`,async({page},info)=>{
  await page.setViewportSize({width,height:1000});await open(page,true);await proof(page);
  await panel(page).getByText('Browse server lifecycle events',{exact:true}).click();
  const review=page.getByRole('button',{name:'Review selected server lifecycle evidence',exact:true});await review.focus();await review.press('Enter');
  await expect(page.getByRole('region',{name:'Server lifecycle evidence',exact:true})).toBeFocused();
  const summary=selected(page).getByText('Immutable before and after engine evidence',{exact:true});await summary.focus();await expect(summary).toBeFocused();
  const focus=await summary.evaluate(element=>{const s=getComputedStyle(element),r=element.getBoundingClientRect();return{outline:s.outlineStyle,width:parseFloat(s.outlineWidth),height:r.height};});
  expect(focus.outline).not.toBe('none');expect(focus.width).toBeGreaterThanOrEqual(2);expect(focus.height).toBeGreaterThanOrEqual(44);
  const layout=await page.evaluate(()=>({page:document.documentElement.scrollWidth,viewport:innerWidth}));expect(layout.page).toBeLessThanOrEqual(layout.viewport+1);
  const scan=await new AxeBuilder({page}).include('.server-lifecycle-history').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(scan.violations).toEqual([]);
  const folder=resolve('../artifacts/dashboard-server-lifecycle-ui-2026-10-03/screenshots');await mkdir(folder,{recursive:true});
  await panel(page).screenshot({path:resolve(folder,`server-lifecycle-panel-${width}.png`)});
  await page.screenshot({path:resolve(folder,`server-lifecycle-dashboard-${width}.png`),fullPage:true});
  await info.attach('source-context',{body:'Synthetic lifecycle DTOs and local fixture API; no connected SQL Server qualification.',contentType:'text/plain'});
});
